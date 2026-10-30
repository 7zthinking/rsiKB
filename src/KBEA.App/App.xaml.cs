using System.Windows;
using KBEA.App.Services;
using KBEA.App.ViewModels;
using KBEA.Core.Archive;
using KBEA.Core.Files;
using KBEA.Core.Git;
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
        var services = new ServiceCollection();
        var configIndex = Array.IndexOf(e.Args, "--config");
        var configPath = configIndex >= 0 && configIndex + 1 < e.Args.Length ? e.Args[configIndex + 1] : null;
        services.AddSingleton(new WorkspaceStore(configPath));
        services.AddSingleton<FileBrowserService>();
        services.AddSingleton<GitService>();
        services.AddSingleton<ArchiveService>();
        services.AddSingleton<PluginCatalog>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();
        var viewModel = _services.GetRequiredService<MainViewModel>();
        ThemeService.Apply(viewModel.Configuration.Theme);
        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetRequiredService<MainViewModel>().Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}
