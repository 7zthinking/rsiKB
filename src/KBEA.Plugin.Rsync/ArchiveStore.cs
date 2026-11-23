using KBEA.Core.Archive;
using Microsoft.Data.Sqlite;

namespace KBEA.Plugin.Rsync;

/// <summary>
/// 归档历史的独立存储：插件自治数据库 <c>.7z/archive.db</c>。
/// 不再依赖宿主的 meta.db —— 归档属于插件自己的功能，数据也归插件自己。
/// </summary>
public sealed class ArchiveStore
{
    private readonly string _dbPath;

    public ArchiveStore(string spacePath)
    {
        var metaDir = Path.Combine(spacePath, ".7z");
        Directory.CreateDirectory(metaDir);
        _dbPath = Path.Combine(metaDir, "archive.db");
    }

    public void Record(ArchiveRecord record)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO archive_nodes
            (Id, SpaceName, NodeName, Destination, Note, CreatedAt, Status)
            VALUES ($id, $space, $node, $dest, $note, $created, $status);
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$space", record.SpaceName);
        command.Parameters.AddWithValue("$node", record.NodeName);
        command.Parameters.AddWithValue("$dest", record.Destination);
        command.Parameters.AddWithValue("$note", record.Note);
        command.Parameters.AddWithValue("$created", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$status", record.Status);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ArchiveRecord> GetHistory(int limit = 50)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, SpaceName, NodeName, Destination, Note, CreatedAt, Status
            FROM archive_nodes
            ORDER BY CreatedAt DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<ArchiveRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ArchiveRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.TryParse(reader.GetString(5), out var created) ? created : DateTimeOffset.MinValue,
                reader.GetString(6)));
        }
        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS archive_nodes
            (Id TEXT PRIMARY KEY, SpaceName TEXT NOT NULL, NodeName TEXT NOT NULL,
             Destination TEXT NOT NULL, Note TEXT NOT NULL, CreatedAt TEXT NOT NULL, Status TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        return connection;
    }
}
