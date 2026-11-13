using System.Globalization;
using System.Text;
using KBEA.Core.Models;
using Microsoft.Data.Sqlite;

namespace KBEA.Core.Knowledge;

/// <summary>
/// 检索引擎：L0 规则解析 → L2 分面预过滤 → L3 稀疏排序 → L4 版本簇展开。
/// 数据源为各空间的 catalog JSON（内存投影）与 SQLite FTS5 索引。
/// </summary>
public sealed class KnowledgeSearchService
{
    private readonly string _metadataPath;
    private readonly CatalogStore _catalog;
    private readonly QueryParser _parser = new();

    public KnowledgeSearchService(string metadataPath)
    {
        _metadataPath = Path.GetFullPath(metadataPath);
        _catalog = new CatalogStore(_metadataPath);
    }

    /// <summary>
    /// 打开检索库连接。WAL + busy_timeout 让前台读取与后台 Worker 重建索引可以并存，
    /// 避免 "database is locked" 直接失败。
    /// </summary>
    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=30000; PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>检索命中项。</summary>
    public sealed class SearchHit
    {
        public string Hash { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTimeOffset Mtime { get; set; }
        public List<string> Tags { get; set; } = [];
        public Dictionary<string, double> Facts { get; set; } = [];
        public string? ClusterId { get; set; }
        public string VersionRole { get; set; } = string.Empty;
        public double Score { get; set; }

        /// <summary>同簇的其他版本（折叠展示用）。</summary>
        public List<SearchHit> Siblings { get; set; } = [];

        /// <summary>结果摘要行，如 "type/howto · 30页 · 2026-08 · 简化版"。</summary>
        public string Summary => string.Join(" · ", BuildSummaryParts());

        private IEnumerable<string> BuildSummaryParts()
        {
            foreach (var tag in Tags.Where(t => t.StartsWith("type/", StringComparison.Ordinal)))
                yield return tag.Split('/')[1];
            if (Facts.TryGetValue("page_count", out var pages) && pages > 0)
                yield return $"{pages:0}页";
            if (Mtime > DateTimeOffset.MinValue)
                yield return Mtime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(VersionRole))
                yield return VersionRole switch
                {
                    "simplified" => "简化版",
                    "latest" => "最新版",
                    "final" => "终版",
                    "full" => "完整版",
                    "archive" => "历史版",
                    "wip" => "草稿",
                    _ => VersionRole
                };
        }
    }

    /// <summary>检索结果集合。</summary>
    public sealed class SearchOutcome
    {
        public IReadOnlyList<SearchHit> Hits { get; init; } = [];
        public QueryParser.ParsedQuery Query { get; init; } = new();
        public int TotalCandidates { get; init; }
        public bool UsedFts { get; init; }
    }

    /// <summary>执行一次检索。</summary>
    public SearchOutcome Search(string query, int topK = 50, bool expandVersions = true)
    {
        var parsed = _parser.Parse(query);
        var entries = _catalog.LoadAll();
        if (entries.Count == 0) return new SearchOutcome { Query = parsed };

        // L2 分面预过滤
        var candidates = entries.Where(e => MatchesFacets(e, parsed)).ToList();
        var totalCandidates = candidates.Count;

        // L3 排序：FTS5 稀疏匹配 + 标签加权（标签加权对所有候选生效）
        var hits = new List<SearchHit>();
        var usedFts = false;
        if (!string.IsNullOrWhiteSpace(parsed.RemainingText))
        {
            var ftsScores = QueryFts(parsed.RemainingText);
            if (ftsScores.Count > 0)
            {
                usedFts = true;
                foreach (var entry in candidates)
                {
                    if (!ftsScores.TryGetValue(entry.Hash, out var textScore)) continue;
                    hits.Add(BuildHit(entry, BaseScore + textScore + TagBonus(entry, parsed)));
                }
            }
        }

        if (hits.Count == 0)
        {
            foreach (var entry in candidates)
            {
                hits.Add(BuildHit(entry, BaseScore + TagBonus(entry, parsed)));
            }
        }

        // 排序：分数降序 → 修改时间降序
        var ordered = hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Mtime)
            .Take(topK)
            .ToList();

