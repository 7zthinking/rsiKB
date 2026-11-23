using System.IO;
using System.Windows;
using System.Windows.Controls;
using KBEA.App.Services;
using KBEA.Core.Git;
using KBEA.Core.Models;
using Microsoft.Win32;

namespace KBEA.App.Views;

public partial class SpaceWindow : Window
{
    private readonly string _originalName;
    private readonly IGitOperations? _git;
    private readonly List<DirectoryDefinition> _directories;
    private readonly Guid? _preselectDirectoryId;
    private bool _gitAutoFilled; // 标记远程地址是否由 Git 自动填入（自动填入时锁死）

    public SpaceWindow(string? existingName = null, IGitOperations? git = null, List<DirectoryDefinition>? directories = null, Guid? preselectDirectoryId = null, string? existingPath = null)
    {
        InitializeComponent();
        _originalName = existingName ?? string.Empty;
        _originalPath = existingPath;
        _git = git;
        _directories = directories ?? [];
        _preselectDirectoryId = preselectDirectoryId;
        NameBox.Text = _originalName;
        LoadDirectories();
        if (existingName is not null)
        {
            Title = "编辑空间";
            Heading.Text = "编辑空间";
            Description.Text = "可以修改名称、重新绑定物理位置或删除这个空间。重新绑定后，原位置的元数据会被清除，新位置会重新建立元数据；资料文件不受影响。";
            RemoteOptions.Visibility = Visibility.Collapsed;
            GitOptions.Visibility = Visibility.Collapsed;
            DeleteButton.Visibility = Visibility.Visible;
            SaveButton.Content = "保存修改";
            // 编辑模式显示当前绑定的位置，允许重新选择
            if (!string.IsNullOrEmpty(existingPath) && Directory.Exists(existingPath))
            {
                FolderPathText.Tag = existingPath;
                FolderPathText.Text = existingPath;
                FolderPathText.Foreground = Foreground;
            }
            Height = 480;
        }
        UpdateSaveState();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public SpaceDialogResult? Value { get; private set; }
    private readonly string? _originalPath;
    private string FolderPath => FolderPathText.Tag as string ?? string.Empty;

    /// <summary>编辑模式下用户重新选择的绑定位置；与原位置相同或未选择时为 null。</summary>
    public string? RebindPath
    {
        get
        {
            if (_originalPath is null) return null;
            var current = FolderPath;
            if (string.IsNullOrWhiteSpace(current)) return null;
            return current.TrimEnd('\\').Equals(_originalPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? null : current;
        }
    }

    private void LoadDirectories()
    {
        DirectoryBox.Items.Clear();
        DirectoryBox.Items.Add(new DirectoryOption(null, "（不归入任何目录）"));
        foreach (var directory in _directories)
        {
            var prefix = BuildPrefix(directory);
            DirectoryBox.Items.Add(new DirectoryOption(directory.Id, $"{prefix}{directory.Name}"));
        }
        // 预选目录
        if (_preselectDirectoryId is { } id)
        {
            var match = DirectoryBox.Items.OfType<DirectoryOption>().FirstOrDefault(x => x.Id == id);
            if (match is not null) { DirectoryBox.SelectedItem = match; return; }
        }
        DirectoryBox.SelectedIndex = 0;
    }

    private string BuildPrefix(DirectoryDefinition directory)
    {
        var names = new List<string>();
        var current = directory;
        var guard = 0;
        while (current is not null && guard++ < 16)
        {
            names.Insert(0, current.Name);
            current = _directories.FirstOrDefault(x => x.Id == current.ParentId);
        }
        return string.Join(" / ", names) + " / ";
    }

    private void UpdateSaveState() => SaveButton.IsEnabled = _originalName.Length > 0
        || (!string.IsNullOrWhiteSpace(NameBox.Text) && Directory.Exists(FolderPath));

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSaveState();

    private void RemoteKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoteHintText is null) return;
        RemoteHintText.Text = RemoteKindBox.SelectedIndex switch
        {
            1 => "rsync 目录：填写远程 rsync 地址，本地副本会自动缓存。",
            2 => "网络位置：填写 \\\\服务器\\共享目录。",
            _ => "Git 仓库：填写仓库地址，首次打开时自动克隆到本地副本。"
        };
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择空间对应的物理位置", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        FolderPathText.Tag = dialog.FolderName;
        FolderPathText.Text = dialog.FolderName;
        FolderPathText.Foreground = Foreground;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = new DirectoryInfo(dialog.FolderName).Name;
        SelectFolderButton.Content = "重新选择";
        RefreshGitInfo();
        UpdateSaveState();
    }

