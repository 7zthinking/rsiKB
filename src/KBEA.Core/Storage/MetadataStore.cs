using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KBEA.Core.Models;
using Microsoft.Data.Sqlite;

namespace KBEA.Core.Storage;

public sealed class MetadataStore
{
    private readonly string _mountPath;
    private readonly string _connectionString;

    public MetadataStore(string mountPath)
    {
        _mountPath = Path.GetFullPath(mountPath);
        MetadataPath = Path.Combine(_mountPath, ".7z");
        DatabasePath = Path.Combine(MetadataPath, "meta.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
    }

    public string MetadataPath { get; }
    public string DatabasePath { get; }

    public void Initialize()
    {
        Directory.CreateDirectory(_mountPath);
        foreach (var directory in new[] { "index", "catalog", "lock", "assistant" })
            Directory.CreateDirectory(Path.Combine(MetadataPath, directory));
        EnsureGitIgnore(_mountPath);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS catalog_links (
                Id TEXT PRIMARY KEY,
                WorkspaceId TEXT NOT NULL,
                CategoryId TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                FileHash TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UNIQUE(WorkspaceId, CategoryId, RelativePath)
            );
            CREATE TABLE IF NOT EXISTS file_index (
                RelativePath TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Extension TEXT NOT NULL,
                Size INTEGER NOT NULL,
                LastWriteUtc TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS file_fts USING fts5(RelativePath UNINDEXED, Name, Extension);
            CREATE TABLE IF NOT EXISTS archive_nodes (
                Id TEXT PRIMARY KEY,
                MountName TEXT NOT NULL,
                NodeName TEXT NOT NULL,
                Destination TEXT NOT NULL,
                Note TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Status TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_logs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Time TEXT NOT NULL,
                Operation TEXT NOT NULL,
                Status TEXT NOT NULL,
                Message TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public void AddLink(Guid workspaceId, Guid categoryId, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var hash = ComputeHash(Path.Combine(_mountPath, normalized));
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO catalog_links (Id, WorkspaceId, CategoryId, RelativePath, FileHash, CreatedAt)
            VALUES ($id, $workspace, $category, $path, $hash, $created)
            ON CONFLICT(WorkspaceId, CategoryId, RelativePath) DO UPDATE SET FileHash = excluded.FileHash;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString());
        command.Parameters.AddWithValue("$category", categoryId.ToString());
        command.Parameters.AddWithValue("$path", normalized);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void RemoveLink(Guid workspaceId, Guid categoryId, string relativePath)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM catalog_links WHERE WorkspaceId=$workspace AND CategoryId=$category AND RelativePath=$path;";
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString());
        command.Parameters.AddWithValue("$category", categoryId.ToString());
        command.Parameters.AddWithValue("$path", NormalizeRelativePath(relativePath));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<CatalogLink> GetLinks(Guid workspaceId, Guid? categoryId = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = categoryId.HasValue
            ? "SELECT Id,WorkspaceId,CategoryId,RelativePath,FileHash,CreatedAt FROM catalog_links WHERE WorkspaceId=$workspace AND CategoryId=$category ORDER BY RelativePath;"
            : "SELECT Id,WorkspaceId,CategoryId,RelativePath,FileHash,CreatedAt FROM catalog_links WHERE WorkspaceId=$workspace ORDER BY RelativePath;";
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString());
        if (categoryId.HasValue) command.Parameters.AddWithValue("$category", categoryId.Value.ToString());
        using var reader = command.ExecuteReader();
        var result = new List<CatalogLink>();
        while (reader.Read())
        {
            result.Add(new CatalogLink(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5))));
        }
        return result;
    }

