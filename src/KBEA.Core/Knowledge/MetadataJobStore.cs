using System.Text.Json;
using KBEA.Core.Plugins;
using Microsoft.Data.Sqlite;

namespace KBEA.Core.Knowledge;

/// <summary>
/// 元数据任务队列：前端投递任务、Worker 领取执行，共享同一个 SQLite 库。
/// 库放在用户本地 AppData（不进 git），每个知识库只保留路径引用。
/// </summary>
public sealed class MetadataJobStore
{
    private readonly string _connectionString;

    public MetadataJobStore(string? databasePath = null)
    {
        DatabasePath = databasePath ?? DefaultDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
    }

    /// <summary>默认任务库位置：%LOCALAPPDATA%\KBEA\metadata-jobs.db。</summary>
    public static string DefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KBEA",
        "metadata-jobs.db");

    public string DatabasePath { get; }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS jobs (
                Id TEXT PRIMARY KEY,
                SpacePath TEXT NOT NULL,
                SpaceName TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                State INTEGER NOT NULL,
                Trigger TEXT NOT NULL,
                Processed INTEGER NOT NULL DEFAULT 0,
                Total INTEGER NOT NULL DEFAULT 0,
                Stage TEXT NOT NULL DEFAULT '',
                Message TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL,
                StartedAt TEXT,
                FinishedAt TEXT,
                ScheduleId TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_jobs_state ON jobs(State);
            CREATE INDEX IF NOT EXISTS idx_jobs_space ON jobs(SpacePath);
            CREATE TABLE IF NOT EXISTS job_progress (
                JobId TEXT PRIMARY KEY,
                SpacePath TEXT NOT NULL,
                Stage TEXT NOT NULL,
                Processed INTEGER NOT NULL DEFAULT 0,
                Total INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS plugin_progress (
                JobId TEXT NOT NULL,
                PluginId TEXT NOT NULL,
                PluginName TEXT NOT NULL,
                Level INTEGER NOT NULL,
                Stage TEXT NOT NULL DEFAULT '',
                Processed INTEGER NOT NULL DEFAULT 0,
                Total INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (JobId, PluginId)
            );
            CREATE INDEX IF NOT EXISTS idx_plugin_progress_job ON plugin_progress(JobId);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>投递一个新任务（排队中）。</summary>
    public MetadataJob Enqueue(MetadataJob job)
    {
        Initialize();
        job.State = MetadataJobState.Queued;
        job.CreatedAt = DateTimeOffset.Now;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (Id,SpacePath,SpaceName,Kind,State,Trigger,Processed,Total,Stage,Message,CreatedAt,StartedAt,FinishedAt,ScheduleId)
            VALUES ($id,$spacePath,$spaceName,$kind,$state,$trigger,0,0,'等待 Worker 领取','',$createdAt,NULL,NULL,$scheduleId);
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$spacePath", job.SpacePath);
        command.Parameters.AddWithValue("$spaceName", job.SpaceName);
        command.Parameters.AddWithValue("$kind", (int)job.Kind);
        command.Parameters.AddWithValue("$state", (int)job.State);
        command.Parameters.AddWithValue("$trigger", job.Trigger);
        command.Parameters.AddWithValue("$createdAt", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$scheduleId", (object?)job.ScheduleId ?? DBNull.Value);
        command.ExecuteNonQuery();
        return job;
    }

    /// <summary>领取最早的排队任务；无排队任务返回 null。</summary>
    public MetadataJob? ClaimNext()
    {
        Initialize();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        // 同一空间的任务必须串行：并发处理会互相抢占 catalog/describe 的写入
        select.CommandText = """
            SELECT Id,SpacePath,SpaceName,Kind,State,Trigger,Processed,Total,Stage,Message,CreatedAt,StartedAt,FinishedAt,ScheduleId
            FROM jobs j
            WHERE j.State=$queued
              AND NOT EXISTS (
                  SELECT 1 FROM jobs r
                  WHERE r.State=$running AND r.SpacePath=j.SpacePath AND r.Id<>j.Id)
            ORDER BY j.CreatedAt LIMIT 1;
            """;
        select.Parameters.AddWithValue("$queued", (int)MetadataJobState.Queued);
        select.Parameters.AddWithValue("$running", (int)MetadataJobState.Running);
        MetadataJob? job = null;
        using (var reader = select.ExecuteReader())
        {
            if (reader.Read()) job = ReadJob(reader);
        }
        if (job is null)
        {
            transaction.Commit();
            return null;
        }

        job.State = MetadataJobState.Running;
        job.StartedAt = DateTimeOffset.Now;
        job.Stage = "开始执行";
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE jobs SET State=$state, StartedAt=$started, Stage=$stage WHERE Id=$id;";
        update.Parameters.AddWithValue("$state", (int)MetadataJobState.Running);
        update.Parameters.AddWithValue("$started", job.StartedAt.Value.ToString("O"));
        update.Parameters.AddWithValue("$stage", job.Stage);
        update.Parameters.AddWithValue("$id", job.Id);
        update.ExecuteNonQuery();
        transaction.Commit();
        return job;
    }

    /// <summary>更新任务进度。</summary>
    public void UpdateProgress(string jobId, string stage, int processed, int total)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs SET Stage=$stage, Processed=$processed, Total=$total WHERE Id=$id;
            INSERT INTO job_progress (JobId,SpacePath,Stage,Processed,Total,UpdatedAt)
            SELECT $id, SpacePath, $stage, $processed, $total, $now FROM jobs WHERE Id=$id
            ON CONFLICT(JobId) DO UPDATE SET Stage=$stage, Processed=$processed, Total=$total, UpdatedAt=$now;
            """;
        command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$processed", processed);
        command.Parameters.AddWithValue("$total", total);
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>更新某个插件在任务中的进度。</summary>
    public void UpdatePluginProgress(string jobId, PluginProgress progress)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plugin_progress (JobId,PluginId,PluginName,Level,Stage,Processed,Total,UpdatedAt)
            VALUES ($jobId,$pluginId,$pluginName,$level,$stage,$processed,$total,$now)
            ON CONFLICT(JobId,PluginId) DO UPDATE SET
                PluginName=$pluginName, Level=$level, Stage=$stage,
                Processed=$processed, Total=$total, UpdatedAt=$now;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$pluginId", progress.PluginId);
        command.Parameters.AddWithValue("$pluginName", progress.PluginName);
        command.Parameters.AddWithValue("$level", (int)progress.Level);
        command.Parameters.AddWithValue("$stage", progress.Stage);
        command.Parameters.AddWithValue("$processed", progress.Processed);
        command.Parameters.AddWithValue("$total", progress.Total);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>读取某任务的各插件进度。</summary>
    public IReadOnlyList<PluginProgress> GetPluginProgress(string jobId)
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PluginId,PluginName,Level,Stage,Processed,Total,UpdatedAt
            FROM plugin_progress WHERE JobId=$jobId ORDER BY Level,PluginId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        using var reader = command.ExecuteReader();
        var result = new List<PluginProgress>();
        while (reader.Read())
        {
            result.Add(new PluginProgress
            {
                PluginId = reader.GetString(0),
                PluginName = reader.GetString(1),
                Level = (MetadataLevel)reader.GetInt32(2),
                Stage = reader.GetString(3),
                Processed = reader.GetInt32(4),
                Total = reader.GetInt32(5),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(6))
            });
        }
        return result;
    }

    /// <summary>标记任务结束。</summary>
    public void Complete(string jobId, MetadataJobState state, string message)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET State=$state, Message=$message, FinishedAt=$finished, Stage=$stage WHERE Id=$id;";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$finished", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$stage", state == MetadataJobState.Succeeded ? "已完成" : "已结束");
        command.Parameters.AddWithValue("$id", jobId);
        command.ExecuteNonQuery();
    }

    /// <summary>列出任务；按库过滤时只看该库最近的任务。</summary>
    public IReadOnlyList<MetadataJob> List(string? spacePath = null, int limit = 200)
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(spacePath))
        {
            command.CommandText = """
                SELECT Id,SpacePath,SpaceName,Kind,State,Trigger,Processed,Total,Stage,Message,CreatedAt,StartedAt,FinishedAt,ScheduleId
                FROM jobs ORDER BY CreatedAt DESC LIMIT $limit;
                """;
        }
        else
        {
            command.CommandText = """
                SELECT Id,SpacePath,SpaceName,Kind,State,Trigger,Processed,Total,Stage,Message,CreatedAt,StartedAt,FinishedAt,ScheduleId
                FROM jobs WHERE SpacePath=$spacePath ORDER BY CreatedAt DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$spacePath", spacePath);
        }
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<MetadataJob>();
        while (reader.Read()) result.Add(ReadJob(reader));
        return result;
    }

    /// <summary>取某库各层级的最近一次任务状态，用于配置面板展示整体进度。</summary>
    public IReadOnlyDictionary<string, MetadataJob> LatestPerSpace()
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,SpacePath,SpaceName,Kind,State,Trigger,Processed,Total,Stage,Message,CreatedAt,StartedAt,FinishedAt,ScheduleId
            FROM jobs ORDER BY CreatedAt ASC;
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, MetadataJob>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var job = ReadJob(reader);
            var key = $"{job.SpacePath}|{(int)job.Kind}";
            result[key] = job; // 顺序升序，后读到的覆盖，即为最新
        }
        return result;
    }

    /// <summary>某库是否有排队或执行中的任务。</summary>
    public bool HasActiveJob(string spacePath)
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM jobs WHERE SpacePath=$p AND State IN ($queued,$running);";
        command.Parameters.AddWithValue("$p", spacePath);
        command.Parameters.AddWithValue("$queued", (int)MetadataJobState.Queued);
        command.Parameters.AddWithValue("$running", (int)MetadataJobState.Running);
        var count = Convert.ToInt64(command.ExecuteScalar());
        return count > 0;
    }

    /// <summary>清理已完成且超过保留期的历史任务。</summary>
    public int Cleanup(int keepDays = 30)
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM jobs WHERE State IN ($succeeded,$failed,$canceled) AND CreatedAt < $cutoff;
            DELETE FROM job_progress WHERE UpdatedAt < $cutoff;
            """;
        command.Parameters.AddWithValue("$succeeded", (int)MetadataJobState.Succeeded);
        command.Parameters.AddWithValue("$failed", (int)MetadataJobState.Failed);
        command.Parameters.AddWithValue("$canceled", (int)MetadataJobState.Canceled);
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.Now.AddDays(-keepDays).ToString("O"));
        return command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static MetadataJob ReadJob(SqliteDataReader reader)
    {
        return new MetadataJob
        {
            Id = reader.GetString(0),
            SpacePath = reader.GetString(1),
            SpaceName = reader.GetString(2),
            Kind = (MetadataJobKind)reader.GetInt32(3),
            State = (MetadataJobState)reader.GetInt32(4),
            Trigger = reader.GetString(5),
            Processed = reader.GetInt32(6),
            Total = reader.GetInt32(7),
            Stage = reader.GetString(8),
            Message = reader.GetString(9),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(10)),
            StartedAt = reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
            FinishedAt = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
            ScheduleId = reader.IsDBNull(13) ? null : reader.GetString(13)
        };
    }
}

/// <summary>知识库元数据计划文件的读写。</summary>
public sealed class MetadataPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public MetadataPlanStore(string spaceRoot)
    {
        SpaceRoot = Path.GetFullPath(spaceRoot);
        PlanPath = Path.Combine(SpaceRoot, ".7z", "metadata-plan.json");
    }

    public string SpaceRoot { get; }
    public string PlanPath { get; }

    /// <summary>读取计划；不存在则返回默认计划（基础层启用，其余层按插件可用性判断）。</summary>
    public MetadataPlan Load()
    {
        try
        {
            if (File.Exists(PlanPath))
            {
                var plan = JsonSerializer.Deserialize<MetadataPlan>(File.ReadAllText(PlanPath), JsonOptions);
                if (plan is not null) return plan;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 计划文件损坏时回退默认，不阻断使用
        }
        return CreateDefault(SpaceRoot);
    }

    /// <summary>保存计划。</summary>
    public void Save(MetadataPlan plan)
    {
        plan.UpdatedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(Path.GetDirectoryName(PlanPath)!);
        // 临时文件名带唯一后缀，避免并发写入时互相抢占
        var temporary = $"{PlanPath}.{Environment.ProcessId:x}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(plan, JsonOptions));
            File.Move(temporary, PlanPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    /// <summary>默认计划：基础层常开，类型层按已注册插件判断，专题层默认关闭（等模型就绪）。</summary>
    public static MetadataPlan CreateDefault(string spacePath)
    {
        var plan = new MetadataPlan { SpacePath = spacePath };
        plan.GetOrAdd(MetadataLevel.Basic).Enabled = true;
        plan.GetOrAdd(MetadataLevel.Type).Enabled = true;
        plan.GetOrAdd(MetadataLevel.Semantic).Enabled = false;
        return plan;
    }
}