    private void RefreshGitInfo()
    {
        if (_git is null || !Directory.Exists(FolderPath)) return;
        try
        {
            var root = _git.DiscoverRepositoryRoot(FolderPath);
            if (root is null)
            {
                GitInfoText.Text = "当前位置尚未关联 Git 仓库。";
                GitButton.Content = "关联 Git 仓库";
                // 取消之前的自动填充
                if (_gitAutoFilled) { RemoteUrlBox.Text = string.Empty; RemoteUrlBox.IsReadOnly = false; _gitAutoFilled = false; }
                return;
            }
            var status = _git.GetStatus(root);
            GitInfoText.Text = $"已关联 Git：{root}\n分支 {status.Branch} · {(string.IsNullOrWhiteSpace(status.RemoteUrl) ? "未配置远程仓库" : status.RemoteUrl)}";
            GitButton.Content = "查看 Git 仓库信息";
            // 自动填入远程地址并锁死
            if (!string.IsNullOrWhiteSpace(status.RemoteUrl))
            {
                RemoteUrlBox.Text = status.RemoteUrl;
                RemoteUrlBox.IsReadOnly = true;
                RemoteKindBox.SelectedIndex = 0; // Git
                _gitAutoFilled = true;
                RemoteHintText.Text = "已检测到该位置的 Git 远程地址，自动填入且不可修改。如需更换，请先在 Git 仓库对话框中解绑。";
            }
        }
        catch (Exception ex) { GitInfoText.Text = $"Git 状态读取失败：{ex.Message}"; }
    }

    private void Git_Click(object sender, RoutedEventArgs e)
    {
        if (_git is null || !Directory.Exists(FolderPath))
        {
            MessageBox.Show(this, "请先选择一个物理位置。", "空间", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new GitRepositoryWindow(_git, FolderPath) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Value is not null)
        {
            try
            {
                var status = _git.BindRepository(dialog.Value.RepositoryPath, dialog.Value.RemoteUrl);
                FolderPathText.Tag = dialog.Value.RepositoryPath;
                FolderPathText.Text = dialog.Value.RepositoryPath;
                GitInfoText.Text = $"Git 已关联：分支 {status.Branch} · {(string.IsNullOrWhiteSpace(status.RemoteUrl) ? "未配置远程仓库" : status.RemoteUrl)}";
                GitButton.Content = "查看 Git 仓库信息";
                // 解绑后取消自动填充锁定
                if (string.IsNullOrWhiteSpace(status.RemoteUrl))
                {
                    RemoteUrlBox.Text = string.Empty;
                    RemoteUrlBox.IsReadOnly = false;
                    _gitAutoFilled = false;
                }
                else
                {
                    RemoteUrlBox.Text = status.RemoteUrl;
                    RemoteUrlBox.IsReadOnly = true;
                    RemoteKindBox.SelectedIndex = 0;
                    _gitAutoFilled = true;
                }
            }
            catch (Exception ex) { MessageBox.Show(this, $"Git 关联失败：{ex.Message}", "Git 仓库", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        else RefreshGitInfo();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "请输入空间名称。", "空间", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var directoryId = (DirectoryBox.SelectedItem as DirectoryOption)?.Id;
        // 新建模式：Path 必填；编辑模式：Path 不变，重新绑定的位置走 NewPath
        Value = new SpaceDialogResult(
            name,
            _originalName.Length == 0 ? FolderPath : string.Empty,
            RemoteUrlBox.Text.Trim(),
            SelectedRemoteKind,
            directoryId,
            NewPath: _originalName.Length == 0 ? null : RebindPath);
        DialogResult = true;
    }

    private RemoteLocationKind SelectedRemoteKind => RemoteKindBox.SelectedIndex switch
    {
        1 => RemoteLocationKind.Rsync,
        2 => RemoteLocationKind.Network,
        _ => RemoteLocationKind.Git
    };

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        Value = new SpaceDialogResult(_originalName, DeleteRequested: true);
        DialogResult = true;
    }

    private sealed record DirectoryOption(Guid? Id, string Label)
    {
        public override string ToString() => Label;
    }
}