        // L4 版本簇展开
        if (expandVersions)
        {
            var byCluster = entries
                .Where(e => !string.IsNullOrEmpty(e.ClusterId))
                .GroupBy(e => e.ClusterId!)
                .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var hit in ordered)
            {
                if (string.IsNullOrEmpty(hit.ClusterId)) continue;
                if (!byCluster.TryGetValue(hit.ClusterId, out var members)) continue;
                hit.Siblings = members
                    .Where(m => m.Hash != hit.Hash)
                    .OrderByDescending(m => m.Mtime)
                    .Select(m => BuildHit(m, 0))
                    .ToList();
            }
        }

        return new SearchOutcome
        {
            Hits = ordered,
            Query = parsed,
            TotalCandidates = totalCandidates,
            UsedFts = usedFts
        };
    }

    /// <summary>分面过滤：格式、时间窗、页数区间、命名空间标签。</summary>
    private static bool MatchesFacets(CatalogEntry entry, QueryParser.ParsedQuery parsed)
    {
        if (parsed.Format is not null && !string.Equals(entry.Format, parsed.Format, StringComparison.OrdinalIgnoreCase))
            return false;

        if (parsed.TimeFrom.HasValue && entry.Mtime < parsed.TimeFrom.Value) return false;
        if (parsed.TimeTo.HasValue && entry.Mtime > parsed.TimeTo.Value) return false;

        if (parsed.MinPages.HasValue || parsed.MaxPages.HasValue)
        {
            // 未知页数不参与硬过滤（宁可召回不可错杀）
            if (entry.Facts.TryGetValue("page_count", out var pages) && pages > 0)
            {
                if (parsed.MinPages.HasValue && pages < parsed.MinPages.Value) return false;
                if (parsed.MaxPages.HasValue && pages > parsed.MaxPages.Value) return false;
            }
        }

        foreach (var tag in parsed.Tags)
        {
            if (entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            // 语义标签是软条件：不匹配不淘汰，只在排序时降权
        }

        return true;
    }

    /// <summary>基础分：纯分面查询时保证非零，便于排序稳定。</summary>
    private const double BaseScore = 1.0;

    /// <summary>标签加权分：命中解析出的标签加分。</summary>
    private static double TagBonus(CatalogEntry entry, QueryParser.ParsedQuery parsed)
    {
        if (parsed.Tags.Count == 0) return 0;
        var bonus = 0.0;
        foreach (var tag in parsed.Tags)
        {
            if (entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) bonus += 10;
        }
        return bonus;
    }

    /// <summary>FTS5 查询：trigram 匹配长词 + LIKE 兜底短词（trigram 对 2 字中文失效）。</summary>
    private Dictionary<string, double> QueryFts(string text)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var databasePath = Path.Combine(_metadataPath, "meta.db");
        if (!File.Exists(databasePath)) return result;

        try
        {
            using var connection = OpenConnection(databasePath);
            connection.Open();

            var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct()
                .ToArray();
            if (tokens.Length == 0) return result;

            foreach (var token in tokens)
            {
                // 3 字以上走 trigram 全文匹配；2 字及以下走 LIKE（trigram 对 2 字中文失效）
                if (token.Length >= 3)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT hash, -bm25(files_fts) AS score
                        FROM files_fts WHERE files_fts MATCH $query
                        ORDER BY score DESC LIMIT 200;
                        """;
                    command.Parameters.AddWithValue("$query", BuildFtsQuery(token));
                    ApplyScores(command, result, 1.0);
                }
                else
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT hash, LENGTH(name) * 0.1 AS score
                        FROM files WHERE name LIKE $pattern ESCAPE '\'
                        LIMIT 200;
                        """;
                    command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(token)}%");
                    ApplyScores(command, result, 1.0);
                }

                // 名称/路径子串命中额外加权（用户输入的多半是标题里的词）
                using var nameCommand = connection.CreateCommand();
                nameCommand.CommandText = """
                    SELECT hash FROM files
                    WHERE name LIKE $pattern ESCAPE '\' OR relative_path LIKE $pattern ESCAPE '\'
                    LIMIT 200;
                    """;
                nameCommand.Parameters.AddWithValue("$pattern", $"%{EscapeLike(token)}%");
                ApplyScores(nameCommand, result, 3.0);

                // 中文长词按二元切分做 OR 召回：命中任一片段即可进入候选
                foreach (var fragment in SplitBigrams(token))
                {
                    using var fragmentCommand = connection.CreateCommand();
                    fragmentCommand.CommandText = """
                        SELECT hash, -bm25(files_fts) AS score
                        FROM files_fts WHERE files_fts MATCH $query
                        ORDER BY score DESC LIMIT 100;
                        """;
                    fragmentCommand.Parameters.AddWithValue("$query", BuildFtsQuery(fragment));
                    ApplyScores(fragmentCommand, result, 0.5);
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // 索引不可用时退化为纯标签排序
        }

        return result;
    }

    private static void ApplyScores(SqliteCommand command, Dictionary<string, double> result, double weight)
    {
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var hash = reader.GetString(0);
            // 只有一列时（纯召回查询）按固定权重计分
            var score = reader.FieldCount > 1 && !reader.IsDBNull(1) ? reader.GetDouble(1) : 1.0;
            var weighted = score * weight;
            result[hash] = result.TryGetValue(hash, out var existing) ? existing + weighted : weighted;
        }
    }

    /// <summary>
    /// 中文词按二元切分（bigram），用于 OR 召回。
    /// trigram 需要完整匹配，"代码审查规范"这种长串整词不命中时，靠二元片段提升召回。
    /// </summary>
    private static IEnumerable<string> SplitBigrams(string token)
    {
        if (token.Length < 4) yield break;
        var hasChinese = token.Any(c => c > 127);
        if (!hasChinese) yield break;
        for (var i = 0; i + 2 <= token.Length; i++)
        {
            var piece = token.Substring(i, 2);
            if (piece.Any(c => c > 127)) yield return piece;
        }
    }

    /// <summary>构造 FTS5 MATCH 查询串，转义特殊字符。</summary>
    private static string BuildFtsQuery(string token)
    {
        var escaped = token.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static SearchHit BuildHit(CatalogEntry entry, double score)
    {
        var relative = entry.PrimaryPath;
        var versionRole = entry.Tags
            .FirstOrDefault(t => t.StartsWith("version/", StringComparison.Ordinal))?
            .Split('/')[1] ?? string.Empty;

        return new SearchHit
        {
            Hash = entry.Hash,
            RelativePath = relative,
            FullPath = relative,
            Format = entry.Format,
            Size = entry.Size,
            Mtime = entry.Mtime,
            Tags = entry.Tags.ToList(),
            Facts = new Dictionary<string, double>(entry.Facts),
            ClusterId = entry.ClusterId,
            VersionRole = versionRole,
            Score = score
        };
    }

    /// <summary>重建 SQLite 查询投影（files / files_fts），供检索使用。</summary>
    public int RebuildIndex()
    {
        var entries = _catalog.LoadAll();
        var databasePath = Path.Combine(_metadataPath, "meta.db");
        Directory.CreateDirectory(_metadataPath);

        using var connection = OpenConnection(databasePath);
        connection.Open();

        // 旧版 files_fts 缺少 body 列，需重建
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='files_fts';";
            var exists = Convert.ToInt64(check.ExecuteScalar()) > 0;
            if (exists)
            {
                using var columns = connection.CreateCommand();
                columns.CommandText = "PRAGMA table_info(files_fts);";
                using var reader = columns.ExecuteReader();
                var hasBody = false;
                while (reader.Read()) if (reader.GetString(1).Equals("body", StringComparison.OrdinalIgnoreCase)) hasBody = true;
                if (!hasBody)
                {
                    using var drop = connection.CreateCommand();
                    drop.CommandText = "DROP TABLE files_fts;";
                    drop.ExecuteNonQuery();
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=DELETE;
                CREATE TABLE IF NOT EXISTS files (
                    hash TEXT PRIMARY KEY,
                    relative_path TEXT NOT NULL,
                    name TEXT NOT NULL,
                    extension TEXT NOT NULL,
                    format TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    mtime TEXT NOT NULL,
                    cluster_id TEXT,
                    version_role TEXT,
                    tags TEXT NOT NULL DEFAULT '',
                    facts TEXT NOT NULL DEFAULT ''
                );
                CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
                    hash UNINDEXED, name, path, body, tags, tokenize='trigram'
                );
                """;
            command.ExecuteNonQuery();
        }

        using (var transaction = connection.BeginTransaction())
        {
            using (var clear = connection.CreateCommand())
            {
                clear.CommandText = "DELETE FROM files; DELETE FROM files_fts;";
                clear.ExecuteNonQuery();
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT OR REPLACE INTO files
                (hash, relative_path, name, extension, format, size, mtime, cluster_id, version_role, tags, facts)
                VALUES ($hash,$path,$name,$ext,$format,$size,$mtime,$cluster,$role,$tags,$facts);
                """;
            var pHash = insert.Parameters.Add("$hash", SqliteType.Text);
            var pPath = insert.Parameters.Add("$path", SqliteType.Text);
            var pName = insert.Parameters.Add("$name", SqliteType.Text);
            var pExt = insert.Parameters.Add("$ext", SqliteType.Text);
            var pFormat = insert.Parameters.Add("$format", SqliteType.Text);
            var pSize = insert.Parameters.Add("$size", SqliteType.Integer);
            var pMtime = insert.Parameters.Add("$mtime", SqliteType.Text);
            var pCluster = insert.Parameters.Add("$cluster", SqliteType.Text);
            var pRole = insert.Parameters.Add("$role", SqliteType.Text);
            var pTags = insert.Parameters.Add("$tags", SqliteType.Text);
            var pFacts = insert.Parameters.Add("$facts", SqliteType.Text);

            using var insertFts = connection.CreateCommand();
            insertFts.CommandText = "INSERT INTO files_fts (hash, name, path, body, tags) VALUES ($hash,$name,$path,$body,$tags);";
            var fHash = insertFts.Parameters.Add("$hash", SqliteType.Text);
            var fName = insertFts.Parameters.Add("$name", SqliteType.Text);
            var fPath = insertFts.Parameters.Add("$path", SqliteType.Text);
            var fBody = insertFts.Parameters.Add("$body", SqliteType.Text);
            var fTags = insertFts.Parameters.Add("$tags", SqliteType.Text);

            foreach (var entry in entries)
            {
                var relative = entry.PrimaryPath;
                var name = Path.GetFileName(relative);
                var extension = Path.GetExtension(relative);
                var role = entry.Tags.FirstOrDefault(t => t.StartsWith("version/", StringComparison.Ordinal))?.Split('/')[1];

                pHash.Value = entry.Hash;
                pPath.Value = relative;
                pName.Value = name;
                pExt.Value = extension;
                pFormat.Value = entry.Format;
                pSize.Value = entry.Size;
                pMtime.Value = entry.Mtime.ToString("O");
                pCluster.Value = (object?)entry.ClusterId ?? DBNull.Value;
                pRole.Value = (object?)role ?? DBNull.Value;
                pTags.Value = string.Join(' ', entry.Tags);
                pFacts.Value = EncodeFacts(entry.Facts);
                insert.ExecuteNonQuery();

                fHash.Value = entry.Hash;
                fName.Value = name;
                fPath.Value = relative;
                fBody.Value = BuildSearchBody(entry);
                fTags.Value = string.Join(' ', entry.Tags);
                insertFts.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        // 归并 WAL 并回收空间，保证 meta.db 体积可控
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
            checkpoint.ExecuteNonQuery();
        }

        return entries.Count;
    }

    /// <summary>构造进入全文索引的正文：文件名 + 人工描述正文 + 标签 + 实体概念。</summary>
    private string BuildSearchBody(CatalogEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(Path.GetFileNameWithoutExtension(entry.PrimaryPath));
        builder.Append(' ');
        var describe = _catalog.ReadDescribe(entry.Hash);
        if (!string.IsNullOrWhiteSpace(describe))
        {
            // 只取人工正文（去掉 frontmatter 与系统骨架标记）
            var body = CatalogStore.ExtractManualBody(describe);
            if (body.Length > 0) builder.Append(body).Append(' ');
        }
        if (entry.Entities.Count > 0) builder.Append(string.Join(' ', entry.Entities)).Append(' ');
        if (entry.Concepts.Count > 0) builder.Append(string.Join(' ', entry.Concepts)).Append(' ');
        return builder.ToString().Trim();
    }

    /// <summary>facts 编码为紧凑文本，便于 SQLite 内直接过滤。</summary>
    private static string EncodeFacts(IReadOnlyDictionary<string, double> facts)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in facts)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(key).Append('=').Append(value.ToString("R", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}
