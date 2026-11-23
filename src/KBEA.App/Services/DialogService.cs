using System.Windows;
using KBEA.App.Views;
using KBEA.Core.Git;
using KBEA.Core.Models;
using Microsoft.Win32;

namespace KBEA.App.Services;

public sealed class DialogService : IDialogService
{
    // GitService 已迁入 kbea-plugin-git（P2）：经 GitOperationsProvider 解析，插件未加载时相关对话框降级
    private static IGitOperations? Git => GitOperationsProvider.Instance;
    private List<DirectoryDefinition> _directories = [];

    /// <summary>供空间对话框展示“归属目录”候选项，由 MainViewModel 在启动时提供。</summary>
    public void SetDirectories(List<DirectoryDefinition> directories) => _directories = directories;

    public string? Prompt(string title, string label, string defaultValue = "")
    {
        var dialog = new InputDialog(title, label, defaultValue) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public string? SelectFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }

    public void Info(string message, string title = "知识库自进化助手") => MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool Confirm(string message, string title = "请确认") => MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool ShowSettings(AppConfiguration configuration)
    {
        var dialog = new SettingsWindow(configuration, PluginBridge.GetSettingsPages(), () => PluginBridge.Instance?.SavePluginSettings()) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true;
    }

    public SpaceDialogResult? ShowNewSpace(Guid? preselectDirectoryId = null)
    {
        var dialog = new SpaceWindow(git: Git, directories: _directories, preselectDirectoryId: preselectDirectoryId) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public SpaceDialogResult? ShowEditSpace(string name, string? currentPath = null, Guid? preselectDirectoryId = null)
    {
        var dialog = new SpaceWindow(name, Git, _directories, preselectDirectoryId, currentPath) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public RemoteLocationDefinition? ShowRemoteLocation()
    {
        var dialog = new RemoteLocationWindow { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public GitDialogResult? ShowGitRepository(string currentPath)
    {
        var git = Git;
        if (git is null)
        {
            Info("Git 插件未加载，无法配置 Git 仓库。");
            return null;
        }
        var dialog = new GitRepositoryWindow(git, currentPath) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public void ShowHistory(string title, string repositoryPath)
    {
        var git = Git;
        var status = git?.GetStatus(repositoryPath) ?? new GitStatusInfo(false, 0, string.Empty, string.Empty, 0);
        var entries = status.IsRepository && git is not null ? git.GetDetailedStatus(repositoryPath) : Array.Empty<KBEA.Core.Models.GitStatusEntry>();
        var commits = status.IsRepository && git is not null ? git.GetHistory(repositoryPath) : Array.Empty<KBEA.Core.Models.GitCommitInfo>();
        var dialog = new GitHistoryWindow(title, repositoryPath, status, entries, commits) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
    }

    public void ShowArchives(IEnumerable<ArchiveRecord> records, string title)
    {
        var dialog = new RecordListWindow(title, records) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
    }

    public void ShowTeamManager(TeamShareService teamService, AppConfiguration configuration, Action onChanged)
    {
        var dialog = new TeamManagementWindow(teamService, configuration, this) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
        onChanged();
    }

    public IGitProgress BeginGitOperation(string title, string subtitle)
    {
        var window = new GitOutputWindow(title, subtitle) { Owner = Application.Current.MainWindow };
        window.Show();
        var wrapper = new GitProgressWrapper(window);
        return wrapper;
    }

    /// <summary>把 GitOutputWindow 封装成 IGitProgress。</summary>
    private sealed class GitProgressWrapper(GitOutputWindow window) : IGitProgress
    {
        private readonly Progress<string> _inner = new(line => window.AppendLine(line));
        private bool _disposed;

        public void Report(string value) => ((IProgress<string>)_inner).Report(value);
        public void Done(bool succeeded, string finalMessage)
        {
            if (_disposed) return;
            _disposed = true; // 关键：Done 后 Dispose 不能再用"操作被中止"覆盖结果（曾致所有 git 操作显示失败）
            window.SetResult(succeeded, finalMessage);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 没调 Done 就 dispose，说明异常中断：给个收尾状态
            window.SetResult(false, "操作被中止。");
        }
    }
}
