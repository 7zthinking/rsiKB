using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KBEA.App.Branding;
using KBEA.App.Services;
using KBEA.Core.Archive;
using KBEA.Core.Files;
using KBEA.Core.Git;
using KBEA.Core.Knowledge;
using KBEA.Core.Models;
using KBEA.Core.Plugins;
using KBEA.Core.Services;
using KBEA.Core.Storage;
using KBEA.Core.Team;

namespace KBEA.App.ViewModels;

public enum SidebarSection { Workspace, Local, Remote }

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceStore _store;
    private readonly FileBrowserService _files;
    // GitService 已迁入 kbea-plugin-git（P2）：实现经 GitOperationsProvider 解析，插件未加载时为 null → Git 功能优雅降级
    private static IGitOperations? Git => GitOperationsProvider.Instance;
    // rsync 归档已迁入 kbea-plugin-rsync：实现经 ArchiveOperationsProvider 解析，插件未加载时为 null → 归档功能优雅降级
    private static IArchiveOperations? Archive => ArchiveOperationsProvider.Instance;
    private readonly PluginCatalog _plugins;
    private readonly IDialogService _dialogs;
    private readonly ChangeMonitor _monitor = new();
    private readonly WorkerClient _worker;
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
    private readonly TeamShareService _team;
    private PluginHostService? _pluginHost;

    public MainViewModel(WorkspaceStore store, FileBrowserService files, PluginCatalog plugins, IDialogService dialogs, WorkerClient worker)
    {
        _store = store;
        _files = files;
        _plugins = plugins;
        _dialogs = dialogs;
        _worker = worker;
        _configuration = store.Load();
        _team = new TeamShareService(
            id => _configuration.Spaces.FirstOrDefault(s => s.Id == id),
            () => new TeamShareService.TeamSettings(_configuration.TeamCacheLimitGb, _configuration.TeamSyncIntervalMinutes, _configuration.TeamServicePort),
            message => GlobalStatus = message);
        _team.PeersChanged += OnTeamPresenceChanged;
        _team.SharesChanged += OnTeamSharesChanged;
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
        AddFavoriteCommand = new RelayCommand(ToggleFavorite, () => SelectedTab is not null && !SelectedTab.IsComputer);
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
        KnowledgeSearchCommand = new AsyncRelayCommand(RunKnowledgeSearchAsync, () => SelectedTab is not null && !IsBusy);
        ShowMetadataConfigCommand = new RelayCommand(ShowMetadataConfig);
        CompareConflictCommand = new RelayCommand(CompareConflict, () =>
            SelectedTab?.SelectedFile is { IsDirectory: false });
        // 按钮对任意选中文件可用（HEAD 基线 vs 工作区比对）；非 Git 目录时窗口内提示。
        // 早期只在 GitState=="冲突" 时可用，导致按钮长期灰着、被误认为未实现。
        ShowSpaceMetadataCommand = new RelayCommand<WorkspaceNodeViewModel>(ShowSpaceMetadata, node => node?.IsSpace == true && node.Space is not null);
        RunSpaceMetadataCommand = new RelayCommand<WorkspaceNodeViewModel>(RunSpaceMetadata, node => node?.IsSpace == true && node.Space is not null);
        SwitchTabRemoteCommand = new AsyncRelayCommand<RemoteLocationDefinition>(SwitchTabSourceToRemote);
        ShowTeamManagerCommand = new RelayCommand(ShowTeamManager);
        RefreshTeamCommand = new AsyncRelayCommand(RefreshTeamAsync);
        AddSharedToWorkspaceCommand = new RelayCommand<SharedSpaceItem>(AddSharedToWorkspace, item => item is not null);
        OpenSharedItemCommand = new RelayCommand<SharedSpaceItem>(OpenSharedItem, item => item is not null);
        OpenTeamShareNodeCommand = new RelayCommand<SharedSpaceItem>(OpenTeamShareNode, item => item is not null);
        KnowledgeSearch.OpenRequested = OpenSearchHit;
        KnowledgeSearch.OpenFolderRequested = OpenSearchHitFolder;
        KnowledgeSearch.IconResolver = ResolveHitIcon;
        KnowledgeSearch.PreviewLoader = path => LoadPreviewFor(path);
        // IsSearchView/IsBrowserView 依赖 KnowledgeSearch.IsSearching（跨 ViewModel 计算属性），
        // 必须转发通知，否则 Reset() 退出检索视图时 UI 不会切换
        KnowledgeSearch.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(KnowledgeSearchViewModel.IsSearching))
            {
                OnPropertyChanged(nameof(IsSearchView));
                OnPropertyChanged(nameof(IsBrowserView));
                OnPropertyChanged(nameof(IsEmptyView));
                OnPropertyChanged(nameof(IsBrowserChromeVisible));
            }
        };
        _monitor.Changed += MonitorChanged;
        Load();
    }

    public AppConfiguration Configuration => _configuration;
    public ObservableCollection<WorkspaceNodeViewModel> Workspaces { get; } = [];
    public ObservableCollection<LocalFolderViewModel> LocalFolders { get; } = [];
    public ObservableCollection<LocalFolderViewModel> QuickFolders { get; } = [];
    public ObservableCollection<LocalFolderViewModel> RecentFolders { get; } = [];
    public ObservableCollection<RemoteLocationDefinition> RemoteLocations { get; } = [];
    /// <summary>侧栏团队共享树（我创建的团队）：团队 → 有共享的成员（含本机） → 空间。</summary>
    public ObservableCollection<TeamShareTeamNode> MyTeamTree { get; } = [];
    /// <summary>侧栏团队共享树（加入的团队）。</summary>
    public ObservableCollection<TeamShareTeamNode> JoinedTeamTree { get; } = [];
    /// <summary>未加入任何其他团队 → 显示提示文案。</summary>
    public bool JoinedTeamEmpty { get; private set; } = true;
    public ObservableCollection<MountTabViewModel> Tabs { get; } = [];
    /// <summary>标签栏展示集合：真实标签 + 末尾固定宽的“＋”占位标签。</summary>
    public ObservableCollection<MountTabViewModel> TabItems { get; } = [];
    public ObservableCollection<CatalogResult> CatalogResults { get; } = [];
    public ObservableCollection<PluginManifest> Plugins { get; } = [];
    /// <summary>插件注册的工具栏分组（由 PluginHostService 灌入，经 IUiSurface 切回 UI 线程）。
    /// 组内按钮只含图标/提示/命令，排布与收起由宿主框架负责。</summary>
    public ObservableCollection<ToolbarGroupVm> PluginToolbarGroups { get; } = [];

    /// <summary>语义检索视图状态。</summary>
    public KnowledgeSearchViewModel KnowledgeSearch { get; } = new();
    public ObservableCollection<string> Favorites { get; } = [];

    /// <summary>当前浏览目录是否已收藏（驱动工具栏收藏按钮的星星/文字切换）。</summary>
    public bool IsCurrentPathFavorited => SelectedTab is not null
        && !SelectedTab.IsComputer
        && _configuration.FavoriteFolders.Contains(SelectedTab.CurrentPath);

    /// <summary>工具栏收藏按钮的动态文案：☆ 收藏 / ★ 取消收藏。</summary>
    public string FavoriteButtonText => IsCurrentPathFavorited ? "★ 取消收藏" : "☆ 收藏";
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
    public AsyncRelayCommand KnowledgeSearchCommand { get; }
    public RelayCommand ShowMetadataConfigCommand { get; }
    /// <summary>对选中文件做基线（HEAD）与工作区的版本比对；仅冲突文件可用。</summary>
    public RelayCommand CompareConflictCommand { get; }
    public RelayCommand<WorkspaceNodeViewModel> ShowSpaceMetadataCommand { get; }
    public RelayCommand<WorkspaceNodeViewModel> RunSpaceMetadataCommand { get; }
    /// <summary>把当前标签切换到指定工作空间。</summary>
    /// <summary>把当前标签切换到指定远程位置。</summary>
    public AsyncRelayCommand<RemoteLocationDefinition> SwitchTabRemoteCommand { get; }
    public RelayCommand ShowTeamManagerCommand { get; }
    public AsyncRelayCommand RefreshTeamCommand { get; }
    public RelayCommand<SharedSpaceItem> AddSharedToWorkspaceCommand { get; }
    public RelayCommand<SharedSpaceItem> OpenSharedItemCommand { get; }
    /// <summary>侧栏团队树点击共享空间行（我的团队→开空间；加入的团队→开 WebDAV）。</summary>
    public RelayCommand<SharedSpaceItem> OpenTeamShareNodeCommand { get; }
    public string Version => "开源版 v0.3.0";

    /// <summary>品牌名（侧栏标题）——来自 branding/branding.yaml，缺失时用内置默认值。</summary>
    public string ProductName => BrandingLoader.Current.ProductName;

    /// <summary>窗口标题（品牌名 + 版本后缀）。</summary>
    public string WindowTitle => BrandingLoader.Current.WindowTitle;

    public string PluginSummary => Plugins.Count == 0 ? "智能服务未启用" : $"发现 {Plugins.Count} 个插件";

    public WorkspaceNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetProperty(ref _selectedNode, value)) return;
            RefreshCommands();
            UpdateFavorites();
            // 程序化同步选中态时不要触发导航（否则会把用户当前目录重置到空间根）
            if (!_suppressOpenNode) OpenNode(value);
        }
    }

    /// <summary>同步选中态时抑制自动导航。</summary>
    private bool _suppressOpenNode;

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
            OnPropertyChanged(nameof(IsBrowserChromeVisible));
            OnPropertyChanged(nameof(SearchAddressText));
            OnPropertyChanged(nameof(SelectedTabItem));
            OnPropertyChanged(nameof(IsCurrentPathFavorited));
            OnPropertyChanged(nameof(FavoriteButtonText));
            RefreshCommands();
            UpdateFavorites();
            // 标签切换 → 左侧栏自动跳到对应类别并选中项
            SyncSidebarToTab();
            // 快照事件：插件据此感知宿主状态变化
            _pluginHost?.Refresh();
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

    public string GlobalStatus
    {
        get => _globalStatus;
        set { if (SetProperty(ref _globalStatus, value)) _pluginHost?.Refresh(); }
    }
    public string CatalogTitle { get => _catalogTitle; set => SetProperty(ref _catalogTitle, value); }
    public bool IsCatalogView
    {
        get => _isCatalogView;
        set
        {
            if (!SetProperty(ref _isCatalogView, value)) return;
            OnPropertyChanged(nameof(IsBrowserView));
            OnPropertyChanged(nameof(IsEmptyView));
            OnPropertyChanged(nameof(IsBrowserChromeVisible));
        }
    }
    public bool IsBrowserView => !IsCatalogView && !IsSearchView && SelectedTab is not null;
    public bool IsEmptyView => !IsCatalogView && !IsSearchView && SelectedTab is null;
    public bool IsSearchView => KnowledgeSearch.IsSearching;
    // 编目视图（选目录节点）独占整个内容区，此时隐藏浏览器 chrome（地址栏/工具栏），避免与编目列表叠加
    public bool IsBrowserChromeVisible => !IsCatalogView && (SelectedTab is not null || IsSearchView);
    /// <summary>检索结果态的地址栏文案：显示结果归属的位置。</summary>
    public string SearchAddressText => $"搜索结果：位置“{SelectedTab?.WorkspaceSpace?.Name ?? SelectedTab?.CurrentPath ?? string.Empty}”";
    public bool IsSidebarVisible { get => _isSidebarVisible; set => SetProperty(ref _isSidebarVisible, value); }

    /// <summary>
    /// 左侧栏当前类别。三类互斥：单值枚举，切换即自动取消另外两类的选中状态。
    /// </summary>
    public SidebarSection SidebarSection
    {
        get => _sidebarSection;
        set
        {
            var changed = SetProperty(ref _sidebarSection, value);
            // 即使值没变也要把三个按钮的选中态重新推给 UI：
            // 用户点击已选中的 ToggleButton 时控件会先本地取消勾选，必须再同步回去。
            OnPropertyChanged(nameof(IsWorkspaceSidebar));
            OnPropertyChanged(nameof(IsLocalSidebar));
            OnPropertyChanged(nameof(IsRemoteSidebar));
            if (!changed) return;
            // 手动切换侧栏类别时，三类各自的选中态全部清掉：
            // 标签当前不在这个类别里，旧类别的高亮就是残留，换回来时不能再显示。
            // （标签自身切换来源时的同步走 _syncingSidebar=true 分支，不受影响。）
            if (!_syncingSidebar)
            {
                SelectedNode = null;
                SelectedLocalFolder = null;
                SelectedRemoteLocation = null;
            }
            RefreshCommands();
            _pluginHost?.Refresh();
        }
    }
    public bool IsWorkspaceSidebar => SidebarSection == SidebarSection.Workspace;
    public bool IsLocalSidebar => SidebarSection == SidebarSection.Local;
    public bool IsRemoteSidebar => SidebarSection == SidebarSection.Remote;
    public bool IsAssistantOpen { get => _isAssistantOpen; set => SetProperty(ref _isAssistantOpen, value); }

    /// <summary>同步侧栏时抑制「清空选中态」，由调用方立即重建选中项。</summary>
    private bool _syncingSidebar;

    /// <summary>
    /// 标签切换后，把左侧栏同步到该标签对应的类别，并选中对应条目。
    /// 左侧栏三类互斥由 <see cref="SidebarSection"/> 单值枚举保证。
    /// </summary>
    private void SyncSidebarToTab(bool clearOthers = true)
    {
        if (_suppressSidebarSync) return;
        var tab = SelectedTab;
        if (tab is null) return;

        _syncingSidebar = true;
        try
        {
            // clearOthers=true：标签间切换/手动切换场景，先把三类选中清掉再设目标，杜绝残留。
            // clearOthers=false：同一标签的来源切换（用户点击条目触发）——点击时 ListBox/TreeView
            // 的视觉选中已经正确，此时清空会让刚点的高亮丢失（匹配在导航完成前会短暂失败），
            // 只做目标项设置；匹配不到时保持现状，等最终通知恢复。
            if (clearOthers)
            {
                SelectedLocalFolder = null;
                SelectedRemoteLocation = null;
                SelectedNode = null;
            }
            switch (tab.SourceKind)
            {
                case TabSourceKind.Workspace:
                    SidebarSection = SidebarSection.Workspace;
                    SelectWorkspaceNode(tab.WorkspaceSpace);
                    break;
                case TabSourceKind.Remote:
                    SidebarSection = SidebarSection.Remote;
                    SelectRemoteLocation(tab.RemoteLocation);
                    break;
                default:
                    SidebarSection = SidebarSection.Local;
                    SelectLocalFolder(tab.CurrentPath);
                    break;
            }
        }
        finally { _syncingSidebar = false; }
    }

    /// <summary>在左侧栏里选中对应的工作空间节点（不触发导航）。</summary>
    private void SelectWorkspaceNode(SpaceDefinition? space)
    {
        if (space is null) return;
        var node = FindSpaceNode(space.Id);
        if (node is null) return;
        _suppressOpenNode = true;
        try { SelectedNode = node; }
        finally { _suppressOpenNode = false; }
    }

    /// <summary>在左侧栏里选中对应的本地目录（不触发导航）。匹配不到时保持现状（可能是导航完成前的中间通知）。</summary>
    private void SelectLocalFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var all = LocalFolders.Concat(QuickFolders).Concat(RecentFolders).ToList();
        // 优先精确匹配；否则取路径前缀最长的一项（当前目录可能是某个常用位置的子目录）
        var match = all.FirstOrDefault(x => x.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            ?? all.Where(x => path.StartsWith(x.FullPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(x => x.FullPath.Length)
                   .FirstOrDefault();
        if (match is null) return; // 匹配不到就不清空现有选中，等导航完成后的最终通知
        SelectedLocalFolder = match;
    }

    /// <summary>在左侧栏里选中对应的远程位置（不触发打开）。</summary>
    private void SelectRemoteLocation(RemoteLocationDefinition? location)
    {
        SelectedRemoteLocation = location;
    }

    /// <summary>左侧栏当前选中的本地目录（供 UI 同步高亮）。</summary>
    public LocalFolderViewModel? SelectedLocalFolder
    {
        get => _selectedLocalFolder;
        private set => SetProperty(ref _selectedLocalFolder, value);
    }
    private LocalFolderViewModel? _selectedLocalFolder;

    /// <summary>左侧栏当前选中的远程位置（供 UI 同步高亮）。</summary>
    public RemoteLocationDefinition? SelectedRemoteLocation
    {
        get => _selectedRemoteLocation;
        private set => SetProperty(ref _selectedRemoteLocation, value);
    }
    private RemoteLocationDefinition? _selectedRemoteLocation;

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
        RefreshLocalFolders();
        RebuildTree();
        foreach (var plugin in _plugins.Discover()) Plugins.Add(plugin);
        // P1 插件宿主：发现 → ALC 加载 → 工具栏按钮灌入 PluginToolbarButtons
        _pluginHost = new PluginHostService(this);
        _pluginHost.Load();
        Services.PluginBridge.Instance = _pluginHost;
        OnPropertyChanged(nameof(PluginSummary));
        // git 插件可能在标签初始化之后才注册完成 → 补一次 Git 状态刷新（避免首启标签漏掉 Git 标记）
        _ = SelectedTab?.RefreshGitStatusAsync();
        foreach (var space in _configuration.Spaces.Where(x => Directory.Exists(x.Path)))
        {
            try { new MetadataStore(space.Path).Initialize(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { GlobalStatus = $"元数据初始化失败：{ex.Message}"; }
        }
        // 启动不自动打开空间：侧栏默认停在「工作空间」，主面板显示欢迎页，内容由用户自己点。
        // （此前是自动打开第一个工作空间，用户反馈改为不预开。）
        UpdateFavorites();
        StartTeamShare();
    }

    // === 团队共享 ===

    /// <summary>启动团队共享服务（后台线程：身份/端口/mDNS/首轮同步）。</summary>
    private void StartTeamShare()
    {
        var team = _team;
        Task.Run(() =>
        {
            try
            {
                team.Start();
            }
            catch (Exception ex)
            {
                GlobalStatus = $"团队共享启动失败：{ex.Message}";
            }
            Application.Current?.Dispatcher.BeginInvoke(RefreshTeamUi);
        });
    }

    private void OnTeamPresenceChanged()
    {
        // mDNS 后台线程触发 → 调回 UI 线程刷新
        Application.Current?.Dispatcher.BeginInvoke(RefreshTeamUi);
    }

    private void OnTeamSharesChanged()
    {
        Application.Current?.Dispatcher.BeginInvoke(RefreshTeamUi);
    }

    /// <summary>重建侧栏团队共享树与状态行。</summary>
    private void RefreshTeamUi()
    {
        SharedSpacesTree(_team.Peers.ToDictionary(p => p.MachineCode, p => p.Online));
    }

    /// <summary>侧栏团队共享树：团队 → 有共享空间的成员 → 该成员共享的空间（无共享的成员不显示）。</summary>
    private void SharedSpacesTree(Dictionary<string, bool> peers)
    {
        var myCode = _team.Identity.MachineCode;
        MyTeamTree.Clear();
        JoinedTeamTree.Clear();
        foreach (var team in _team.Teams)
        {
            var isMyTeam = team.IsOwner(myCode);
            var members = new List<TeamShareMemberNode>();
            foreach (var member in team.Members)
            {
                var shares = member.MachineCode == myCode
                    ? team.MyShares
                    : _team.Store.ReadShares(team.Definition.TeamId, member.MachineCode);
                if (shares.Count == 0) continue;
                var online = member.MachineCode == myCode || peers.GetValueOrDefault(member.MachineCode);
                members.Add(new TeamShareMemberNode
                {
                    MachineCode = member.MachineCode,
                    Nickname = member.Nickname,
                    Online = online,
                    Shares = shares.Select(s => new SharedSpaceItem(new SharedEntry
                    {
                        TeamId = team.Definition.TeamId,
                        TeamName = team.Definition.Name,
                        OwnerMachineCode = member.MachineCode,
                        OwnerNickname = member.Nickname,
                        SpaceId = s.SpaceId,
                        SpaceName = s.SpaceName,
                        Level = s.Level,
                        Online = online
                    }) { FromMyTeam = isMyTeam }).ToList()
                });
            }
            var node = new TeamShareTeamNode
            {
                TeamId = team.Definition.TeamId,
                Name = team.Definition.Name,
                MemberSummary = members.Count == 0 ? "暂无共享" : $"{members.Count} 名成员共享中",
                Members = members.OrderBy(n => n.Nickname).ToList()
            };
            if (team.IsOwner(myCode)) MyTeamTree.Add(node);
            else JoinedTeamTree.Add(node);
        }
        JoinedTeamEmpty = JoinedTeamTree.Count == 0;
        OnPropertyChanged(nameof(JoinedTeamEmpty));
    }

    private void ShowTeamManager()
    {
        _dialogs.ShowTeamManager(_team, _configuration, () =>
        {
            RefreshTeamUi();
            RebuildTree();
        });
    }

    private async Task RefreshTeamAsync()
    {
        GlobalStatus = "正在同步团队共享…";
        await Task.Run(() => _team.SyncNowAsync("手动")).ConfigureAwait(true);
        RefreshTeamUi();
        GlobalStatus = "团队共享已同步";
    }

    /// <summary>把团队共享条目添加为「我的工作空间」中的团队引用空间（TeamLink）。</summary>
    private async void AddSharedToWorkspace(SharedSpaceItem? item)
    {
        if (item is null) return;
        var entry = item.Entry;
        if (_configuration.Spaces.Any(s => s.IsTeamLink && s.OwnerMachineCode == entry.OwnerMachineCode && s.RemoteSpaceId == entry.SpaceId))
        {
            _dialogs.Info("这个共享目录已经在我的工作空间里了。");
            return;
        }
        if (!entry.Online)
        {
            _dialogs.Info($"来源（{entry.OwnerNickname}）当前离线，无法建立引用。稍后再试。");
            return;
        }
        var space = _team.CreateTeamLinkSpace(entry);
        _configuration.Spaces.Add(space);
        SaveConfiguration();
        RebuildTree();
        GlobalStatus = $"正在镜像团队共享目录「{entry.SpaceName}」…";
        try
        {
            var (synced, online) = await _team.SyncTeamLinkAsync(space);
            GlobalStatus = online ? $"已添加团队引用空间，同步 {synced} 个文件" : "来源离线，引用已建立（内容待来源上线后同步）";
            OpenSpace(space);
        }
        catch (Exception ex)
        {
            GlobalStatus = $"镜像同步失败：{ex.Message}";
        }
    }

    /// <summary>从侧栏团队共享树打开空间/WebDAV 时置位：标签切换不把左侧栏拽去工作空间/本地分组，
    /// 保持用户停留在团队共享面板（右侧照常切到对应空间浏览）。</summary>
    private bool _suppressSidebarSync;

    /// <summary>侧栏团队树点击共享空间行：
    /// 我创建的团队 → 直接打开对应本地空间（与工作空间浏览一致）；
    /// 加入的团队 → 打开对端 WebDAV UNC 目录（\\ip@端口\空间名，凭据已由 cmdkey 自动写入，走 HTTP WebDAV），右侧即文件夹浏览视图。</summary>
    private void OpenTeamShareNode(SharedSpaceItem? item)
    {
        if (item is null) return;
        SetShareSelection(item);
        ExitSearchIfActive(); // 点左边栏其他位置 → 自动取消检索结果
        _suppressSidebarSync = true;
        try
        {
            if (item.FromMyTeam)
            {
                var space = _configuration.Spaces.FirstOrDefault(s => s.Id == item.Entry.SpaceId);
                if (space is null || string.IsNullOrWhiteSpace(space.Path) || !Directory.Exists(space.Path))
                {
                    GlobalStatus = "空间不存在或目录不可用";
                    return;
                }
                OpenSpace(space);
            }
            else
            {
                var peer = _team.GetPeer(item.Entry.OwnerMachineCode);
                if (peer is not { Online: true })
                {
                    GlobalStatus = $"对方（{item.Entry.OwnerNickname}）不在线，暂无法打开 WebDAV 目录";
                    return;
                }
                OpenLocal($"\\\\{peer.Ip}@{peer.Port}\\{item.Entry.SpaceName}");
            }
        }
        finally
        {
            _suppressSidebarSync = false;
        }
    }

    /// <summary>侧栏共享行单选：点击行高亮，其余行取消。</summary>
    private void SetShareSelection(SharedSpaceItem selected)
    {
        foreach (var team in MyTeamTree)
            foreach (var member in team.Members)
                foreach (var share in member.Shares)
                    share.IsSelected = share == selected;
        foreach (var team in JoinedTeamTree)
            foreach (var member in team.Members)
                foreach (var share in member.Shares)
                    share.IsSelected = share == selected;
    }

    /// <summary>双击团队共享条目：直接取回并打开（不建立工作空间引用）。</summary>
    private void OpenSharedItem(SharedSpaceItem? item)
    {
        if (item is null) return;
        GlobalStatus = $"正在取回「{item.Entry.SpaceName}」…";
        _ = Task.Run(async () =>
        {
            // 无引用浏览 = 取回空间根下的首个可见文件供快速打开；完整浏览请添加为工作空间
            var space = _team.CreateTeamLinkSpace(item.Entry);
            space.Id = Guid.NewGuid();
            var (synced, online) = await _team.SyncTeamLinkAsync(space);
            GlobalStatus = online ? $"已取回 {synced} 个文件；双击文件即可打开" : "来源离线，暂无法取回";
        });
    }

    /// <summary>检索预览分发：团队命中 → 取回到内容缓存再预览；本地 → 原逻辑。</summary>
    private (System.Windows.Media.Imaging.BitmapImage? Image, string Text) LoadPreviewFor(string path)
    {
        var teamRef = TeamHitRef.Decode(path);
        if (teamRef is null) return PreviewHelper.Load(path, _files, Services.PluginBridge.TryPluginPreview);
        var cached = ResolveTeamHitFile(teamRef, maximumBytes: 8 * 1024 * 1024);
        if (cached is null) return (null, "来源当前离线，无法预览。等 owner 上线后重试。");
        return PreviewHelper.Load(cached, _files, Services.PluginBridge.TryPluginPreview);
    }

    /// <summary>把团队命中文件取回内容缓存（LRU），返回本地缓存路径。</summary>
    private string? ResolveTeamHitFile(TeamHitRef teamRef, long maximumBytes)
    {
        if (teamRef.Size > maximumBytes) return null;
        var key = _team.Cache.Key(teamRef.OwnerMachineCode, teamRef.SpaceId, teamRef.Path, teamRef.Size, teamRef.Mtime);
        var cached = _team.Cache.TryGet(key);
        if (cached is not null) return cached;
        var peer = _team.GetPeer(teamRef.OwnerMachineCode);
        if (peer is not { Online: true }) return null;
        var bytes = new TeamClient(_team.Identity).GetFileBytesAsync(peer, teamRef.SpaceId, teamRef.Path).GetAwaiter().GetResult();
        if (bytes is null) return null;
        return _team.Cache.Put(key, bytes);
    }

    /// <summary>合并团队共享元数据命中（追加在本地命中之后）。</summary>
    private void MergeTeamHits(string query)
    {
        try
        {
            var teamHits = _team.SearchShared(query);
            foreach (var (entry, source) in teamHits.Take(50))
            {
                var hit = new KnowledgeSearchService.SearchHit
                {
                    Hash = $"team:{entry.Hash}",
                    RelativePath = entry.Path,
                    FullPath = new TeamHitRef
                    {
                        OwnerMachineCode = source.OwnerMachineCode,
                        OwnerNickname = source.OwnerNickname,
                        SpaceId = source.SpaceId,
                        SpaceName = source.SpaceName,
                        TeamId = source.TeamId,
                        Path = entry.Path,
                        Size = entry.Size,
                        Mtime = entry.Mtime,
                        Level = source.Level
                    }.Encode(),
                    Format = string.IsNullOrWhiteSpace(Path.GetExtension(entry.Path)) ? "文件夹" : Path.GetExtension(entry.Path).TrimStart('.').ToUpperInvariant(),
                    Size = entry.Size,
                    Mtime = entry.Mtime > 0 ? DateTimeOffset.FromUnixTimeSeconds(entry.Mtime) : DateTimeOffset.MinValue,
                    Tags = [$"type/来自 {source.OwnerNickname}·{source.SpaceName}", "team/共享"],
                    Score = 0.1
                };
                var item = new SearchHitItem(hit);
                item.BuildTags();
                KnowledgeSearch.Hits.Add(item);
            }
            if (teamHits.Count > 0)
                GlobalStatus = $"本地命中之外，团队共享另有 {teamHits.Count} 项（点击取回预览）。";
        }
        catch (Exception ex)
        {
            GlobalStatus = $"团队检索合并失败：{ex.Message}";
        }
    }

    // === 标签来源切换 ===

    /// <summary>把当前标签切换到指定远程位置。</summary>
    private async Task SwitchTabSourceToRemote(RemoteLocationDefinition? location)
    {
        if (location is null || SelectedTab is null || IsBusy) return;
        IsBusy = true;
        ExitSearchIfActive(); // 点左边栏其他位置 → 自动取消检索结果
        GlobalStatus = $"正在准备远程位置 {location.Name}…";
        try
        {
            var path = await Task.Run(() => PrepareRemoteLocation(location));
            SaveConfiguration();
            SelectedTab.SwitchRemote(location, path);
            GlobalStatus = location.Kind == RemoteLocationKind.Rsync
                ? $"已切换到 {location.Name} 的本地缓存；尚未执行实时 rsync 浏览。"
                : $"已切换到远程位置 {location.Name}：{path}";
            SyncSidebarToTab();
        }
        catch (Exception ex)
        {
            GlobalStatus = "远程位置打开失败";
            _dialogs.Info($"远程位置打开失败：{ex.Message}");
        }
        finally { IsBusy = false; }
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
        // 如果当前选中了目录节点，预选归属目录
        var preselectDirId = SelectedNode?.Kind == WorkspaceNodeKind.Directory && SelectedNode.Directory is { } d ? d.Id : (Guid?)null;
        var result = _dialogs.ShowNewSpace(preselectDirectoryId: preselectDirId);
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
            // 元数据初始化/索引全部后台执行：大目录不再卡 UI（UI 立即建空间并打开），失败自动重试 3 次
            _ = Task.Run(async () =>
            {
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        var metadata = new MetadataStore(space.Path);
                        metadata.Initialize();
                        metadata.IndexFiles();
                        _worker.EnqueueJob(space.Path, space.Name, MetadataJobKind.Basic, "space-created");
                        _worker.EnqueueJob(space.Path, space.Name, MetadataJobKind.Type, "space-created");
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                            GlobalStatus = $"空间“{space.Name}”元数据已就绪。");
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 3)
                        {
                            Application.Current?.Dispatcher.BeginInvoke(() =>
                                GlobalStatus = $"空间“{space.Name}”元数据初始化失败：{ex.Message}（可在知识WIKI面板手动重跑）");
                            return;
                        }
                        await Task.Delay(TimeSpan.FromSeconds(30));
                    }
                }
            });
            GlobalStatus = $"已创建空间“{space.Name}”，元数据正在后台生成…";
        }
        _configuration.Spaces.Add(space);
        SaveConfiguration();
        RebuildTree();
        SelectedNode = FindSpaceNode(space.Id);
        GlobalStatus = $"已创建空间“{space.Name}”，元数据正在后台生成。";
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
        var result = _dialogs.ShowEditSpace(space.Name, space.Path, space.DirectoryId);
        if (result is null) return;
        if (result.DeleteRequested) { DeleteSpace(space); return; }

        // 重新绑定位置：确认 → 清旧元数据 → 新位置建元数据 → 改配置 → 标签跟随
        var newPath = result.NewPath?.Trim();
        if (!string.IsNullOrWhiteSpace(newPath) && !newPath.Equals(space.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(newPath)) { _dialogs.Info("新的位置不存在或无法访问。"); return; }
            if (!_dialogs.Confirm($"把空间“{space.Name}”的绑定位置改为：\n\n{newPath}\n\n原位置（{space.Path}）的元数据将被删除，新位置会重新建立元数据。\n两处的资料文件都不会被改动。是否继续？")) return;
            try
            {
                if (Directory.Exists(space.Path)) MetadataStore.Cleanup(space.Path);
                new MetadataStore(newPath).Initialize();
            }
            catch (Exception ex)
            {
                _dialogs.Info($"元数据迁移失败，空间位置未改变：{ex.Message}");
                return;
            }
            var oldPath = space.Path;
            space.Path = newPath;
            space.LastSyncAt = null;
            // 打开着这个空间的标签跟随到新位置
            foreach (var tab in Tabs.Where(x => x.WorkspaceSpace?.Id == space.Id).ToArray()) tab.SwitchSpace(space);
            GlobalStatus = $"空间“{space.Name}”已从 {oldPath} 重新绑定到 {newPath}。";
        }

        var newName = result.Name.Trim();
        if (!newName.Equals(space.Name, StringComparison.Ordinal))
        {
            space.Name = newName;
            GlobalStatus = $"空间已更名为“{space.Name}”。";
        }
        SaveConfiguration();
        RebuildTree();
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
        foreach (var tab in Tabs.Where(x => x.WorkspaceSpace?.Id == space.Id).ToArray()) CloseTab(tab);
        _configuration.Spaces.Remove(space);
        SelectedNode = null;
        IsCatalogView = false;
        SaveConfiguration();
        RebuildTree();
        GlobalStatus = $"已删除空间“{space.Name}”及其元数据，资料文件未改动。";
    }

    private void AssignCategory()
    {
        if (SelectedTab is not { WorkspaceSpace: { } space } tab || tab.SelectedFile is not { } file) return;
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
            var metadata = new MetadataStore(space.Path);
            metadata.AddLink(space.Id, directory.Id, file.RelativePath);
            metadata.ExportCatalogSnapshot(space, _configuration.Directories);
            GlobalStatus = $"已将“{file.Name}”编入“{directory.Name}”。";
        }
        catch (Exception ex) { _dialogs.Info($"编目失败：{ex.Message}"); }
    }

    private void ShowCatalog(DirectoryDefinition directory)
    {
        CatalogResults.Clear();
        ExitSearchIfActive(); // 点左边栏其他位置 → 自动取消检索结果
        foreach (var space in _configuration.Spaces.Where(x => x.DirectoryId == directory.Id && Directory.Exists(x.Path)))
        {
            try
            {
                var metadata = new MetadataStore(space.Path);
                // 列出该空间已索引的全部文件资料（file_index 有真实数据；catalog_links 从未写入，
                // 且其中的 DirectoryId 是编目时的归属目录，与选中节点无关）
                foreach (var file in metadata.GetAllFiles())
                    CatalogResults.Add(new CatalogResult(space.Id, space.Name, space.Path, file.RelativePath, file.FullPath, file.DisplaySize, file.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { GlobalStatus = $"读取编目失败：{ex.Message}"; }
            // SQLite 层的失败（库损坏、被占用、.7z 不可写）同样不能冒泡杀进程
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException)
            {
                GlobalStatus = $"读取编目失败：{ex.Message}";
                Services.DiagLog.Exception("ShowCatalog", ex);
            }
        }
        CatalogTitle = $"{directory.Name} · {CatalogResults.Count} 项";
        // Directory 节点点击 → 永远显示编目视图（统计 + 子空间列表），不再自动跳到子空间
        // 用户如果想进某个子空间，直接点 TreeView 里的 Space 节点即可
        if (SelectedTab is not null) SelectedTab.CatalogDirectoryName = directory.Name;
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

    /// <summary>在当前空间执行语义检索：规则解析分面 → 过滤 → 稀疏排序 → 版本簇展开。</summary>
    private async Task RunKnowledgeSearchAsync()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        var query = KnowledgeSearch.Query;
        if (string.IsNullOrWhiteSpace(query)) return;

        // 本地标签页没有空间元数据，退回文件名检索并明确提示
        var space = tab.WorkspaceSpace;
        if (space is null)
        {
            tab.SearchText = query;
            tab.SearchCommand.Execute(null);
            GlobalStatus = "本地目录仅支持文件名检索；语义检索需在空间标签页使用。";
            return;
        }

        try
        {
            IsBusy = true;
            var metadataPath = Path.Combine(space.Path, ".7z");
            var catalogDirectory = Path.Combine(metadataPath, "catalog");

            // 尚未整理过索引时给出明确指引，而不是返回空结果
            if (!Directory.Exists(catalogDirectory) || !Directory.EnumerateFiles(catalogDirectory, "*.json").Any())
            {
                KnowledgeSearch.Reset();
                _dialogs.Info("该空间尚未生成知识索引。\n\n请先点击工具栏「◈ 整理知识索引」，扫描文件生成标签与描述后再检索。");
                return;
            }

            var service = new KnowledgeSearchService(metadataPath);
            var outcome = await Task.Run(() => service.Search(query));
            KnowledgeSearch.Apply(outcome);
            MergeTeamHits(query);
            KnowledgeSearch.IsSearching = true;
            IsCatalogView = false;
            OnPropertyChanged(nameof(IsSearchView));
            OnPropertyChanged(nameof(IsBrowserView));
            OnPropertyChanged(nameof(IsEmptyView));
            GlobalStatus = outcome.Hits.Count == 0
                ? "未找到匹配文件。"
                : $"语义检索命中 {outcome.Hits.Count} 项（候选 {outcome.TotalCandidates} 项）。";
        }
        catch (Exception ex)
        {
            _dialogs.Info($"检索失败：{ex.Message}");
        }
        finally { IsBusy = false; }
    }

    /// <summary>打开知识索引构建面板：只显示当前打开标签对应的工作空间。</summary>
    private void ShowMetadataConfig()
    {
        // 「针对当前打开的工作空间」：本地目录/我的电脑标签没有对应知识库，回退到全部列表
        var space = SelectedTab?.WorkspaceSpace;
        ShowMetadataPanel(space);
    }

    /// <summary>打开冲突/变更版本比对窗口（HEAD 基线 vs 工作区）。</summary>
    private void CompareConflict()
    {
        var tab = SelectedTab;
        var file = tab?.SelectedFile;
        if (tab is null || file is null || file.IsDirectory) return;
        var root = string.IsNullOrWhiteSpace(tab.GitRepositoryRoot)
            ? Git?.DiscoverRepositoryRoot(tab.CurrentPath)
            : tab.GitRepositoryRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            _dialogs.Info("当前目录不在 Git 仓库中。");
            return;
        }
        var git = Git;
        if (git is null)
        {
            _dialogs.Info("Git 插件未加载，无法进行版本比对。");
            return;
        }
        var window = new Views.ConflictDiffWindow(git, root, file.RelativePath)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    /// <summary>打开知识库元数据面板，并聚焦到指定知识库。</summary>
    private void ShowSpaceMetadata(WorkspaceNodeViewModel? node)
    {
        if (node?.Space is null) return;
        ShowMetadataPanel(node.Space);
    }

    /// <summary>显示元数据面板；focusSpace 为空时显示全部知识库。</summary>
    private void ShowMetadataPanel(SpaceDefinition? focusSpace)
    {
        var spaces = _configuration.Spaces
            .Where(s => !string.IsNullOrWhiteSpace(s.Path))
            .Where(s => focusSpace is null || s.Id == focusSpace.Id)
            .ToList();
        if (spaces.Count == 0)
        {
            _dialogs.Info("该知识库当前不可用，无法查看元数据。");
            return;
        }
        var window = new Views.MetadataConfigWindow(_worker, spaces)
        {
            Owner = Application.Current.MainWindow
        };
        window.StatusChanged += (_, message) => GlobalStatus = message;
        window.ShowDialog();
    }

    /// <summary>为指定空间投递基础层 + 类型层元数据任务。</summary>
    private void RunSpaceMetadata(WorkspaceNodeViewModel? node)
    {
        if (node?.Space is not { } space) return;
        foreach (var kind in new[] { MetadataJobKind.Basic, MetadataJobKind.Type })
        {
            _worker.EnqueueJob(space.Path, space.Name, kind, "manual");
        }
        GlobalStatus = $"已为“{space.Name}”投递元数据任务，后台处理中。";
    }

    /// <summary>用户跳转到其他位置（侧栏/空间/本地/远程）时自动取消检索结果，回到文件夹浏览。</summary>
    private void ExitSearchIfActive()
    {
        if (!KnowledgeSearch.IsSearching) return;
        KnowledgeSearch.Reset(); // IsSearching 变更会经构造器转发器刷新视图属性
    }

    /// <summary>检索命中 → 图标绝对路径（空间根 + 相对路径；团队命中给空走通用图标）。</summary>
    private (string Path, bool IsDirectory) ResolveHitIcon(KnowledgeSearchService.SearchHit hit)
    {
        if (TeamHitRef.Decode(hit.FullPath) is not null) return (string.Empty, false);
        var space = SelectedTab?.WorkspaceSpace;
        if (space is null || string.IsNullOrWhiteSpace(space.Path)) return (string.Empty, false);
        return (Path.Combine(space.Path, hit.RelativePath), string.IsNullOrEmpty(Path.GetExtension(hit.RelativePath)));
    }

    /// <summary>打开检索命中文件的所在文件夹：新标签定位到目标目录（团队共享命中不支持）。</summary>
    private void OpenSearchHitFolder(KnowledgeSearchService.SearchHit hit)
    {
        if (TeamHitRef.Decode(hit.FullPath) is not null)
        {
            GlobalStatus = "团队共享文件暂不支持定位所在文件夹。";
            return;
        }
        var space = SelectedTab?.WorkspaceSpace;
        if (space is null) return;
        var fullPath = Path.Combine(space.Path, hit.RelativePath);
        var target = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : space.Path;
        if (string.IsNullOrEmpty(target) || !Directory.Exists(target)) return;
        OpenSpace(space, forceNew: true);
        SelectedTab?.SelectCatalogFile(Directory.Exists(fullPath) ? fullPath : Path.Combine(target, Path.GetFileName(fullPath)));
        ExitSearchIfActive();
        GlobalStatus = $"已定位到 {target}";
    }

    /// <summary>打开检索命中的文件。</summary>
    private void OpenSearchHit(KnowledgeSearchService.SearchHit hit)
    {
        // 团队共享命中：取回到内容缓存后用系统默认程序打开
        var teamRef = TeamHitRef.Decode(hit.FullPath);
        if (teamRef is not null)
        {
            GlobalStatus = $"正在取回 {teamRef.Path}…";
            _ = Task.Run(() =>
            {
                var cached = ResolveTeamHitFile(teamRef, maximumBytes: 64 * 1024 * 1024);
                if (cached is null) { GlobalStatus = "取回失败：来源离线或权限不足。"; return; }
                GlobalStatus = $"已取回 {Path.GetFileName(teamRef.Path)}，正在打开…";
                Process.Start(new ProcessStartInfo(cached) { UseShellExecute = true });
                GlobalStatus = $"已打开团队共享文件 {Path.GetFileName(teamRef.Path)}";
            });
            return;
        }
        var tab = SelectedTab;
        var space = tab?.WorkspaceSpace;
        if (tab is null || space is null) return;
        var fullPath = Path.Combine(space.Path, hit.RelativePath);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) return;
        tab.SelectCatalogFile(fullPath);
        KnowledgeSearch.IsSearching = false;
        OnPropertyChanged(nameof(IsSearchView));
        OnPropertyChanged(nameof(IsBrowserView));
        OnPropertyChanged(nameof(IsEmptyView));
    }

    private void OpenSpace(SpaceDefinition space, bool forceNew = false)
    {
        if (!Directory.Exists(space.Path)) { _dialogs.Info("该位置当前离线。重新连接磁盘或网络后即可使用。"); return; }
        IsCatalogView = false;
        ExitSearchIfActive(); // 点左边栏其他位置 → 自动取消检索结果
        // 不清除 SelectedNode，保持树节点选中态

        // 标签为主：优先复用当前标签（让它切换空间），只有 forceNew 或没有标签时才新建
        if (!forceNew && SelectedTab is not null)
        {
            SelectedTab.SwitchSpace(space);
            if (_configuration.AutoDetect) _monitor.Start(space.Path);
            return;
        }

        var tab = Tabs.FirstOrDefault(x => x.WorkspaceSpace?.Id == space.Id);
        if (tab is null)
        {
            tab = new MountTabViewModel(space, _files);
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
        // 标签来源变化（空间↔本地↔远程）→ 左侧栏跟着切到对应类别。
        // 同一标签的来源切换不主动清三类选中（点击场景下视觉已正确），只设目标项。
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MountTabViewModel.SourceKind) && SelectedTab == tab) SyncSidebarToTab(clearOthers: false);
        };
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
        // WPF 陷阱：如果被移除的正好是 TabControl 当前选中项，Selector 会在集合通知回调里
        // 回查已失效的容器索引（GetSelectedTabItem → ContainerAt）并抛 IndexOutOfRangeException。
        // 必须在改动集合之前先把选中态迁走。
        if (ReferenceEquals(SelectedTab, tab))
        {
            var next = Tabs.FirstOrDefault(x => !ReferenceEquals(x, tab));
            SelectedTab = next;
        }

        Tabs.Remove(tab);
        TabItems.Remove(tab);
        // 标签已关闭：不再回填后台扫描结果（避免对已释放的 UI 集合写入）
        tab.Dispose();
    }

    private void NewTab()
    {
        // 新标签默认跟随「第一个工作空间」；没有工作空间时退到我的电脑
        var first = _configuration.Spaces.FirstOrDefault(x => Directory.Exists(x.Path));
        MountTabViewModel tab;
        if (first is not null)
        {
            tab = new MountTabViewModel(first, _files);
        }
        else
        {
            tab = new MountTabViewModel(_files);
        }
        AttachTab(tab);
        AddTab(tab);
        SelectedTab = tab;
        GlobalStatus = first is not null ? $"已打开工作空间“{first.Name}”。" : "已打开我的电脑。";
    }

    private void OpenLocalFolder()
    {
        var path = _dialogs.SelectFolder("选择要浏览的本地文件夹");
        if (!string.IsNullOrWhiteSpace(path)) OpenLocal(path, forceNew: true);
    }

    public void OpenLocal(string path, bool forceNew = false)
    {
        if (!Directory.Exists(path)) return;
        IsCatalogView = false; // 切回浏览视图
        ExitSearchIfActive(); // 点左边栏其他位置 → 自动取消检索结果

        // 标签为主：优先复用当前标签（让它切换本地路径），只有 forceNew 或没有标签时才新建
        if (!forceNew && SelectedTab is not null)
        {
            SelectedTab.SwitchLocal(path);
            TrackRecent(path);
            return;
        }

        var tab = Tabs.FirstOrDefault(x => x.IsLocal && path.StartsWith(x.RootPath, StringComparison.OrdinalIgnoreCase));
        if (tab is null)
        {
            tab = new MountTabViewModel(path, _files);
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
        if (_configuration.RemoteLocations.Count != before) SaveConfiguration();
    }

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
        // 当前有标签 → 把它切到远程来源；没有标签 → 走标签来源切换命令（它会新建/复用标签）
        if (SelectedTab is not null)
        {
            await SwitchTabSourceToRemote(location);
            return;
        }
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
        return GitOperationsProvider.Instance?.CloneRepository(location.Address, location.LocalPath)
            ?? throw new InvalidOperationException("Git 插件未加载，无法克隆远程仓库。");
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
        var git = Git;
        if (git is null)
        {
            _dialogs.Info("Git 插件未加载，无法关联 Git 仓库。");
            return;
        }
        if (SelectedTab is not { } tab) return;
        var result = _dialogs.ShowGitRepository(tab.CurrentPath);
        if (result is null) return;
        var repositoryPath = result.RepositoryPath;
        var remote = result.RemoteUrl.Trim();
        await RunOperationAsync(tab, "Git 仓库关联", repositoryPath, progress => git.BindRepository(repositoryPath, remote, progress).Display, "初始化本地仓库并关联远程 origin");
        // 只在标签绑定的是工作空间时写回配置（本地标签的 Space 是临时对象）
        if (tab.WorkspaceSpace is { } bound) bound.RemoteUrl = remote;
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

    private Task PullAsync()
    {
        var git = Git;
        if (git is null)
        {
            _dialogs.Info("Git 插件未加载，无法拉取更新。");
            return Task.CompletedTask;
        }
        if (SelectedTab is not { } tab || string.IsNullOrEmpty(tab.GitRepositoryRoot)) return Task.CompletedTask;
        return RunOperationAsync(tab, "拉取更新", tab.GitRepositoryRoot, progress => git.Pull(tab.GitRepositoryRoot, _configuration.AuthorName, _configuration.AuthorEmail, progress), "从远程仓库拉取最新版本");
    }

    private async Task SavePushAsync()
    {
        var git = Git;
        if (git is null)
        {
            _dialogs.Info("Git 插件未加载，无法保存并上传。");
            return;
        }
        if (SelectedTab is not { } tab || string.IsNullOrEmpty(tab.GitRepositoryRoot)) return;
        var message = _dialogs.Prompt("保存并上传", "提交说明", $"更新资料 {DateTime.Now:MM-dd HH:mm}");
        if (message is null) return;
        await RunOperationAsync(tab, "保存并上传", tab.GitRepositoryRoot, progress => git.SaveAndPush(tab.GitRepositoryRoot, message, _configuration.AuthorName, _configuration.AuthorEmail, progress), "提交本地变更并上传到远程仓库");
    }

    private async Task RunOperationAsync(MountTabViewModel tab, string operation, string repoRoot, Func<IProgress<string>, string> action, string subtitle)
    {
        IsBusy = true;
        using var progress = _dialogs.BeginGitOperation($"{tab.Title} · {operation}", subtitle);
        progress.Report($"▶ {operation}：{repoRoot}");
        GlobalStatus = $"{tab.Title}：正在{operation}…";
        try
        {
            var message = await Task.Run(() => action(progress));
            progress.Done(true, message);
            var space = tab.WorkspaceSpace;
            if (space is not null)
            {
                space.LastSyncAt = DateTimeOffset.Now;
                new MetadataStore(space.Path).RecordSync(operation, "成功", message);
            }
            SaveConfiguration();
            tab.Refresh();
            GlobalStatus = $"{tab.Title}：{message}";
        }
        catch (Exception ex)
        {
            var detail = ex is GitCommandException gce ? gce.Message : $"{operation}失败：{ex.Message}";
            progress.Report($"✖ {detail}");
            progress.Done(false, detail);
            try { if (tab.WorkspaceSpace is { } s) new MetadataStore(s.Path).RecordSync(operation, "失败", ex.Message); } catch { }
            GlobalStatus = $"{tab.Title}：{operation}失败";
        }
        finally { IsBusy = false; }
    }

    private void ShowHistory()
    {
        if (SelectedTab is { } tab && !string.IsNullOrEmpty(tab.GitRepositoryRoot))
            _dialogs.ShowHistory($"{tab.Title} · Git 状态及历史", tab.GitRepositoryRoot);
    }

    private async Task ArchiveAsync()
    {
        var archive = Archive;
        if (archive is null)
        {
            _dialogs.Info("rsync 归档插件未加载，请在设置 → 插件管理中启用「rsync 归档插件」。");
            return;
        }
        if (SelectedTab is not { WorkspaceSpace: { } space } tab) return;
        var note = _dialogs.Prompt("标记关键节点", "节点备注", $"关键节点 {DateTime.Now:yyyy-MM-dd HH:mm}");
        if (note is null) return;
        IsBusy = true;
        GlobalStatus = $"{tab.Title}：正在执行 rsync 关键节点归档…";
        try
        {
            var progress = new Progress<string>(message => GlobalStatus = $"{tab.Title}：{message}");
            var record = await Task.Run(() => archive.CreateAsync(space.Name, space.Path, note, progress));
            GlobalStatus = $"{tab.Title}：关键节点 {record.NodeName} 已归档。";
        }
        catch (Exception ex) { GlobalStatus = "归档失败"; _dialogs.Info($"归档失败：{ex.Message}"); }
        finally { IsBusy = false; }
    }

    private void ShowArchiveHistory()
    {
        var archive = Archive;
        if (archive is null)
        {
            _dialogs.Info("rsync 归档插件未加载，请在设置 → 插件管理中启用「rsync 归档插件」。");
            return;
        }
        if (SelectedTab is { WorkspaceSpace: { } space } tab)
            _dialogs.ShowArchives(archive.GetHistory(space.Path), $"{tab.Title} · 关键节点");
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

    private void ToggleFavorite()
    {
        if (SelectedTab is not { } tab) return;
        var path = tab.CurrentPath;
        if (_configuration.FavoriteFolders.Contains(path))
        {
            _configuration.FavoriteFolders.Remove(path);
            GlobalStatus = "已取消收藏当前目录。";
        }
        else
        {
            _configuration.FavoriteFolders.Add(path);
            GlobalStatus = "已收藏当前目录。";
        }
        SaveConfiguration();
        UpdateFavorites();
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
        OnPropertyChanged(nameof(IsCurrentPathFavorited));
        OnPropertyChanged(nameof(FavoriteButtonText));
    }

    private void CloseTab(MountTabViewModel? tab)
    {
        if (tab is null) return;
        // 选中态的迁移在 RemoveTab 内部完成（必须在集合改动之前）。
        RemoveTab(tab);
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
            var tab = Tabs.FirstOrDefault(x => x.WorkspaceSpace?.Path.Equals(path, StringComparison.OrdinalIgnoreCase) == true);
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

    /// <summary>
    /// 使用 MetadataScanningService 扫描空间全部可识别文件，产出 FileMetadata JSON。
    /// 写入 .7z/catalog/{hash}.json 目录；不阻塞 UI，不抛异常给调用方。
    /// </summary>
    private void ScanWorkspaceMetadata(SpaceDefinition space)
    {
        if (space is null || string.IsNullOrWhiteSpace(space.Path)) return;
        try
        {
            var count = MetadataScanningService.Instance.ScanWorkspace(space.Path);
            Application.Current?.Dispatcher.BeginInvoke(() => GlobalStatus = $"“{space.Name}”已完成元数据扫描：{count} 个文件已登记。");
            // TODO: workspace.json 暂未写入；当需要工作空间级别的扫描摘要时，可在此写入 .7z/workspace.json。
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => GlobalStatus = $"元数据扫描失败：{ex.Message}");
        }
    }

    private void RefreshCommands()
    {
        DeleteNodeCommand.NotifyCanExecuteChanged();
        AssignCategoryCommand.NotifyCanExecuteChanged();
        BindGitCommand.NotifyCanExecuteChanged();
        PullCommand.NotifyCanExecuteChanged();
        SavePushCommand.NotifyCanExecuteChanged();
        HistoryCommand.NotifyCanExecuteChanged();
        CompareConflictCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
        ArchiveHistoryCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        AddFavoriteCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pluginHost?.Dispose();
        Services.PluginBridge.Instance = null;
        _monitor.Dispose();
        _team.Dispose();
        // 关闭所有标签：停止它们回填后台扫描结果
        foreach (var tab in Tabs) tab.Dispose();
    }

    /// <summary>插件清单增减后刷新状态栏摘要（PluginHostService 调用）。</summary>
    internal void NotifyPluginSummaryChanged() => OnPropertyChanged(nameof(PluginSummary));

    /// <summary>拖拽脱离空间：把 Directory 从当前父节点移除，放到 Workspaces 根层级。</summary>
    public void DetachWorkspaceNode(WorkspaceNodeViewModel node)
    {
        var (_, sourceList, sourceIndex) = ResolveSourceList(node);
        if (sourceList is null || sourceIndex < 0) return; // 已经找不到

        sourceList.RemoveAt(sourceIndex);
        Workspaces.Add(node);

        // 更新配置：清除 Directory 的 ParentId
        if (node.Directory is { } dir)
        {
            dir.ParentId = null;
        }
        SaveConfiguration();
        RebuildTree();
    }

    /// <summary>在工作空间树中拖拽重排序。支持 Before(前)/Into(嵌套)/After(后) 三种放置区域。</summary>
    internal void ReorderWorkspaceNode(WorkspaceNodeViewModel source, WorkspaceNodeViewModel target, DropTargetZone zone)
    {
        // 抽象：source 当前所在的集合，以及它在集合中的索引
        var (sourceParent, sourceList, sourceIndex) = ResolveSourceList(source);
        if (sourceList is null || sourceIndex < 0) return;

        // === Into 模式：嵌套到 target 里面 ===
        if (zone == DropTargetZone.Into)
        {
            if (target.IsDirectory)
            {
                // Directory 可以接受 Directory 或 Space 作为子节点
                if (target.Children.Contains(source)) return; // 已经在里面
                sourceList.RemoveAt(sourceIndex);
                target.Children.Add(source);
                if (source.Directory is { } movedDir && target.Directory is { } targetDir)
                    movedDir.ParentId = targetDir.Id;
                else if (source.Space is { } movedSpace && target.Directory is { } spaceTargetDir)
                    movedSpace.DirectoryId = spaceTargetDir.Id;
            }
            else if (target.IsSpace && source.IsDirectory)
            {
                // Space 可以接受 Directory 子节点（Directory 变成 Space 内部的目录）
                if (!target.Children.Contains(source))
                {
                    sourceList.RemoveAt(sourceIndex);
                    target.Children.Add(source);
                    if (source.Directory is { } movedDir)
                        movedDir.ParentId = null; // 挂到 Space 下的 Directory 没有父 Directory
                }
            }
            else return; // target 不接受这种类型的子节点

            SaveConfiguration();
            RebuildTree();
            return;
        }

        // === Before / After 模式：同级重排或跨父移动 ===
        var (targetParent, targetList, targetIndex) = ResolveSourceList(target);
        if (targetList is null || targetIndex < 0) return;

        // After 模式：插入到 target 后面
        var insertIndex = zone == DropTargetZone.After ? targetIndex + 1 : targetIndex;

        if (ReferenceEquals(sourceList, targetList))
        {
            // 同层级重排序
            var adjustedIndex = sourceIndex < insertIndex ? insertIndex - 1 : insertIndex;
            sourceList.Move(sourceIndex, adjustedIndex);
        }
        else
        {
            // 跨父节点移动：列表位置 + 挂载 ID 同步更新，否则 RebuildTree 后节点会弹回原父
            sourceList.RemoveAt(sourceIndex);
            targetList.Insert(insertIndex, source);

            // 新父 = target 所在列表的容器节点；target 在根级（Workspaces）时为 null → 提升到根
            var newParent = FindParentNode(Workspaces, target);

            if (source.Directory is { } movedDir)
                movedDir.ParentId = newParent?.Directory?.Id; // 挂到目标目录下；根级则清空 → 顶级节点
            else if (source.Space is { } movedSpace)
                movedSpace.DirectoryId = newParent?.Directory?.Id; // Space 同理：挂目录或独立到根
        }

        SaveConfiguration();
        RebuildTree();
    }

    /// <summary>
    /// 找到 node 实际所在的 ObservableCollection（可能是 Workspaces 根，也可能是某个 Directory/Space 的 Children），
    /// 同时返回它的直接父节点引用（如果在根则为 null）和在集合中的索引。
    /// </summary>
    private (WorkspaceNodeViewModel? parent, ObservableCollection<WorkspaceNodeViewModel>? list, int index) ResolveSourceList(WorkspaceNodeViewModel node)
    {
        // 先看它是不是顶级节点
        var rootIndex = Workspaces.IndexOf(node);
        if (rootIndex >= 0) return (null, Workspaces, rootIndex);

        // 再递归搜各节点的 Children
        var parent = FindParentNode(Workspaces, node);
        if (parent is not null)
        {
            var idx = parent.Children.IndexOf(node);
            if (idx >= 0) return (parent, parent.Children, idx);
        }

        return (null, null, -1);
    }

    private static WorkspaceNodeViewModel? FindParentNode(ObservableCollection<WorkspaceNodeViewModel> root, WorkspaceNodeViewModel child)
    {
        foreach (var node in root)
        {
            if (node.Children.Contains(child)) return node;
            var found = FindParentNode(node.Children, child);
            if (found is not null) return found;
        }
        return null;
    }
}
