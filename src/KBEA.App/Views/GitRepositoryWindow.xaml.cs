using System.IO;
using System.Windows;
using KBEA.App.Services;
using KBEA.Core.Git;
using Microsoft.Win32;

namespace KBEA.App.Views;

public partial class GitRepositoryWindow : Window
{
    private readonly GitService _git;
    private bool _isRepository;
    private bool _editing;

    public GitRepositoryWindow(GitService git, string currentPath)
    {
        InitializeComponent();
        _git = git;
        RepositoryPathBox.Text = currentPath;
        LoadRepository(currentPath);
    }

    public GitDialogResult? Value { get; private set; }

    private void LoadRepository(string path)
    {
        _isRepository = false;
        _editing = false;
        RepositoryPathBox.Text = path;
        RemoteUrlBox.Text = string.Empty;
        RemoteUrlBox.IsReadOnly = true;
        SelectRepositoryButton.Visibility = Visibility.Visible;
        EditConnectionButton.Visibility = Visibility.Collapsed;
        ConfirmButton.Content = "确认关联";
        HintText.Text = "选择或输入 远程仓库 地址后，将在该目录初始化或关联 Git 仓库。";
        BranchText.Text = "当前目录尚未关联 Git 仓库";
        ChangesText.Text = "确认关联会保留现有文件，不会自动提交或上传。";
        if (!Directory.Exists(path)) return;
        try
        {
            var root = _git.DiscoverRepositoryRoot(path);
            if (root is null) return;
            var status = _git.GetStatus(root);
            _isRepository = true;
            RepositoryPathBox.Text = root;
            RemoteUrlBox.Text = status.RemoteUrl;
            BranchText.Text = $"当前分支：{status.Branch}";
            ChangesText.Text = status.ConflictCount > 0
                ? $"{status.ChangeCount} 项变更，{status.ConflictCount} 项冲突"
                : status.ChangeCount > 0 ? $"{status.ChangeCount} 项未提交变更" : "工作区没有未提交变更";
            SelectRepositoryButton.Visibility = Visibility.Collapsed;
            EditConnectionButton.Visibility = Visibility.Visible;
            ConfirmButton.Content = "保存连接";
            ConfirmButton.IsEnabled = false;
            HintText.Text = string.IsNullOrWhiteSpace(status.RemoteUrl)
                ? "尚未配置 远程仓库。点击“修改仓库连接”可添加远程地址。"
                : "如需变更远程连接，请先点击“修改仓库连接”。";
        }
        catch (Exception ex)
        {
            ChangesText.Text = $"读取仓库状态失败：{ex.Message}";
        }
    }

    private void SelectRepository_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要关联的 Git 仓库目录", Multiselect = false };
        if (dialog.ShowDialog(this) == true) LoadRepository(dialog.FolderName);
    }

    private void EditConnection_Click(object sender, RoutedEventArgs e)
    {
        _editing = true;
        RemoteUrlBox.IsReadOnly = false;
        RemoteUrlBox.Focus();
        RemoteUrlBox.SelectAll();
        ConfirmButton.IsEnabled = true;
        EditConnectionButton.IsEnabled = false;
        HintText.Text = "保存后仅更新当前仓库的 远程仓库 连接，不会立即拉取或上传。";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var path = RepositoryPathBox.Text.Trim();
        var remote = RemoteUrlBox.Text.Trim();
        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, "请选择存在的仓库目录。", "Git 仓库", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_isRepository && !_editing) { DialogResult = false; return; }
        Value = new GitDialogResult(path, remote);
        DialogResult = true;
    }
}
