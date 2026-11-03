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

public enum SidebarSection { Workspace, Local, Remote }

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
    private SidebarSection _sidebarSection = SidebarSection.Workspace;
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
        NewSpaceCommand = new AsyncRelayCommand(NewSpaceAsync);
        NewDirectoryCommand = new AsyncRelayCommand(NewDirectoryAsync);
        AssignCategoryCommand = new RelayCommand(AssignCategory, () => SelectedTab?.IsLocal == false && SelectedTab.SelectedFile is not null);
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
        AddFavoriteCommand = new RelayCommand(AddFavorite, () => SelectedTab is not null && !SelectedTab.IsComputer);
        OpenFavoriteCommand = new RelayCommand<string>(OpenFavorite);
        OpenLocalLocationCommand = new RelayCommand<string>(path => { if (!string.IsNullOrWhiteSpace(path)) OpenLocal(path); });
        OpenLocalFolderCommand = new RelayCommand(OpenLocalFolder);
        RefreshLocalFoldersCommand = new RelayCommand(RefreshLocalFolders);
        ShowWorkspaceSidebarCommand = new RelayCommand(() => SidebarSection = SidebarSection.Workspace);
        ShowLocalSidebarCommand = new RelayCommand(() => SidebarSection = SidebarSection.Local);
        ShowRemoteSidebarCommand = new RelayCommand(() => SidebarSection = SidebarSection.Remote);
        AddRemoteLocationCommand = new RelayCommand(AddRemoteLocation);
        OpenRemoteLocationCommand = new AsyncRelayCommand<RemoteLocationDefinition>(OpenRemoteAsync);
        MountRemoteCommand = new AsyncRelayCommand(MountRemoteAsync);
        EditSpaceCommand = new RelayCommand<WorkspaceNodeViewModel>(EditSpace);
        EditDirectoryCommand = new RelayCommand<WorkspaceNodeViewModel>(EditDirectory);
        DeleteNodeCommand = new RelayCommand(DeleteNode, () => SelectedNode is not null);
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarVisible = !IsSidebarVisible);
        ToggleAssistantCommand = new RelayCommand(() => IsAssistantOpen = !IsAssistantOpen);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        _monitor.Changed += MonitorChanged;
        Load();
    }

    public AppConfiguration Configuration => _configuration;
    public ObservableCollection<WorkspaceNodeViewModel> Workspaces { get; } = [];
    public ObservableCollection<LocalFolderViewModel> LocalFolders { get; } = [];
    public ObservableCollection<LocalFolderViewModel> QuickFolders { get; } = [];
    public ObservableCollection<LocalFolderViewModel> RecentFolders { get; } = [];
    public ObservableCollection<RemoteLocationDefinition> RemoteLocations { get; } = [];
    public ObservableCollection<MountTabViewModel> Tabs { get; } = [];
    /// <summary>标签栏展示集合：真实标签 + 末尾固定宽的“＋”占位标签。</summary>
    public ObservableCollection<MountTabViewModel> TabItems { get; } = [];
    public ObservableCollection<CatalogResult> CatalogResults { get; } = [];
    public ObservableCollection<PluginManifest> Plugins { get; } = [];
    public ObservableCollection<string> Favorites { get; } = [];
    public AsyncRelayCommand NewSpaceCommand { get; }
    public AsyncRelayCommand NewDirectoryCommand { get; }
    public RelayCommand<WorkspaceNodeViewModel> EditSpaceCommand { get; }
    public RelayCommand<WorkspaceNodeViewModel> EditDirectoryCommand { get; }
    public RelayCommand DeleteNodeCommand { get; }
    public AsyncRelayCommand MountRemoteCommand { get; }
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
    public RelayCommand<string> OpenLocalLocationCommand { get; }
    public RelayCommand OpenLocalFolderCommand { get; }
    public RelayCommand RefreshLocalFoldersCommand { get; }
    public RelayCommand ShowWorkspaceSidebarCommand { get; }
    public RelayCommand ShowLocalSidebarCommand { get; }
    public RelayCommand ShowRemoteSidebarCommand { get; }
    public RelayCommand AddRemoteLocationCommand { get; }
    public AsyncRelayCommand<RemoteLocationDefinition> OpenRemoteLocationCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand ToggleAssistantCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public string Version => "开源版 v0.3.0";
    public string PluginSummary => Plugins.Count == 0 ? "智能服务未启用" : $"发现 {Plugins.Count} 个插件";

    public WorkspaceNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetProperty(ref _selectedNode, value)) return;
            RefreshCommands();
            UpdateFavorites();
            OpenNode(value);
        }
    }

    public void OpenNode(WorkspaceNodeViewModel? node)
    {
        if (node?.Kind == WorkspaceNodeKind.Space && node.Space is not null) OpenSpace(node.Space);
        else if (node?.Kind == WorkspaceNodeKind.Directory && node.Directory is not null) ShowCatalog(node.Directory);
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
            OnPropertyChanged(nameof(SelectedTabItem));
            RefreshCommands();
            UpdateFavorites();
        }
    }

    /// <summary>标签栏中应处于选中态的项（占位标签不参与选中）。</summary>
    public MountTabViewModel? SelectedTabItem
    {
        get => _selectedTab;
        set
        {
            if (value is null || value.IsPlaceholder) return;
            SelectedTab = value;
            OnPropertyChanged();
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
    public SidebarSection SidebarSection
    {
        get => _sidebarSection;
        private set
        {
            if (!SetProperty(ref _sidebarSection, value)) return;
            OnPropertyChanged(nameof(IsWorkspaceSidebar));
            OnPropertyChanged(nameof(IsLocalSidebar));
            OnPropertyChanged(nameof(IsRemoteSidebar));
        }
    }
    public bool IsWorkspaceSidebar => SidebarSection == SidebarSection.Workspace;
    public bool IsLocalSidebar => SidebarSection == SidebarSection.Local;
    public bool IsRemoteSidebar => SidebarSection == SidebarSection.Remote;
    public bool IsAssistantOpen { get => _isAssistantOpen; set => SetProperty(ref _isAssistantOpen, value); }

    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetProperty(ref _isBusy, value)) RefreshCommands(); }
    }

    private void Load()
    {
        TabItems.Add(MountTabViewModel.CreatePlaceholder());
        _dialogs.SetDirectories(_configuration.Directories);
        MigrateRemoteLocations();
        RefreshRemoteLocations();
        RebuildTree();
        RefreshLocalFolders();
        foreach (var plugin in _plugins.Discover()) Plugins.Add(plugin);
        OnPropertyChanged(nameof(PluginSummary));
        foreach (var space in _configuration.Spaces.Where(x => Directory.Exists(x.Path)))
        {
            try { new MetadataStore(space.Path).Initialize(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { GlobalStatus = $"元数据初始化失败：{ex.Message}"; }
        }
        var first = _configuration.Spaces.FirstOrDefault(x => Directory.Exists(x.Path));
        if (first is not null) OpenSpace(first);
        UpdateFavorites();
    }

    private void RefreshLocalFolders()
    {
        QuickFolders.Clear();
        AddQuickFolder("主文件夹", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddQuickFolder("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddQuickFolder("下载", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        AddQuickFolder("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddQuickFolder("图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddQuickFolder("音乐", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AddQuickFolder("视频", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        RefreshRecentFolders();
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

    private void AddQuickFolder(string name, string path)
    {
        if (Directory.Exists(path)) QuickFolders.Add(new LocalFolderViewModel(path, name, _files, placeholder: true));
    }

    private void RefreshRecentFolders()
    {
        RecentFolders.Clear();
        foreach (var path in _configuration.RecentLocalFolders.Where(Directory.Exists).Take(8))
        {
            var name = new DirectoryInfo(path).Name is { Length: > 0 } value ? value : path;
            RecentFolders.Add(new LocalFolderViewModel(path, name, _files, placeholder: true));
        }
    }

    /// <summary>重建「目录 → 空间」树：目录为逻辑分组，空间为物理位置。</summary>
    private void RebuildTree()
    {
        Workspaces.Clear();
        var directoryNodes = _configuration.Directories
            .ToDictionary(x => x.Id, x => new WorkspaceNodeViewModel(WorkspaceNodeKind.Directory, x.Name) { Directory = x });
        foreach (var node in directoryNodes.Values)
        {
            node.IsExpanded = true;
            if (node.Directory?.ParentId is { } parentId && directoryNodes.TryGetValue(parentId, out var parent)) parent.Children.Add(node);
            else Workspaces.Add(node);
        }
        foreach (var space in _configuration.Spaces)
        {
            var node = new WorkspaceNodeViewModel(WorkspaceNodeKind.Space, space.Name) { Space = space };
            if (space.DirectoryId is { } directoryId && directoryNodes.TryGetValue(directoryId, out var owner)) owner.Children.Add(node);
            else Workspaces.Add(node);
        }
        foreach (var node in Workspaces) node.IsExpanded = true;
    }

    private async Task NewSpaceAsync()
    {
        var result = _dialogs.ShowNewSpace();
        if (result is null) return;
        var space = new SpaceDefinition
        {
            Name = result.Name.Trim(),
            Path = result.Path,
            RemoteUrl = result.RemoteUrl,
            RemoteKind = result.RemoteKind
        };
        if (!string.IsNullOrWhiteSpace(space.Path))
        {
            var duplicated = _configuration.Spaces.Any(x => x.Path.Equals(space.Path, StringComparison.OrdinalIgnoreCase));
            if (duplicated)
            {
                _dialogs.Info("这个位置已经是空间了，无需重复添加。");
                return;
            }
            if (result.DirectoryId is { } directoryId && _configuration.Directories.Any(x => x.Id == directoryId)) space.DirectoryId = directoryId;
            IsBusy = true;
            GlobalStatus = $"正在建立空间“{space.Name}”…";
            try
            {
                await Task.Run(() => { var metadata = new MetadataStore(space.Path); metadata.Initialize(); metadata.IndexFiles(); });
            }
            catch (Exception ex) { _dialogs.Info($"空间初始化失败：{ex.Message}"); GlobalStatus = "空间初始化失败"; IsBusy = false; return; }
            IsBusy = false;
        }
        _configuration.Spaces.Add(space);
        SaveConfiguration();
        RebuildTree();
        SelectedNode = FindSpaceNode(space.Id);
        GlobalStatus = $"已创建空间“{space.Name}”。";
        if (!string.IsNullOrWhiteSpace(space.Path)) OpenSpace(space);
    }

    private Task NewDirectoryAsync()
    {
        var name = _dialogs.Prompt("新建目录", "目录名称（用于逻辑归类空间）");
        if (string.IsNullOrWhiteSpace(name)) return Task.CompletedTask;
        var directory = new DirectoryDefinition { Name = name.Trim() };
        if (SelectedNode is { Kind: WorkspaceNodeKind.Directory, Directory: { } parent }) directory.ParentId = parent.Id;
        _configuration.Directories.Add(directory);
        SaveConfiguration();
        _dialogs.SetDirectories(_configuration.Directories);
        RebuildTree();
        GlobalStatus = $"已创建目录“{directory.Name}”。";
        return Task.CompletedTask;
    }

    private void EditSpace(WorkspaceNodeViewModel? node)
    {
        if (node?.Kind != WorkspaceNodeKind.Space || node.Space is null) return;
        var space = node.Space;
        var result = _dialogs.ShowEditSpace(space.Name);
        if (result is null) return;
        if (result.DeleteRequested) { DeleteSpace(space); return; }
        space.Name = result.Name.Trim();
        SaveConfiguration();
        RebuildTree();
        GlobalStatus = $"空间已重命名为“{space.Name}”。";
    }

    private void EditDirectory(WorkspaceNodeViewModel? node)
    {
        if (node?.Kind != WorkspaceNodeKind.Directory || node.Directory is null) return;
        var directory = node.Directory;
        var name = _dialogs.Prompt("重命名目录", "目录名称", directory.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        directory.Name = name.Trim();
        SaveConfiguration();
        RebuildTree();
        GlobalStatus = $"目录已重命名为“{directory.Name}”。";
    }

    private void DeleteNode()
    {
        if (SelectedNode is not { } node) return;
        if (node.Kind == WorkspaceNodeKind.Space) { if (node.Space is not null) DeleteSpace(node.Space); return; }
        if (node.Directory is not { } directory) return;
        if (!_dialogs.Confirm($"删除目录“{directory.Name}”？\n\n目录中的空间会移到上一层，不会删除任何物理文件。")) return;
        foreach (var space in _configuration.Spaces.Where(x => x.DirectoryId == directory.Id)) space.DirectoryId = directory.ParentId;
        foreach (var child in _configuration.Directories.Where(x => x.ParentId == directory.Id)) child.ParentId = directory.ParentId;
        _configuration.Directories.Remove(directory);
        SelectedNode = null;
        SaveConfiguration();
        _dialogs.SetDirectories(_configuration.Directories);
        RebuildTree();
        GlobalStatus = $"已删除目录“{directory.Name}”，其中的空间已上移。";
    }

    private void DeleteSpace(SpaceDefinition space)
    {
        if (!_dialogs.Confirm($"删除空间“{space.Name}”？\n\n将删除该位置中的 .7z 元数据目录，但不会删除资料文件。")) return;
        _monitor.Stop(space.Path);
        try
        {
            var metadataPath = Path.Combine(space.Path, ".7z");
            if (Directory.Exists(metadataPath)) Directory.Delete(metadataPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Info($"元数据删除失败：{ex.Message}");
            return;
        }
        foreach (var tab in Tabs.Where(x => !x.IsLocal && x.Space.Id == space.Id).ToArray()) CloseTab(tab);
        _configuration.Spaces.Remove(space);
        SelectedNode = null;
        IsCatalogView = false;
        SaveConfiguration();
        RebuildTree();
        GlobalStatus = $"已删除空间“{space.Name}”及其元数据，资料文件未改动。";
    }

    private void AssignCategory()
    {
        if (SelectedTab is not { IsLocal: false } tab || tab.SelectedFile is not { } file) return;
        var directories = _configuration.Directories
            .Where(x => _configuration.Spaces.Any(s => s.DirectoryId == x.Id))
            .ToList();
        if (directories.Count == 0) { _dialogs.Info("还没有可用于归类的目录。请先新建目录。"); return; }
        var name = _dialogs.Prompt("编目到目录", $"把“{file.Name}”归入哪个目录？\n可选：{string.Join("、", directories.Select(x => x.Name))}", directories[0].Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var directory = directories.FirstOrDefault(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (directory is null) { _dialogs.Info("找不到这个目录。"); return; }
        try
        {
            var metadata = new MetadataStore(tab.Space.Path);
            metadata.AddLink(tab.Space.Id, directory.Id, file.RelativePath);
            metadata.ExportCatalogSnapshot(tab.Space, _configuration.Directories);
            GlobalStatus = $"已将“{file.Name}”编入“{directory.Name}”。";
        }
        catch (Exception ex) { _dialogs.Info($"编目失败：{ex.Message}"); }
    }

    private void ShowCatalog(DirectoryDefinition directory)
    {
        CatalogResults.Clear();
        foreach (var space in _configuration.Spaces.Where(x => x.DirectoryId == directory.Id && Directory.Exists(x.Path)))
        {
            try
            {
                var metadata = new MetadataStore(space.Path);
                foreach (var link in metadata.GetLinks(space.Id, directory.Id))
                    CatalogResults.Add(new CatalogResult(space.Id, space.Name, space.Path, link.RelativePath, Path.Combine(space.Path, link.RelativePath), link.FileHash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { GlobalStatus = $"读取编目失败：{ex.Message}"; }
        }
        CatalogTitle = $"{directory.Name} · {CatalogResults.Count} 项";
        IsCatalogView = true;
    }

    private void OpenCatalogResult()
    {
        if (SelectedCatalogResult is not { } result) return;
        var space = _configuration.Spaces.FirstOrDefault(x => x.Id == result.SpaceId);
        if (space is null) return;
        OpenSpace(space);
        SelectedTab?.SelectCatalogFile(result.FullPath);
    }

    private void OpenSpace(SpaceDefinition space, bool forceNew = false)
    {
        if (!Directory.Exists(space.Path)) { _dialogs.Info("该位置当前离线。重新连接磁盘或网络后即可使用。"); return; }
        var tab = forceNew ? null : Tabs.FirstOrDefault(x => !x.IsLocal && x.Space.Id == space.Id);
        if (tab is null)
        {
            tab = new MountTabViewModel(space, _files, _git);
            AttachTab(tab);
            AddTab(tab);
        }
        SelectedTab = tab;
        if (_configuration.AutoDetect) _monitor.Start(space.Path);
    }

    private void AttachTab(MountTabViewModel tab)
    {
        tab.SelectionChanged += (_, _) => RefreshCommands();
        tab.GitStatusChanged += (_, _) => { if (SelectedTab == tab) RefreshCommands(); };
    }

    /// <summary>新增真实标签，并把它插到末尾的“＋”占位标签之前。</summary>
    private void AddTab(MountTabViewModel tab)
    {
        Tabs.Add(tab);
        var placeholderIndex = TabItems.Count - 1;
        TabItems.Insert(placeholderIndex < 0 ? TabItems.Count : placeholderIndex, tab);
    }

    private void RemoveTab(MountTabViewModel tab)
    {
        Tabs.Remove(tab);
        TabItems.Remove(tab);
    }

    private void NewTab()
    {
        var tab = new MountTabViewModel(_files, _git);
        AttachTab(tab);
        AddTab(tab);
        SelectedTab = tab;
        GlobalStatus = "已打开我的电脑。";
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
            TrackRecent(path);
            return;
        }
        var tab = forceNew ? null : Tabs.FirstOrDefault(x => x.IsLocal && path.StartsWith(x.RootPath, StringComparison.OrdinalIgnoreCase));
        if (tab is null)
        {
            tab = new MountTabViewModel(path, _files, _git);
            AttachTab(tab);
            AddTab(tab);
        }
        else if (!tab.CurrentPath.Equals(path, StringComparison.OrdinalIgnoreCase)) tab.Navigate(path);
        SelectedTab = tab;
        TrackRecent(path);
        GlobalStatus = $"正在浏览 {path}";
    }

    private void TrackRecent(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _configuration.RecentLocalFolders.RemoveAll(x => x.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        _configuration.RecentLocalFolders.Insert(0, fullPath);
        if (_configuration.RecentLocalFolders.Count > 12) _configuration.RecentLocalFolders.RemoveRange(12, _configuration.RecentLocalFolders.Count - 12);
        SaveConfiguration();
        // 不立即刷新“最近访问”列表，避免刚点开的条目瞬间跳到第一位；列表在下次启动时更新排序。
    }

    private void MigrateRemoteLocations()
    {
        var before = _configuration.RemoteLocations.Count;
        foreach (var space in _configuration.Spaces.Where(x => !string.IsNullOrWhiteSpace(x.RemoteUrl)))
            RememberRemoteLocation(new RemoteLocationDefinition
            {
                Name = space.Name,
                Kind = space.RemoteKind,
                Address = space.RemoteUrl,
                LocalPath = space.Path
            });
        if (IsRemoteArchiveAddress(_configuration.ArchiveRoot))
            RememberRemoteLocation(new RemoteLocationDefinition
            {
                Name = "rsync 归档目录",
                Kind = RemoteLocationKind.Rsync,
                Address = _configuration.ArchiveRoot,
                LocalPath = CreateCachePath()
            });
        if (_configuration.RemoteLocations.Count != before) SaveConfiguration();
    }

    private static bool IsRemoteArchiveAddress(string path) => !string.IsNullOrWhiteSpace(path)
        && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal)
            || (path.Contains(':') && !Path.IsPathRooted(path)));

    private static string CreateCachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KBEA", "RemoteCache", Guid.NewGuid().ToString("N"));

    private RemoteLocationDefinition RememberRemoteLocation(RemoteLocationDefinition location)
    {
        var existing = _configuration.RemoteLocations.FirstOrDefault(x => x.Kind == location.Kind
            && x.Address.Equals(location.Address, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (string.IsNullOrWhiteSpace(existing.LocalPath) && !string.IsNullOrWhiteSpace(location.LocalPath))
                existing.LocalPath = location.LocalPath;
            return existing;
        }
        _configuration.RemoteLocations.Add(location);
        return location;
    }

    private void RefreshRemoteLocations()
    {
        RemoteLocations.Clear();
        foreach (var location in _configuration.RemoteLocations) RemoteLocations.Add(location);
    }

    private void AddRemoteLocation()
    {
        var location = _dialogs.ShowRemoteLocation();
        if (location is null) return;
        RememberRemoteLocation(location);
        SaveConfiguration();
        RefreshRemoteLocations();
        GlobalStatus = $"已保存远程位置“{location.Name}”。";
    }

    private async Task OpenRemoteAsync(RemoteLocationDefinition? location)
    {
        if (location is null || IsBusy) return;
        IsBusy = true;
        GlobalStatus = $"正在打开远程位置 {location.Name}…";
        try
        {
            var path = await Task.Run(() => PrepareRemoteLocation(location));
            SaveConfiguration();
            OpenLocal(path);
            GlobalStatus = location.Kind == RemoteLocationKind.Rsync
                ? $"已打开 {location.Name} 的本地缓存；尚未执行实时 rsync 浏览。"
                : $"已打开 {location.Name}：{path}";
        }
        catch (Exception ex)
        {
            GlobalStatus = "远程位置打开失败";
            _dialogs.Info($"远程位置打开失败：{ex.Message}");
        }
        finally { IsBusy = false; }
    }

    private string PrepareRemoteLocation(RemoteLocationDefinition location)
    {
        if (location.Kind == RemoteLocationKind.Network)
        {
            if (!Directory.Exists(location.Address)) throw new DirectoryNotFoundException("网络位置当前不可用，请检查地址、网络连接和访问权限。");
            return location.Address;
        }
        if (string.IsNullOrWhiteSpace(location.LocalPath)) location.LocalPath = CreateCachePath();
        if (location.Kind == RemoteLocationKind.Rsync)
        {
            Directory.CreateDirectory(location.LocalPath);
            return location.LocalPath;
        }
        if (Directory.Exists(location.LocalPath) && Directory.EnumerateFileSystemEntries(location.LocalPath).Any())
            return location.LocalPath;
        return _git.CloneRepository(location.Address, location.LocalPath);
    }

    private async Task MountRemoteAsync()
    {
        var location = _dialogs.ShowRemoteLocation();
        if (location is null) return;
        location = RememberRemoteLocation(location);
        SaveConfiguration();
        RefreshRemoteLocations();
        IsBusy = true;
        GlobalStatus = $"正在挂接远程位置 {location.Name}…";
        try
        {
            var path = await Task.Run(() => PrepareRemoteLocation(location));
            if (_configuration.Spaces.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                _dialogs.Info("这个远程位置已经是空间了，无需重复添加。");
                return;
            }
            var space = new SpaceDefinition
            {
                Name = location.Name,
                Path = path,
                RemoteUrl = location.Kind == RemoteLocationKind.Git ? location.Address : string.Empty,
                RemoteKind = location.Kind
            };
            if (SelectedNode is { Kind: WorkspaceNodeKind.Directory, Directory: { } parent }) space.DirectoryId = parent.Id;
            await Task.Run(() => { var metadata = new MetadataStore(path); metadata.Initialize(); metadata.IndexFiles(); });
            _configuration.Spaces.Add(space);
            SaveConfiguration();
            RebuildTree();
            OpenSpace(space);
            GlobalStatus = $"已挂接 {space.Name}，隐藏元数据目录与文件名索引已建立。";
        }
        catch (Exception ex)
        {
            GlobalStatus = "远程位置挂接失败";
            _dialogs.Info($"远程位置挂接失败：{ex.Message}");
        }
        finally { IsBusy = false; }
    }

    private async Task BindGitAsync()
    {
        if (SelectedTab is not { } tab) return;
        var result = _dialogs.ShowGitRepository(tab.CurrentPath);
        if (result is null) return;
        var repositoryPath = result.RepositoryPath;
        var remote = result.RemoteUrl.Trim();
        await RunOperationAsync(tab, "Git 仓库关联", () => _git.BindRepository(repositoryPath, remote).Display);
        tab.Space.RemoteUrl = remote;
        if (!string.IsNullOrWhiteSpace(remote))
        {
            RememberRemoteLocation(new RemoteLocationDefinition
            {
                Name = new DirectoryInfo(repositoryPath).Name,
                Kind = RemoteLocationKind.Git,
                Address = remote,
                LocalPath = repositoryPath
            });
            RefreshRemoteLocations();
        }
        SaveConfiguration();
        if (!tab.CurrentPath.Equals(repositoryPath, StringComparison.OrdinalIgnoreCase)
            && !tab.CurrentPath.StartsWith(repositoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            OpenLocal(repositoryPath);
    }

    private Task PullAsync() => SelectedTab is { } tab && !string.IsNullOrEmpty(tab.GitRepositoryRoot)
        ? RunOperationAsync(tab, "拉取更新", () => _git.Pull(tab.GitRepositoryRoot, _configuration.AuthorName, _configuration.AuthorEmail))
        : Task.CompletedTask;

    private async Task SavePushAsync()
    {
        if (SelectedTab is not { } tab || string.IsNullOrEmpty(tab.GitRepositoryRoot)) return;
        var message = _dialogs.Prompt("保存并上传", "提交说明", $"更新资料 {DateTime.Now:MM-dd HH:mm}");
        if (message is null) return;
        await RunOperationAsync(tab, "保存并上传", () => _git.SaveAndPush(tab.GitRepositoryRoot, message, _configuration.AuthorName, _configuration.AuthorEmail));
    }

    private async Task RunOperationAsync(MountTabViewModel tab, string operation, Func<string> action)
    {
        IsBusy = true;
        GlobalStatus = $"{tab.Title}：正在{operation}…";
        try
        {
            var message = await Task.Run(action);
            tab.Space.LastSyncAt = DateTimeOffset.Now;
            if (!tab.IsLocal) new MetadataStore(tab.Space.Path).RecordSync(operation, "成功", message);
            SaveConfiguration();
            tab.Refresh();
            GlobalStatus = $"{tab.Title}：{message}";
        }
        catch (Exception ex)
        {
            try { if (!tab.IsLocal) new MetadataStore(tab.Space.Path).RecordSync(operation, "失败", ex.Message); } catch { }
            GlobalStatus = $"{tab.Title}：{operation}失败";
            _dialogs.Info($"{operation}失败：{ex.Message}");
        }
        finally { IsBusy = false; }
    }

    private void ShowHistory()
    {
        if (SelectedTab is { } tab && !string.IsNullOrEmpty(tab.GitRepositoryRoot))
            _dialogs.ShowHistory(_git.GetHistory(tab.GitRepositoryRoot), $"{tab.Title} · Git 历史");
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
            var record = await Task.Run(() => _archive.CreateAsync(tab.Space, _configuration, note, progress));
            GlobalStatus = $"{tab.Title}：关键节点 {record.NodeName} 已归档。";
        }
        catch (Exception ex) { GlobalStatus = "归档失败"; _dialogs.Info($"归档失败：{ex.Message}"); }
        finally { IsBusy = false; }
    }

    private void ShowArchiveHistory()
    {
        if (SelectedTab is { IsLocal: false } tab) _dialogs.ShowArchives(_archive.GetHistory(tab.Space.Path), $"{tab.Title} · 关键节点");
    }

    private void ShowSettings()
    {
        if (!_dialogs.ShowSettings(_configuration)) return;
        SaveConfiguration();
        ThemeService.Apply(_configuration.Theme);
        foreach (var space in _configuration.Spaces)
        {
            if (_configuration.AutoDetect) _monitor.Start(space.Path);
            else _monitor.Stop(space.Path);
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
        var space = _configuration.Spaces.FirstOrDefault(x => path.StartsWith(x.Path, StringComparison.OrdinalIgnoreCase));
        if (space is null) { OpenLocal(path); return; }
        OpenSpace(space);
        SelectedTab?.Navigate(path);
    }

    private void UpdateFavorites()
    {
        Favorites.Clear();
        foreach (var favorite in _configuration.FavoriteFolders) Favorites.Add(favorite);
    }

    private void CloseTab(MountTabViewModel? tab)
    {
        if (tab is null) return;
        var index = Tabs.IndexOf(tab);
        RemoveTab(tab);
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
            var tab = Tabs.FirstOrDefault(x => x.Space.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (tab is null) return;
            tab.Refresh();
            GlobalStatus = $"{tab.Title}：检测到文件变更（提示模式，不会自动上传）。";
        });
    }

    private WorkspaceNodeViewModel? FindSpaceNode(Guid spaceId) =>
        Workspaces.SelectMany(Flatten).FirstOrDefault(x => x.Space?.Id == spaceId);

    private static IEnumerable<WorkspaceNodeViewModel> Flatten(WorkspaceNodeViewModel node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var item in Flatten(child)) yield return item;
    }

    private void SaveConfiguration() => _store.Save(_configuration);

    private void RefreshCommands()
    {
        DeleteNodeCommand.NotifyCanExecuteChanged();
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