    public IReadOnlyList<Guid> GetCategoryIds(Guid workspaceId, string relativePath)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CategoryId FROM catalog_links WHERE WorkspaceId=$workspace AND RelativePath=$path;";
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString());
        command.Parameters.AddWithValue("$path", NormalizeRelativePath(relativePath));
        using var reader = command.ExecuteReader();
        var result = new List<Guid>();
        while (reader.Read()) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    public int IndexFiles(CancellationToken cancellationToken = default)
    {
        var files = EnumerateFiles().ToArray();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM file_index; DELETE FROM file_fts;";
            clear.ExecuteNonQuery();
        }
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO file_index (RelativePath,Name,Extension,Size,LastWriteUtc) VALUES ($path,$name,$extension,$size,$modified);";
        var pathParameter = insert.Parameters.Add("$path", SqliteType.Text);
        var nameParameter = insert.Parameters.Add("$name", SqliteType.Text);
        var extensionParameter = insert.Parameters.Add("$extension", SqliteType.Text);
        var sizeParameter = insert.Parameters.Add("$size", SqliteType.Integer);
        var modifiedParameter = insert.Parameters.Add("$modified", SqliteType.Text);
        using var insertFts = connection.CreateCommand();
        insertFts.CommandText = "INSERT INTO file_fts (RelativePath,Name,Extension) VALUES ($path,$name,$extension);";
        var ftsPath = insertFts.Parameters.Add("$path", SqliteType.Text);
        var ftsName = insertFts.Parameters.Add("$name", SqliteType.Text);
        var ftsExtension = insertFts.Parameters.Add("$extension", SqliteType.Text);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var relative = Path.GetRelativePath(_mountPath, file);
            pathParameter.Value = ftsPath.Value = relative;
            nameParameter.Value = ftsName.Value = info.Name;
            extensionParameter.Value = ftsExtension.Value = info.Extension;
            sizeParameter.Value = info.Length;
            modifiedParameter.Value = info.LastWriteTimeUtc.ToString("O");
            insert.ExecuteNonQuery();
            insertFts.ExecuteNonQuery();
        }
        transaction.Commit();
        return files.Length;
    }

    public IReadOnlyList<FileEntry> Search(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var query = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => $"\"{token.Replace("\"", "\"\"")}\""));
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.RelativePath,f.Name,f.Size,f.LastWriteUtc
            FROM file_fts fts JOIN file_index f ON f.RelativePath=fts.RelativePath
            WHERE file_fts MATCH $query ORDER BY f.Name LIMIT 200;
            """;
        command.Parameters.AddWithValue("$query", query);
        using var reader = command.ExecuteReader();
        var result = new List<FileEntry>();
        while (reader.Read())
        {
            var relative = reader.GetString(0);
            result.Add(new FileEntry(reader.GetString(1), Path.Combine(_mountPath, relative), relative, false,
                reader.GetInt64(2), DateTimeOffset.Parse(reader.GetString(3)).LocalDateTime));
        }
        return result;
    }

    public string ExportCatalogSnapshot(WorkspaceDefinition workspace)
    {
        var directory = Path.Combine(MetadataPath, "catalog");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{workspace.Id}.json");
        var snapshot = new CatalogSnapshot(workspace.Categories, GetLinks(workspace.Id));
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public void RecordArchive(ArchiveRecord record)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO archive_nodes (Id,MountName,NodeName,Destination,Note,CreatedAt,Status) VALUES ($id,$mount,$node,$destination,$note,$created,$status);";
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$mount", record.MountName);
        command.Parameters.AddWithValue("$node", record.NodeName);
        command.Parameters.AddWithValue("$destination", record.Destination);
        command.Parameters.AddWithValue("$note", record.Note);
        command.Parameters.AddWithValue("$created", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$status", record.Status);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ArchiveRecord> GetArchives(int limit = 50)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,MountName,NodeName,Destination,Note,CreatedAt,Status FROM archive_nodes ORDER BY CreatedAt DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<ArchiveRecord>();
        while (reader.Read()) result.Add(new ArchiveRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)), reader.GetString(6)));
        return result;
    }

    public void RecordSync(string operation, string status, string message)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sync_logs (Time,Operation,Status,Message) VALUES ($time,$operation,$status,$message);";
        command.Parameters.AddWithValue("$time", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", message);
        command.ExecuteNonQuery();
    }

    public void SetConfigValue(string key, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public string? GetConfigValue(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM settings WHERE Key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public static void EnsureGitIgnore(string mountPath)
    {
        var path = Path.Combine(mountPath, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var required = new[] { ".7z/", "*.tmp", "*.temp", "~$*", "Thumbs.db", "*.tif", "*.tiff", "*.gdb/", "*.shp", "*.shx", "*.dbf", "*.prj", "*.gpkg", "*.las", "*.laz" };
        var missing = required.Where(rule => !existing.Any(line => line.Trim().Equals(rule, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length == 0) return;
        using var writer = File.AppendText(path);
        if (existing.Count > 0) writer.WriteLine();
        writer.WriteLine("# KBEA: metadata, temporary and spatial data files");
        foreach (var rule in missing) writer.WriteLine(rule);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private IEnumerable<string> EnumerateFiles()
    {
        var pending = new Stack<string>();
        pending.Push(_mountPath);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    var name = Path.GetFileName(entry);
                    if (!name.Equals(".git", StringComparison.OrdinalIgnoreCase) && !name.Equals(".7z", StringComparison.OrdinalIgnoreCase)) pending.Push(entry);
                }
                else yield return entry;
            }
        }
    }

    private string NormalizeRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) relativePath = Path.GetRelativePath(_mountPath, relativePath);
        relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (relativePath.StartsWith("..", StringComparison.Ordinal)) throw new ArgumentException("只能编目当前挂载文件夹中的内容。", nameof(relativePath));
        return relativePath;
    }

    private static string ComputeHash(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record CatalogSnapshot(List<CategoryNode> Categories, IReadOnlyList<CatalogLink> Links);
}
