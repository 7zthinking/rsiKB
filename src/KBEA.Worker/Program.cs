using System.Text;
using System.Text.Json;
using KBEA.Core.Knowledge;
using KBEA.Core.Plugins;
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

        // 在启动调度器前加载插件：Worker 只加载声明 metadata 能力的元数据类插件，
        // UI 类插件（toolbar/git/wiki…）在 Worker 内跳过。单个插件失败只记日志。
        LoadPlugins();

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

    /// <summary>发现并加载 plugins 目录下声明 metadata 能力的插件（AppContext.BaseDirectory/plugins）。</summary>
    private static void LoadPlugins()
    {
        try
        {
            var candidates = new PluginCatalog().DiscoverWithDirectories()
                .Where(p => p.Manifest.Enabled && p.Manifest.HasCapability("metadata"))
                .ToList();
            var loaded = new PluginLoader().Load(candidates, new WorkerPluginHost());
            var count = 0;
            foreach (var plugin in loaded)
            {
                if (plugin.SkipReason is not null)
                {
                    Log($"插件 {plugin.Manifest.Id} 跳过：{plugin.SkipReason}");
                    continue;
                }
                if (plugin.Error is not null)
                {
                    Log($"插件 {plugin.Manifest.Id} 加载失败：{plugin.Error.Message}");
                    continue;
                }
                count++;
                Log($"插件已加载：{plugin.Manifest.Id} v{plugin.Manifest.Version}");
            }
            Log($"已加载插件：{count} 个");
        }
        catch (Exception ex)
        {
            // 插件目录不可读等异常不阻断 Worker 启动
            Log($"插件加载失败：{ex.Message}");
        }
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

/// <summary>
/// Worker 内的精简插件宿主：元数据工具可用；UI 注册为 no-op，侧栏/浏览快照为空。
/// </summary>
internal sealed class WorkerPluginHost : IPluginHostContext
{
    /// <summary>与 App 侧 MainViewModel.Version 的语义版本保持一致。</summary>
    private const string HostVersionValue = "0.3.0";

    public string HostVersion => HostVersionValue;
    public IMetadataWorkbench Metadata { get; } = new MetadataWorkbench();
    public ISidebarState Sidebar { get; } = new EmptySidebarState();
    public IBrowserState Browser { get; } = new EmptyBrowserState();
    public IUiSurface Ui { get; } = new NoopUiSurface();
    public KBEA.Core.Plugins.IGitUiCommands? GitUi => null; // Worker 无 UI，Git 插件在 Worker 内不注册按钮
    public KBEA.Core.Plugins.IWikiUiCommands? WikiUi => null; // Worker 无 UI，WIKI 插件在 Worker 内只注册 Ingest 实现
    public KBEA.Core.Plugins.IArchiveUiCommands? ArchiveUi => null; // Worker 无 UI，归档插件在 Worker 内不注册按钮

    private sealed class EmptySidebarState : ISidebarState
    {
        public SidebarSnapshot Current { get; } = new(string.Empty, null, 0);
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class EmptyBrowserState : IBrowserState
    {
        public BrowserSnapshot Current { get; } = new(null, null, null, 0, null);
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class NoopUiSurface : IUiSurface
    {
        public void AddToolbarGroup(ToolbarGroupVm group) { }
        public void RemoveToolbarGroup(ToolbarGroupVm group) { }
        public void SetGlobalStatus(string message) { }
    }
}
