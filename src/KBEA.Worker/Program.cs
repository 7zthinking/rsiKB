using System.Text;
using System.Text.Json;
using KBEA.Core.Knowledge;
using KBEA.Core.Storage;

namespace KBEA.Worker;

/// <summary>
/// 元数据后台 Worker 进程。
/// 独立于前端运行：从任务库领取元数据任务、执行、回报进度，并按计划触发定时任务。
/// 前端关闭或重启不影响已排队任务的执行。
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var options = WorkerOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        var jobStore = new MetadataJobStore(options.DatabasePath);
        jobStore.Initialize();

        var spaces = LoadSpaces(options.ConfigPath);
        var scheduler = new MetadataScheduler(jobStore, () => spaces);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log("收到停止信号，正在退出…");
            shutdown.Cancel();
        };

        Log($"KBEA 元数据 Worker 启动（pid={Environment.ProcessId}）");
        Log($"任务库：{jobStore.DatabasePath}");
        Log($"已登记空间：{spaces.Count} 个");

        var recovered = scheduler.RecoverStaleJobs();
        if (recovered > 0) Log($"已恢复 {recovered} 个中断任务");

        if (options.RunOnce)
        {
            // 单次模式：处理完当前队列后退出，供任务计划或调试使用
            var job = jobStore.ClaimNext();
            if (job is null)
            {
                Log("队列为空，无事可做");
                return 0;
            }
            Log($"执行任务 {job.Id}：{MetadataLevelMapping.DisplayName(job.Kind)} @ {job.SpaceName}");
            var ok = scheduler.RunJob(job, Log);
            Log(ok ? "任务完成" : "任务失败");
            return ok ? 0 : 1;
        }

        Log("进入轮询模式（Ctrl+C 退出）");
        var executed = scheduler.RunLoop(shutdown.Token, Log);
        Log($"Worker 退出，本次共执行 {executed} 个任务");
        return 0;
    }

    private static IReadOnlyList<SpaceDefinitionLite> LoadSpaces(string? configPath)
    {
        try
        {
            var store = new WorkspaceStore(configPath);
            var configuration = store.Load();
            return configuration.Spaces
                .Where(s => !string.IsNullOrWhiteSpace(s.Path))
                .Select(s => new SpaceDefinitionLite(s.Name, s.Path))
                .ToList();
        }
        catch (Exception ex)
        {
            Log($"读取空间配置失败：{ex.Message}");
            return [];
        }
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    private static void PrintHelp()
    {
        Console.WriteLine("""
            KBEA 元数据 Worker

            用法：KBEA.Worker [选项]

              --config <路径>   指定 workspaces.json（默认读 %APPDATA%\KBEA\workspaces.json）
              --db <路径>       指定任务库位置（默认 %LOCALAPPDATA%\KBEA\metadata-jobs.db）
              --once            只处理当前队列中的一个任务后退出
              --help            显示本帮助
            """);
    }
}

/// <summary>Worker 命令行参数。</summary>
internal sealed class WorkerOptions
{
    public string? ConfigPath { get; private set; }
    public string? DatabasePath { get; private set; }
    public bool RunOnce { get; private set; }
    public bool ShowHelp { get; private set; }

    public static WorkerOptions Parse(string[] args)
    {
        var options = new WorkerOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--config" when i + 1 < args.Length:
                    options.ConfigPath = args[++i];
                    break;
                case "--db" when i + 1 < args.Length:
                    options.DatabasePath = args[++i];
                    break;
                case "--once":
                    options.RunOnce = true;
                    break;
                case "--help" or "-h" or "/?":
                    options.ShowHelp = true;
                    break;
            }
        }
        return options;
    }
}
