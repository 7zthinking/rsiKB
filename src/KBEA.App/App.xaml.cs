using System.Windows;
using KBEA.App.Services;
using KBEA.App.ViewModels;
using KBEA.Core.Archive;
using KBEA.Core.Files;
using KBEA.Core.Plugins;
using KBEA.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace KBEA.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 全局异常日志：UI 线程 + 后台线程未捕获异常写入 %TEMP%\kbea-crash.log
        DispatcherUnhandledException += (_, args) =>
        {
            DiagLog.Exception("UI", args.Exception);
            // WPF 控件容器在集合变动瞬间的竞态异常（IndexOutOfRange / ArgumentOutOfRange）
            // 是可恢复的 UI 层问题：吞掉它并继续跑，绝不让整个程序退出。
            if (IsRecoverableUiRace(args.Exception))
            {
                args.Handled = true;
                DiagLog.Write("UI: recovered from container race, application continues");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagLog.Exception("Task", args.Exception);
            args.SetObserved();
        };
        var services = new ServiceCollection();
        var configIndex = Array.IndexOf(e.Args, "--config");
        var configPath = configIndex >= 0 && configIndex + 1 < e.Args.Length ? e.Args[configIndex + 1] : null;
        services.AddSingleton(new WorkspaceStore(configPath));
        services.AddSingleton<FileBrowserService>();
        // GitService 已随 P2 迁入 kbea-plugin-git：宿主经 GitOperationsProvider.Instance 解析 IGitOperations
        // rsync 归档已随本次迁入 kbea-plugin-rsync：宿主经 ArchiveOperationsProvider.Instance 解析 IArchiveOperations
        services.AddSingleton<PluginCatalog>();
        services.AddSingleton<WorkerClient>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();
        var viewModel = _services.GetRequiredService<MainViewModel>();
        ThemeService.Apply(viewModel.Configuration.Theme);
        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        var openIndex = Array.IndexOf(e.Args, "--open");
        if (openIndex >= 0 && openIndex + 1 < e.Args.Length && Directory.Exists(e.Args[openIndex + 1]))
            viewModel.OpenLocal(e.Args[openIndex + 1]);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetRequiredService<MainViewModel>().Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 判断异常是否属于 WPF 容器竞态（可恢复）。
    /// 这类异常发生在 ItemsSource 集合变动与控件容器生成/回收交叠的瞬间，
    /// 只影响当次 UI 更新，不涉及数据损坏，重绘即可恢复。
    /// </summary>
    private static bool IsRecoverableUiRace(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is IndexOutOfRangeException or ArgumentOutOfRangeException) return true;
        }
        return exception.StackTrace?.Contains("ItemContainerGenerator") == true;
    }
}
