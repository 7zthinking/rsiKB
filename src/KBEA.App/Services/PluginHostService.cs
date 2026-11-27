using System.Windows;
using System.Windows.Input;
using KBEA.Core.Plugins;
using KBEA.Core.Versioning;
using KBEA.App.ViewModels;

namespace KBEA.App.Services;

/// <summary>
/// P1 插件宿主：发现并加载 plugins 目录下的插件程序集，把 App 的侧栏/浏览状态
/// 以只读快照暴露给插件，承接工具栏按钮注册与全局状态写入。
/// 单个插件加载失败只写诊断日志，不阻断启动（与 PluginCatalog 容错策略一致）。
/// </summary>
public sealed class PluginHostService : IPluginHostContext, IDisposable
{
    /// <summary>宿主版本（来自程序集，全仓库唯一真相源：Directory.Build.props 的 &lt;Version&gt;）。</summary>
    public static string HostVersionValue => AppVersion.Version;

    private readonly MainViewModel _main;
    private readonly SidebarState _sidebar;
    private readonly BrowserState _browser;
    private readonly UiSurface _ui;
    private readonly List<IPluginEntryPoint> _instances = [];
    private readonly List<ISettingsContribution> _settingsContributors = [];
    private readonly List<IMetadataFieldContribution> _fieldContributors = [];
    private readonly List<IPreviewContribution> _previewContributors = [];
    private readonly List<IAboutContribution> _aboutContributors = [];
    private readonly Dictionary<string, string> _pluginStatuses = new(StringComparer.OrdinalIgnoreCase);

    public PluginHostService(MainViewModel main)
    {
        _main = main;
        _sidebar = new SidebarState(this);
        _browser = new BrowserState(this);
        _ui = new UiSurface(this);
        GitUi = new GitUiCommands(main);
        WikiUi = new WikiUiCommands(main);
        ArchiveUi = new ArchiveUiCommands(main);
    }

    public string HostVersion => HostVersionValue;
    public IMetadataWorkbench Metadata { get; } = new MetadataWorkbench();
    public ISidebarState Sidebar => _sidebar;
    public IBrowserState Browser => _browser;
    public IUiSurface Ui => _ui;
    public IGitUiCommands? GitUi { get; }
    public IWikiUiCommands? WikiUi { get; }
    public IArchiveUiCommands? ArchiveUi { get; }

    /// <summary>发现并加载插件；成功加载的清单去重后登记到 MainViewModel.Plugins，UI 贡献随即灌入。</summary>
    public void Load()
    {
        IReadOnlyList<PluginLoader.LoadedPlugin> loaded;
        try
        {
            var candidates = new PluginCatalog().DiscoverWithDirectories();
            loaded = new PluginLoader().Load(candidates, this);
        }
        catch (Exception ex)
        {
            DiagLog.Exception("PluginHost", ex);
            return;
        }

        foreach (var plugin in loaded)
        {
            var manifest = plugin.Manifest;
            if (plugin.SkipReason is not null)
            {
                DiagLog.Write($"插件 {manifest.Id} 跳过：{plugin.SkipReason}");
                _pluginStatuses[manifest.Id] = $"未加载：{plugin.SkipReason}";
                continue;
            }
            if (plugin.Error is not null)
            {
                DiagLog.Exception($"插件 {manifest.Id} 加载失败", plugin.Error);
                _pluginStatuses[manifest.Id] = $"加载失败：{plugin.Error.Message}";
                continue;
            }
            if (plugin.Instance is null) continue;

            _instances.Add(plugin.Instance);
            _pluginStatuses[manifest.Id] = "已加载";
            DiagLog.Write($"插件已加载：{manifest.Id} v{manifest.Version}");
            // manifest 展示去重：MainViewModel.Load 已登记 Discover 的清单
            if (!_main.Plugins.Any(p => p.Id == manifest.Id)) _main.Plugins.Add(manifest);
            OnPropertyChangedProxy();

            // 工具栏贡献：声明 toolbar 能力且实现了 IToolbarContribution 的插件
            // （插件只声明分组与图标/提示，排布与收起由宿主框架负责）
            if (manifest.HasCapability("toolbar") && plugin.Instance is IToolbarContribution toolbar)
                foreach (var group in toolbar.GetToolbarGroups()) _ui.AddToolbarGroup(group);

            // P4 扩展点：设置页 / 详情字段 / 预览（能力 + 接口双匹配，与 toolbar 同一模式）
            if (manifest.HasCapability("settings") && plugin.Instance is ISettingsContribution settings)
                _settingsContributors.Add(settings);
            if (manifest.HasCapability("fields") && plugin.Instance is IMetadataFieldContribution fields)
                _fieldContributors.Add(fields);
            if (manifest.HasCapability("preview") && plugin.Instance is IPreviewContribution preview)
                _previewContributors.Add(preview);
            // 「关于我们」卡片贡献（能力 + 接口双匹配）
            if (manifest.HasCapability("about") && plugin.Instance is IAboutContribution about)
                _aboutContributors.Add(about);
        }
    }

