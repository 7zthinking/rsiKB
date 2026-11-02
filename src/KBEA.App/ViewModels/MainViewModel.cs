using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KBEA.App.Services;
using KBEA.Core.Archive;
using KBEA.Core.Files;
using KBEA.Core.Git;
using KBEA.Core.Models;
using KBEA.Core.Plugins;
using KBEA.Core.Storage;

namespace KBEA.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceStore _store;
    private readonly FileBrowserService _files;
    private readonly GitService _git;
    private readonly ArchiveService _archive;
    private readonly PluginCatalog _plugins;
    private readonly IDialogService _dialogs;
    private readonly ChangeMonitor _monitor = new();
    private AppConfiguration _configuration;
    private WorkspaceNodeViewModel? _selectedNode;
    private MountTabViewModel? _selectedTab;
    private CatalogResult? _selectedCatalogResult;
    private string _globalStatus = "就绪";
    private string _catalogTitle = string.Empty;
    private bool _isCatalogView;
    private bool _isBusy;
    private bool _isSidebarVisible = true;
    private bool _isWorkspaceSidebar = true;
    private bool _isAssistantOpen;
    private bool _disposed;

    public MainViewModel(WorkspaceStore store, FileBrowserService files, GitService git, ArchiveService archive, PluginCatalog plugins, IDialogService dialogs)
    {
        _store = store;
        _files = files;
        _git = git;
        _archive = archive;
        _plugins = plugins;
        _dialogs = dialogs;
        _configuration = store.Load();
        NewWorkspaceCommand = new RelayCommand(NewWorkspace);
        MountFolderCommand = new AsyncRelayCommand(MountFolderAsync);
        UnmountCommand = new RelayCommand(Unmount, () => SelectedTab?.IsLocal == false || SelectedNode?.Kind == WorkspaceNodeKind.Mount);
        AddCategoryCommand = new RelayCommand(AddCategory);
        AssignCategoryCommand = new RelayCommand(AssignCategory, () => SelectedNode?.Kind == WorkspaceNodeKind.Category && SelectedTab?.IsLocal == false && SelectedTab.SelectedFile is not null);
        BindGitCommand = new AsyncRelayCommand(BindGitAsync, () => SelectedTab?.IsOnline == true && !IsBusy);
        PullCommand = new AsyncRelayCommand(PullAsync, () => SelectedTab?.CanUseGit == true && !IsBusy);
        SavePushCommand = new AsyncRelayCommand(SavePushAsync, () => SelectedTab?.CanUseGit == true && !IsBusy);
        HistoryCommand = new RelayCommand(ShowHistory, () => SelectedTab?.CanUseGit == true);
        ArchiveCommand = new AsyncRelayCommand(ArchiveAsync, () => SelectedTab?.IsLocal == false && SelectedTab.IsOnline && !IsBusy);
        ArchiveHistoryCommand = new RelayCommand(ShowArchiveHistory, () => SelectedTab?.IsLocal == false);
        SettingsCommand = new RelayCommand(ShowSettings);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => SelectedTab is not null && !IsBusy);
        NewTabCommand = new RelayCommand(NewTab);
        CloseTabCommand = new RelayCommand<MountTabViewModel>(CloseTab);
        OpenCatalogResultCommand = new RelayCommand(OpenCatalogResult, () => SelectedCatalogResult is not null);
        AddFavoriteCommand = new RelayCommand(AddFavorite, () => SelectedTab is not null);
        OpenFavoriteCommand = new RelayCommand<string>(OpenFavorite);
        OpenLocalFolderCommand = new RelayCommand(OpenLocalFolder);
        RefreshLocalFoldersCommand = new RelayCommand(RefreshLocalFolders);
        ShowWorkspaceSidebarCommand = new RelayCommand(() => IsWorkspaceSidebar = true);
        ShowLocalSidebarCommand = new RelayCommand(() => IsWorkspaceSidebar = false);
        ExportCatalogCommand = new RelayCommand(ExportCatalog);
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarVisible = !IsSidebarVisible);
        ToggleAssistantCommand = new RelayCommand(() => IsAssistantOpen = !IsAssistantOpen);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        _monitor.Changed += MonitorChanged;
        Load();
    }

    public AppConfiguration Configuration => _configuration;
    public ObservableCollection<WorkspaceNodeViewModel> Workspaces { get; } = [];
    public ObservableCollection<LocalFolderViewModel> LocalFolders { get; } = [];
    public ObservableCollection<MountTabViewModel> Tabs { get; } = [];
    public ObservableCollection<CatalogResult> CatalogResults { get; } = [];
    public ObservableCollection<PluginManifest> Plugins { get; } = [];
    public ObservableCollection<string> Favorites { get; } = [];
    public RelayCommand NewWorkspaceCommand { get; }
    public AsyncRelayCommand MountFolderCommand { get; }
    public RelayCommand UnmountCommand { get; }
    public RelayCommand AddCategoryCommand { get; }
    public RelayCommand AssignCategoryCommand { get; }
    public AsyncRelayCommand BindGitCommand { get; }
    public AsyncRelayCommand PullCommand { get; }
    public AsyncRelayCommand SavePushCommand { get; }
    public RelayCommand HistoryCommand { get; }
    public AsyncRelayCommand ArchiveCommand { get; }
    public RelayCommand ArchiveHistoryCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand NewTabCommand { get; }
    public RelayCommand<MountTabViewModel> CloseTabCommand { get; }
    public RelayCommand OpenCatalogResultCommand { get; }
    public RelayCommand AddFavoriteCommand { get; }
    public RelayCommand<string> OpenFavoriteCommand { get; }
    public RelayCommand OpenLocalFolderCommand { get; }
    public RelayCommand RefreshLocalFoldersCommand { get; }
    public RelayCommand ShowWorkspaceSidebarCommand { get; }
    public RelayCommand ShowLocalSidebarCommand { get; }
    public RelayCommand ExportCatalogCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand ToggleAssistantCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public string Version => "开源版 v0.2.0";
    public string PluginSummary => Plugins.Count == 0 ? "智能服务未启用" : $"发现 {Plugins.Count} 个插件";

    public WorkspaceNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetProperty(ref _selectedNode, value)) return;
            RefreshCommands();
            UpdateFavorites();
            if (value?.Kind == WorkspaceNodeKind.Mount && value.Mount is not null) OpenMount(value.Workspace, value.Mount);
            else if (value?.Kind == WorkspaceNodeKind.Category && value.Category is not null) ShowCatalog(value.Workspace, value.Category);
        }
    }

    public MountTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value)) return;
            if (value is not null) IsCatalogView = false;
            OnPropertyChanged(nameof(IsBrowserView));
            OnPropertyChanged(nameof(IsEmptyView));
            RefreshCommands();
            UpdateFavorites();
        }
    }

    public CatalogResult? SelectedCatalogResult
    {
        get => _selectedCatalogResult;
        set { if (SetProperty(ref _selectedCatalogResult, value)) OpenCatalogResultCommand.NotifyCanExecuteChanged(); }
    }

    public string GlobalStatus { get => _globalStatus; set => SetProperty(ref _globalStatus, value); }
    public string CatalogTitle { get => _catalogTitle; set => SetProperty(ref _catalogTitle, value); }
    public bool IsCatalogView
    {
        get => _isCatalogView;
        set
        {
            if (!SetProperty(ref _isCatalogView, value)) return;
            OnPropertyChanged(nameof(IsBrowserView));
            OnPropertyChanged(nameof(IsEmptyView));
        }
    }
    public bool IsBrowserView => !IsCatalogView && SelectedTab is not null;
    public bool IsEmptyView => !IsCatalogView && SelectedTab is null;
    public bool IsSidebarVisible { get => _isSidebarVisible; set => SetProperty(ref _isSidebarVisible, value); }
    public bool IsWorkspaceSidebar
    {
        get => _isWorkspaceSidebar;
        set { if (SetProperty(ref _isWorkspaceSidebar, value)) OnPropertyChanged(nameof(IsLocalSidebar)); }
    }
    public bool IsLocalSidebar => !IsWorkspaceSidebar;
    public bool IsAssistantOpen { get => _isAssistantOpen; set => SetProperty(ref _isAssistantOpen, value); }

    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetProperty(ref _isBusy, value)) RefreshCommands(); }
    }

    private void Load()
    {
        RebuildTree();
        RefreshLocalFolders();
        foreach (var plugin in _plugins.Discover()) Plugins.Add(plugin);
        OnPropertyChanged(nameof(PluginSummary));
        foreach (var workspace in _configuration.Workspaces)
        {
            foreach (var favorite in workspace.Favorites)
                if (!_configuration.FavoriteFolders.Contains(favorite)) _configuration.FavoriteFolders.Add(favorite);
            foreach (var mount in workspace.Mounts.Where(x => Directory.Exists(x.Path)))
            {
                try { new MetadataStore(mount.Path).Initialize(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { GlobalStatus = $"元数据初始化失败：{ex.Message}"; }
            }
        }
        var first = _configuration.Workspaces.SelectMany(x => x.Mounts).FirstOrDefault(x => Directory.Exists(x.Path));
        if (first is not null)
        {
            var workspace = _configuration.Workspaces.First(x => x.Mounts.Contains(first));
            OpenMount(workspace, first);
        }
        UpdateFavorites();
    }

    private void RefreshLocalFolders()
    {
        LocalFolders.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady) LocalFolders.Add(new LocalFolderViewModel(drive.RootDirectory.FullName, drive.Name, _files));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void RebuildTree()
    {
        Workspaces.Clear();
        foreach (var workspace in _configuration.Workspaces)
        {
            var workspaceNode = new WorkspaceNodeViewModel(workspace, WorkspaceNodeKind.Workspace, workspace.Name);
            foreach (var category in workspace.Categories) workspaceNode.Children.Add(CreateCategoryNode(workspace, category));
            foreach (var mount in workspace.Mounts)
                workspaceNode.Children.Add(new WorkspaceNodeViewModel(workspace, WorkspaceNodeKind.Mount, mount.Name) { Mount = mount });
            Workspaces.Add(workspaceNode);
        }
    }

    private static WorkspaceNodeViewModel CreateCategoryNode(WorkspaceDefinition workspace, CategoryNode category)
    {
        var node = new WorkspaceNodeViewModel(workspace, WorkspaceNodeKind.Category, category.Name) { Category = category };
        foreach (var child in category.Children) node.Children.Add(CreateCategoryNode(workspace, child));
        return node;
    }

    private void NewWorkspace()
    {
        var name = _dialogs.Prompt("新建工作空间", "工作空间名称", "新工作空间");
        if (string.IsNullOrWhiteSpace(name)) return;
        var workspace = new WorkspaceDefinition { Name = name.Trim() };
        _configuration.Workspaces.Add(workspace);
        SaveConfiguration();
        RebuildTree();
        SelectedNode = Workspaces.First(x => x.Workspace == workspace);
        GlobalStatus = $"已创建工作空间“{workspace.Name}”。";
    }

    private async Task MountFolderAsync()
    {
        var workspace = SelectedNode?.Workspace ?? (SelectedTab?.IsLocal == false ? SelectedTab.Workspace : null) ?? _configuration.Workspaces.FirstOrDefault();
        if (workspace is null) { _dialogs.Info("请先新建一个工作空间。"); return; }
        var path = _dialogs.SelectFolder("选择要挂接的物理文件夹");
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_configuration.Workspaces.SelectMany(x => x.Mounts).Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.Info("这个文件夹已经挂接，无需重复添加。");
            return;
        }
        var mount = new MountDefinition { Name = new DirectoryInfo(path).Name, Path = path };
        IsBusy = true;
        GlobalStatus = $"正在挂接 {path}…";
        try
        {
            await Task.Run(() => { var metadata = new MetadataStore(path); metadata.Initialize(); metadata.IndexFiles(); });
            workspace.Mounts.Add(mount);
            SaveConfiguration();
            RebuildTree();
            OpenMount(workspace, mount);
            GlobalStatus = $"已挂接 {mount.Name}，元数据与文件名索引已建立。";
        }
        catch (Exception ex) { _dialogs.Info($"挂接失败：{ex.Message}"); GlobalStatus = "挂接失败"; }
        finally { IsBusy = false; }
    }

    private void Unmount()
    {
        var mount = SelectedNode?.Mount ?? (SelectedTab?.IsLocal == false ? SelectedTab.Mount : null);
        if (mount is null) return;
        var workspace = _configuration.Workspaces.FirstOrDefault(x => x.Mounts.Contains(mount));
        if (workspace is null) return;
        if (!_dialogs.Confirm($"仅从当前工作空间解挂“{mount.Name}”？\n\n磁盘文件、Git 历史和 .7z 元数据都会保留。")) return;
        workspace.Mounts.Remove(mount);
        var tab = Tabs.FirstOrDefault(x => x.Mount == mount);
        if (tab is not null) CloseTab(tab);
        _monitor.Stop(mount.Path);
        SaveConfiguration();
        RebuildTree();
        GlobalStatus = $"已解挂 {mount.Name}，数据未删除。";
    }

    private void AddCategory()
    {
        var node = SelectedNode;
        var workspace = node?.Workspace ?? (SelectedTab?.IsLocal == false ? SelectedTab.Workspace : null) ?? _configuration.Workspaces.FirstOrDefault();
        if (workspace is null) { _dialogs.Info("请先新建一个工作空间。"); return; }
        var name = _dialogs.Prompt("新建分类", "分类名称");
        if (string.IsNullOrWhiteSpace(name)) return;
        var category = new CategoryNode { Name = name.Trim() };
        if (node?.Kind == WorkspaceNodeKind.Category && node.Category is not null) node.Category.Children.Add(category);
        else workspace.Categories.Add(category);
        SaveConfiguration();
        RebuildTree();
        GlobalStatus = $"已添加分类“{category.Name}”。";
    }

    private void AssignCategory()
    {
        if (SelectedNode?.Category is not { } category || SelectedTab?.IsLocal != false || SelectedTab.SelectedFile is not { } file) return;
        try
        {
            var metadata = new MetadataStore(SelectedTab.Mount.Path);
            metadata.AddLink(SelectedTab.Workspace.Id, category.Id, file.RelativePath);
            metadata.ExportCatalogSnapshot(SelectedTab.Workspace);
            GlobalStatus = $"已将“{file.Name}”编入“{category.Name}”。";
        }
        catch (Exception ex) { _dialogs.Info($"编目失败：{ex.Message}"); }
    }

    private void ShowCatalog(WorkspaceDefinition workspace, CategoryNode category)
    {
        CatalogResults.Clear();
        foreach (var mount in workspace.Mounts.Where(x => Directory.Exists(x.Path)))
        {
            try
            {
                var metadata = new MetadataStore(mount.Path);
                foreach (var link in metadata.GetLinks(workspace.Id, category.Id))
                    CatalogResults.Add(new CatalogResult(mount.Id, mount.Name, mount.Path, link.RelativePath, Path.Combine(mount.Path, link.RelativePath), link.FileHash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { GlobalStatus = $"读取编目失败：{ex.Message}"; }
        }
        CatalogTitle = $"{workspace.Name} / {category.Name} · {CatalogResults.Count} 项";
        IsCatalogView = true;
    }

    private void OpenCatalogResult()
    {
        if (SelectedCatalogResult is not { } result) return;
        var workspace = _configuration.Workspaces.FirstOrDefault(x => x.Id == SelectedNode?.Workspace.Id);
        var mount = workspace?.Mounts.FirstOrDefault(x => x.Id == result.MountId);
        if (workspace is null || mount is null) return;
        OpenMount(workspace, mount);
        SelectedTab?.SelectCatalogFile(result.FullPath);
    }

    private void OpenMount(WorkspaceDefinition workspace, MountDefinition mount, bool forceNew = false)
    {
        if (!Directory.Exists(mount.Path)) { _dialogs.Info("该文件夹当前离线。重新连接磁盘或网络后即可使用。"); return; }
        var tab = forceNew ? null : Tabs.FirstOrDefault(x => !x.IsLocal && x.Mount == mount);
        if (tab is null)
        {
            tab = new MountTabViewModel(mount, workspace, _files, _git);
            tab.SelectionChanged += (_, _) => RefreshCommands();
            Tabs.Add(tab);
        }
        SelectedTab = tab;
        if (_configuration.AutoDetect) _monitor.Start(mount.Path);
    }

    private void NewTab()
    {
        if (SelectedTab is { } tab)
        {
            if (tab.IsLocal) OpenLocal(tab.CurrentPath, forceNew: true);
            else OpenMount(tab.Workspace, tab.Mount, forceNew: true);
            return;
        }
        OpenLocalFolder();
    }

    private void OpenLocalFolder()
    {
        var path = _dialogs.SelectFolder("选择要浏览的本地文件夹");
        if (!string.IsNullOrWhiteSpace(path)) OpenLocal(path, forceNew: true);
    }

    public void OpenLocal(string path, bool forceNew = false)
    {
        if (!Directory.Exists(path)) return;
        if (!forceNew && SelectedTab?.IsLocal == true && path.StartsWith(SelectedTab.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            SelectedTab.Navigate(path);
            return;
        }
        var tab = forceNew ? null : Tabs.FirstOrDefault(x => x.IsLocal && path.StartsWith(x.RootPath, StringComparison.OrdinalIgnoreCase));
        if (tab is null)
        {
            tab = new MountTabViewModel(path, _files, _git);
            Tabs.Add(tab);
        }
        else if (!tab.CurrentPath.Equals(path, StringComparison.OrdinalIgnoreCase)) tab.Navigate(path);
        SelectedTab = tab;
        GlobalStatus = $"正在浏览 {path}";
    }

    private async Task BindGitAsync()
    {
        if (SelectedTab is not { } tab) return;
        var remote = _dialogs.Prompt("绑定 Git 仓库", "远程仓库地址（可留空，仅使用本地仓库）", tab.GitStatus.RemoteUrl);
        if (remote is null) return;
        await RunOperationAsync(tab, "Git 绑定", () => _git.BindRepository(tab.Mount.Path, remote).Display);
        tab.Mount.RemoteUrl = remote;
        SaveConfiguration();
    }

    private Task PullAsync() => SelectedTab is { } tab
        ? RunOperationAsync(tab, "拉取更新", () => _git.Pull(tab.Mount.Path, _configuration.AuthorName, _configuration.AuthorEmail))
        : Task.CompletedTask;

    private async Task SavePushAsync()
    {
        if (SelectedTab is not { } tab) return;
        var message = _dialogs.Prompt("保存并上传", "提交说明", $"更新资料 {DateTime.Now:MM-dd HH:mm}");
        if (message is null) return;
        await RunOperationAsync(tab, "保存并上传", () => _git.SaveAndPush(tab.Mount.Path, message, _configuration.AuthorName, _configuration.AuthorEmail));
    }

    private async Task RunOperationAsync(MountTabViewModel tab, string operation, Func<string> action)
    {
        IsBusy = true;
        GlobalStatus = $"{tab.Title}：正在{operation}…";
        try
        {
            var message = await Task.Run(action);
            tab.Mount.LastSyncAt = DateTimeOffset.Now;
            if (!tab.IsLocal) new MetadataStore(tab.Mount.Path).RecordSync(operation, "成功", message);
            SaveConfiguration();
            tab.Refresh();
            GlobalStatus = $"{tab.Title}：{message}";
        }
        catch (Exception ex)
        {
            try { if (!tab.IsLocal) new MetadataStore(tab.Mount.Path).RecordSync(operation, "失败", ex.Message); } catch { }
            GlobalStatus = $"{tab.Title}：{operation}失败";
            _dialogs.Info($"{operation}失败：{ex.Message}");
        }
        finally { IsBusy = false; }
    }

    private void ShowHistory()
    {
        if (SelectedTab is { } tab) _dialogs.ShowHistory(_git.GetHistory(tab.Mount.Path), $"{tab.Title} · Git 历史");
    }

    private async Task ArchiveAsync()
    {
        if (SelectedTab is not { IsLocal: false } tab) return;
        var note = _dialogs.Prompt("标记关键节点", "节点备注", $"关键节点 {DateTime.Now:yyyy-MM-dd HH:mm}");
        if (note is null) return;
        IsBusy = true;
        GlobalStatus = $"{tab.Title}：正在执行 rsync 关键节点归档…";
        try
        {
            var progress = new Progress<string>(message => GlobalStatus = $"{tab.Title}：{message}");
            var record = await Task.Run(() => _archive.CreateAsync(tab.Mount, _configuration, note, progress));
            GlobalStatus = $"{tab.Title}：关键节点 {record.NodeName} 已归档。";
        }
        catch (Exception ex) { GlobalStatus = "归档失败"; _dialogs.Info($"归档失败：{ex.Message}"); }
        finally { IsBusy = false; }
    }

    private void ShowArchiveHistory()
    {
        if (SelectedTab is { IsLocal: false } tab) _dialogs.ShowArchives(_archive.GetHistory(tab.Mount.Path), $"{tab.Title} · 关键节点");
    }

    private void ShowSettings()
    {
        if (!_dialogs.ShowSettings(_configuration)) return;
        SaveConfiguration();
        ThemeService.Apply(_configuration.Theme);
        foreach (var workspace in _configuration.Workspaces)
            foreach (var mount in workspace.Mounts)
            {
                if (_configuration.AutoDetect) _monitor.Start(mount.Path);
                else _monitor.Stop(mount.Path);
            }
        GlobalStatus = "设置已保存。";
    }

    private async Task RefreshAsync()
    {
        if (SelectedTab is not { } tab || !tab.IsOnline) return;
        IsBusy = true;
        GlobalStatus = $"{tab.Title}：正在刷新{(tab.IsLocal ? "目录" : "索引")}与 Git 状态…";
        try
        {
            var count = await Task.Run(tab.RebuildIndex);
            tab.Refresh();
            GlobalStatus = tab.IsLocal
                ? $"{tab.Title}：目录已刷新，当前显示 {count} 项。"
                : $"{tab.Title}：已刷新，索引包含 {count} 个文件。";
        }
        catch (Exception ex) { _dialogs.Info($"刷新失败：{ex.Message}"); GlobalStatus = "刷新失败"; }
        finally { IsBusy = false; }
    }

    private void AddFavorite()
    {
        if (SelectedTab is not { } tab) return;
        if (!_configuration.FavoriteFolders.Contains(tab.CurrentPath)) _configuration.FavoriteFolders.Add(tab.CurrentPath);
        SaveConfiguration();
        UpdateFavorites();
        GlobalStatus = "已收藏当前目录。";
    }

    private void OpenFavorite(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        var mount = _configuration.Workspaces.SelectMany(x => x.Mounts).FirstOrDefault(x => path.StartsWith(x.Path, StringComparison.OrdinalIgnoreCase));
        if (mount is null) { OpenLocal(path); return; }
        var workspace = _configuration.Workspaces.First(x => x.Mounts.Contains(mount));
        OpenMount(workspace, mount);
        SelectedTab?.Navigate(path);
    }

    private void UpdateFavorites()
    {
        Favorites.Clear();
        foreach (var favorite in _configuration.FavoriteFolders) Favorites.Add(favorite);
    }

    private void ExportCatalog()
    {
        var workspace = SelectedNode?.Workspace ?? (SelectedTab?.IsLocal == false ? SelectedTab.Workspace : null);
        if (workspace is null) return;
        var count = 0;
        foreach (var mount in workspace.Mounts.Where(x => Directory.Exists(x.Path)))
        {
            new MetadataStore(mount.Path).ExportCatalogSnapshot(workspace);
            count++;
        }
        GlobalStatus = $"已把分类树与编目快照导出到 {count} 个挂载文件夹的 .7z/catalog/。";
    }

    private void CloseTab(MountTabViewModel? tab)
    {
        if (tab is null) return;
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (SelectedTab == tab) SelectedTab = Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : null;
    }

    private void ToggleTheme()
    {
        _configuration.Theme = _configuration.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        ThemeService.Apply(_configuration.Theme);
        SaveConfiguration();
    }

    private void MonitorChanged(object? sender, string path)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var tab = Tabs.FirstOrDefault(x => x.Mount.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (tab is null) return;
            tab.Refresh();
            GlobalStatus = $"{tab.Title}：检测到文件变更（提示模式，不会自动上传）。";
        });
    }

    private void SaveConfiguration() => _store.Save(_configuration);

    private void RefreshCommands()
    {
        UnmountCommand.NotifyCanExecuteChanged();
        AssignCategoryCommand.NotifyCanExecuteChanged();
        BindGitCommand.NotifyCanExecuteChanged();
        PullCommand.NotifyCanExecuteChanged();
        SavePushCommand.NotifyCanExecuteChanged();
        HistoryCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
        ArchiveHistoryCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        AddFavoriteCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor.Dispose();
    }
}
