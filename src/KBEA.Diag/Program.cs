using KBEA.Core.Knowledge;
using KBEA.Core.Storage;
using KBEA.Diag;
using Microsoft.Data.Sqlite;

// 诊断工具：排查 .7z 访问与任务失败
// 用法：KBEA.Diag spaces [空间路径...]  |  KBEA.Diag jobs [任务库路径]  |  KBEA.Diag logo <源png> <输出目录>
var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "spaces";

if (mode == "logo")
{
    LogoTool.Run(args);
    return;
}

if (mode == "jobs")
{
    try
    {
        var jobDb = args.Length > 1 ? args[1] : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KBEA", "metadata-jobs.db");
        if (!File.Exists(jobDb)) { Console.WriteLine($"任务库不存在: {jobDb}"); return; }

        var cs = new SqliteConnectionStringBuilder { DataSource = jobDb, Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SpaceName, Kind, State, Trigger, Stage, Message, CreatedAt, FinishedAt
            FROM jobs ORDER BY CreatedAt DESC LIMIT 30;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var kind = Convert.ToInt32(reader.GetValue(1)) switch { 0 => "基础层", 1 => "类型层", _ => "专题层" };
            var state = Convert.ToInt32(reader.GetValue(2)) switch
            {
                0 => "排队中", 1 => "执行中", 2 => "已完成", 3 => "失败", _ => "已取消"
            };
            Console.WriteLine($"[{state}] {kind} @ {reader.GetString(0)} (触发={reader.GetString(3)})");
            Console.WriteLine($"    阶段: {reader.GetString(4)}");
            Console.WriteLine($"    信息: {reader.GetString(5)}");
            Console.WriteLine($"    时间: {reader.GetString(6)} → {(reader.IsDBNull(7) ? "-" : reader.GetString(7))}");
            Console.WriteLine();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"查询失败: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }
    return;
}

var repoRoot = AppContext.BaseDirectory;
while (!string.IsNullOrEmpty(repoRoot) && !Directory.Exists(Path.Combine(repoRoot, "KBEA.sln")))
    repoRoot = Path.GetDirectoryName(repoRoot);

var targets = args.Length > 1
    ? args.Skip(1).ToArray()
    : repoRoot is null ? [] : [repoRoot];

foreach (var target in targets)
{
    Console.WriteLine($"== 空间: {target}");
    Try("MetadataStore.Initialize", () => new MetadataStore(target).Initialize());
    Try("CatalogStore.LoadManifest", () => new CatalogStore(Path.Combine(target, ".7z"), target).LoadManifest());
    Try("CatalogStore.LoadAll", () => new CatalogStore(Path.Combine(target, ".7z"), target).LoadAll());
    Try("KnowledgeSearchService.RebuildIndex", () => new KnowledgeSearchService(Path.Combine(target, ".7z")).RebuildIndex());
    Try("CatalogStore.WriteDescribe", () =>
    {
        var catalog = new CatalogStore(Path.Combine(target, ".7z"), target);
        var entry = catalog.LoadAll().FirstOrDefault();
        if (entry is null) return;
        catalog.WriteDescribe(entry, "<!-- kb:auto -->\n\n测试\n\n<!-- /kb:auto -->");
    });
    Console.WriteLine();
}

static void Try(string name, Action action)
{
    try
    {
        action();
        Console.WriteLine($"   OK   {name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   FAIL {name}: {ex.GetType().Name}: {ex.Message}");
        var line = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
        if (line is not null) Console.WriteLine($"        at {line}");
    }
}