    // ==================== P4 扩展点宿主侧 ====================

    /// <summary>插件清单与加载状态（P5：设置窗口「插件管理」页；含未加载的插件）。</summary>
    public IReadOnlyList<PluginStatusVm> GetPluginStatuses()
    {
        var result = new List<PluginStatusVm>();
        foreach (var manifest in _main.Plugins)
        {
            _pluginStatuses.TryGetValue(manifest.Id, out var status);
            result.Add(new PluginStatusVm(manifest, status ?? (manifest.Enabled ? "未加载" : "已禁用")));
        }
        return result;
    }

    /// <summary>收集各插件的设置页（SettingsWindow 打开时调用，UI 线程；单个插件失败不影响其余）。</summary>
    public IReadOnlyList<SettingsPageVm> GetSettingsPages()
    {
        var pages = new List<SettingsPageVm>();
        foreach (var contributor in _settingsContributors)
        {
            try { pages.Add(contributor.GetSettingsPage()); }
            catch (Exception ex) { DiagLog.Exception($"插件 {contributor.GetType().Name} 构建设置页失败", ex); }
        }
        return pages;
    }

    /// <summary>宿主「保存设置」时回调各插件（UI 线程；单个插件失败不影响其余）。</summary>
    public void SavePluginSettings()
    {
        foreach (var contributor in _settingsContributors)
        {
            try { contributor.OnSettingsSaved(); }
            catch (Exception ex) { DiagLog.Exception($"插件 {contributor.GetType().Name} 保存设置失败", ex); }
        }
    }

    /// <summary>汇总各插件的详情字段（选中文件变化时调用，UI 线程）。</summary>
    public IReadOnlyList<(string Name, string Value)> GetPluginFields(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return [];
        var rows = new List<(string Name, string Value)>();
        foreach (var contributor in _fieldContributors)
        {
            try
            {
                foreach (var field in contributor.GetFields(filePath)) rows.Add(field);
            }
            catch (Exception ex) { DiagLog.Exception($"插件 {contributor.GetType().Name} 读取详情字段失败", ex); }
        }
        return rows;
    }

    /// <summary>收集各插件的「关于我们」卡片（SettingsWindow 打开时调用，UI 线程；单个插件失败不影响其余）。</summary>
    public IReadOnlyList<AboutCardInfo> GetAboutCards()
    {
        var cards = new List<AboutCardInfo>();
        foreach (var contributor in _aboutContributors)
        {
            try
            {
                var card = contributor.GetAboutCard();
                if (card is not null) cards.Add(card);
            }
            catch (Exception ex) { DiagLog.Exception($"插件 {contributor.GetType().Name} 构建关于卡片失败", ex); }
        }
        return cards;
    }

    /// <summary>预览链第一棒：按扩展名找第一个接手的插件（未命中返回 null，宿主走内置渲染）。</summary>
    public (byte[]? ImageData, string Text)? TryPluginPreview(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
        var extension = Path.GetExtension(filePath);
        foreach (var contributor in _previewContributors)
        {
            try
            {
                if (!contributor.Supports(extension)) continue;
                return contributor.Render(filePath);
            }
            catch (Exception ex) { DiagLog.Exception($"插件 {contributor.GetType().Name} 预览失败", ex); }
        }
        return null;
    }

    /// <summary>把宿主状态变化推给插件（MainViewModel 在 SelectedTab/SidebarSection/GlobalStatus 变化处调用）。</summary>
    public void Refresh()
    {
        _sidebar.RaiseChanged();
        _browser.RaiseChanged();
    }

    public SidebarSnapshot BuildSidebarSnapshot() => new(
        _main.SidebarSection.ToString(),
        _main.SelectedNode?.Name,
        _main.Configuration.Spaces.Count);

    public BrowserSnapshot BuildBrowserSnapshot()
    {
        var tab = _main.SelectedTab;
        return new BrowserSnapshot(
            tab?.CurrentPath,
            tab?.WorkspaceSpace?.Name,
            tab?.SourceKind.ToString(),
            tab?.SelectedFiles.Count ?? 0,
            tab?.GitStatus.Display);
    }

    public void Dispose()
    {
        foreach (var instance in _instances)
        {
            try { instance.Shutdown(); }
            catch (Exception ex) { DiagLog.Exception($"插件 {instance.DisplayName} Shutdown 失败", ex); }
        }
        _instances.Clear();
    }

    /// <summary>插件清单增减后刷新状态栏摘要。</summary>
    private void OnPropertyChangedProxy() => _main.NotifyPluginSummaryChanged();

    /// <summary>宿主 WIKI 命令面：转发 MainViewModel 的元数据面板命令（窗口/对话框逻辑留在宿主）。</summary>
    private sealed class WikiUiCommands(MainViewModel main) : IWikiUiCommands
    {
        public ICommand OpenMetadataConfigCommand => main.ShowMetadataConfigCommand;
    }

    /// <summary>宿主归档命令面：转发 MainViewModel 的关键节点与留档历史命令（rsync 实现由插件提供）。</summary>
    private sealed class ArchiveUiCommands(MainViewModel main) : IArchiveUiCommands
    {
        public ICommand ArchiveCommand => main.ArchiveCommand;
        public ICommand ArchiveHistoryCommand => main.ArchiveHistoryCommand;
    }

    /// <summary>宿主 Git 命令面：直接转发 MainViewModel 上的命令（对话框/配置写回逻辑留在宿主）。</summary>
    private sealed class GitUiCommands(MainViewModel main) : IGitUiCommands
    {
        public ICommand CompareConflictCommand => main.CompareConflictCommand;
        public ICommand PullCommand => main.PullCommand;
        public ICommand SavePushCommand => main.SavePushCommand;
        public ICommand HistoryCommand => main.HistoryCommand;
        public ICommand BindGitCommand => main.BindGitCommand;
    }

    private sealed class SidebarState : ISidebarState
    {
        private readonly PluginHostService _owner;

        public SidebarState(PluginHostService owner) { _owner = owner; }

        public SidebarSnapshot Current => _owner.BuildSidebarSnapshot();

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class BrowserState : IBrowserState
    {
        private readonly PluginHostService _owner;

        public BrowserState(PluginHostService owner) { _owner = owner; }

        public BrowserSnapshot Current => _owner.BuildBrowserSnapshot();

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>UI 注册面：集合与状态栏操作一律切回 UI 线程（插件可能在任意线程调用）。</summary>
    private sealed class UiSurface : IUiSurface
    {
        private readonly PluginHostService _owner;

        public UiSurface(PluginHostService owner) { _owner = owner; }

        public void AddToolbarGroup(ToolbarGroupVm group) => Dispatch(() =>
        {
            if (!_owner._main.PluginToolbarGroups.Contains(group))
                _owner._main.PluginToolbarGroups.Add(group);
        });

        public void RemoveToolbarGroup(ToolbarGroupVm group) =>
            Dispatch(() => _owner._main.PluginToolbarGroups.Remove(group));

        public void SetGlobalStatus(string message) =>
            Dispatch(() => _owner._main.GlobalStatus = message);

        private static void Dispatch(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) action();
            else dispatcher.BeginInvoke(action);
        }
    }
}
