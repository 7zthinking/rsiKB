using System.Text.Json;
using KBEA.Core.Knowledge;

namespace KBEA.Core.Plugins;

public interface IAssistantProvider { string DisplayName { get; } }
public interface IReportProvider { string DisplayName { get; } }
public interface IProjectMgmtProvider { string DisplayName { get; } }
public interface IIndexEngine { Task RebuildAsync(string mountPath, CancellationToken cancellationToken = default); }
public interface ICatalogRuleSource { string DisplayName { get; } }
public interface IInfoCardProvider { string DisplayName { get; } }

/// <summary>元数据层级：决定插件产出的元数据深度与执行顺序。</summary>
public enum MetadataLevel
{
    /// <summary>基础层：通用规则标签与事实提取，对所有文件生效。</summary>
    Basic = 0,

    /// <summary>类型层：按文件格式专用解析（如 PPT 大纲、PDF 目录、Word 标题）。</summary>
    Type = 1,

    /// <summary>专题层：按业务专题聚合的语义元数据（摘要、语义标签、关联），依赖模型。</summary>
    Semantic = 2
}

/// <summary>文件元数据扫描插件。针对特定扩展名集合，可产出扩展属性（如 word_count、page_count）。</summary>
public interface IMetadataPlugin
{
    string PluginId { get; }
    string PluginName { get; }
    string[] SupportedExtensions { get; }
    FileMetadata Scan(string filePath);
    string[] ExtendedProperties { get; } // e.g. ["word_count", "page_count"]

    /// <summary>该插件产出的元数据层级，默认基础层。</summary>
    MetadataLevel Level => MetadataLevel.Basic;

    /// <summary>该插件是否启用，可被用户按库关闭。</summary>
    bool Enabled => true;
}

/// <summary>类型层插件：除扫描外，还可产出按格式定制的结构化摘要（如幻灯片标题列表）。</summary>
public interface ITypeMetadataPlugin : IMetadataPlugin
{
    /// <summary>产出面向该类型的结构化摘要行，用于写入 describe 骨架。</summary>
    IReadOnlyList<string> DescribeHighlights(string filePath, FileMetadata metadata);
}

/// <summary>单个文件的元数据记录。扩展属性键值对由插件定义。</summary>
public record FileMetadata(
    string FilePath,
    string FileName,
    string Extension,
    long FileSize,
    DateTime CreatedTime,
    DateTime ModifiedTime,
    string[] Tags,
    IReadOnlyDictionary<string, string> ExtendedProperties);

public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string EntryAssembly { get; set; } = string.Empty;
    public string[] Capabilities { get; set; } = [];

    /// <summary>兼容的宿主最低版本（SemVer 前缀比较；空 = 不限制）。</summary>
    public string MinHostVersion { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 用户是否显式设置过启用状态。
    /// 为 true 时，用户的选择优先于发行方策略（plugins.defaults.json）——策略只决定「开箱默认」。
    /// </summary>
    public bool EnabledByUser { get; set; }

    /// <summary>
    /// 插件自带的默认启用值（用户改动前的原值）——供「恢复默认」用。
    /// 首次改动时写入 plugin.json 的 defaultEnabled 字段，之后一直保留。
    /// </summary>
    public bool DefaultEnabled { get; set; } = true;

    /// <summary>
    /// 本次运行中用户是否改动过启用状态（内存态，不落盘）。
    /// 设置页勾选后置位，保存设置时据此决定要不要写回 plugin.json。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UserModified { get; set; }

    /// <summary>启用状态的来源（默认 / 发行方策略 / 用户设置），用于设置页展示。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PluginEnableSource EnableSource { get; set; } = PluginEnableSource.Default;

    /// <summary>清单文件绝对路径（P5：启用开关写回 plugin.json 用；不参与序列化）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>是否声明了指定能力。</summary>
    public bool HasCapability(string capability) =>
        Capabilities.Any(c => c.Equals(capability, StringComparison.OrdinalIgnoreCase));

    /// <summary>展示名（禁用时标注）。</summary>
    public string DisplayName => Enabled ? Name : $"{Name}（已禁用）";
}

/// <summary>
/// 插件状态视图模型（P5：设置窗口「插件管理」页）。
/// Enabled=false 时表示用户已禁用——写回 plugin.json，重启后不再加载。
/// </summary>
public sealed class PluginStatusVm
{
    public PluginStatusVm(PluginManifest manifest, string status)
    {
        Manifest = manifest;
        Status = status;
    }

    public PluginManifest Manifest { get; }

    /// <summary>加载状态说明（已加载 / 未加载：<原因> / 加载失败：<原因> / 已禁用）。</summary>
    public string Status { get; }

    public string Id => Manifest.Id;

    public string Name => Manifest.Name;

    public string Version => Manifest.Version;

    /// <summary>能力列表（展示用，逗号分隔）。</summary>
    public string Capabilities => Manifest.Capabilities.Length == 0 ? "—" : string.Join(" · ", Manifest.Capabilities);

    public bool Enabled => Manifest.Enabled;

    /// <summary>用户是否显式设置过（true 时不随发行方策略变化）。</summary>
    public bool EnabledByUser => Manifest.EnabledByUser;

    /// <summary>启用来源（展示用文案）。</summary>
    public string EnableSourceText => Manifest.EnableSource switch
    {
        PluginEnableSource.PolicyEnabled => "发行方默认启用",
        PluginEnableSource.PolicyDisabled => "发行方默认禁用",
        PluginEnableSource.UserEnabled => "用户已启用",
        PluginEnableSource.UserDisabled => "用户已禁用",
        _ => "插件自带默认"
    };
}

public sealed class PluginCatalog
{
    /// <summary>发行方默认启用策略（程序目录下 plugins.defaults.json）。</summary>
    private static PluginEnablePolicy? _policy;

    /// <summary>当前生效的发行方策略（首次读取后走缓存）。</summary>
    public static PluginEnablePolicy Policy => _policy ??= PluginEnablePolicy.Load();

    /// <summary>丢弃策略缓存，下次访问重新读盘。</summary>
    public static void ReloadPolicy() => _policy = null;

    /// <summary>
    /// 测试用：临时把策略指向指定文件（null = 恢复从程序目录读取）。
    /// </summary>
    internal static void OverridePolicyForTests(string? policyPath)
    {
        _policy = policyPath is null ? null : PluginEnablePolicy.Load(policyPath);
    }

    public IReadOnlyList<PluginManifest> Discover(string? pluginRoot = null)
    {
        pluginRoot ??= Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(pluginRoot)) return [];
        var result = new List<PluginManifest>();
        foreach (var file in Directory.EnumerateFiles(pluginRoot, "plugin.json", SearchOption.AllDirectories))
        {
            try
            {
                var text = ReadManifestText(file);
                var manifest = JsonSerializer.Deserialize<PluginManifest>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id))
                {
                    manifest.SourcePath = file;
                    // 清单没写 defaultEnabled 时，把当前的 enabled 当作插件自带默认值
                    if (!text.Contains("\"defaultEnabled\"", StringComparison.OrdinalIgnoreCase))
                        manifest.DefaultEnabled = manifest.Enabled;
                    ApplyEnablePolicy(manifest);
                    result.Add(manifest);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken optional plugin must not prevent the open-source core from starting.
            }
        }
        return result;
    }

    /// <summary>同 Discover，但附带每个 manifest 所在目录（供 PluginLoader 定位入口程序集）。</summary>
    public IReadOnlyList<(PluginManifest Manifest, string Directory)> DiscoverWithDirectories(string? pluginRoot = null)
    {
        pluginRoot ??= Path.Combine(AppContext.BaseDirectory, "plugins");
        var result = new List<(PluginManifest, string)>();
        if (!Directory.Exists(pluginRoot)) return result;
        foreach (var file in Directory.EnumerateFiles(pluginRoot, "plugin.json", SearchOption.AllDirectories))
        {
            try
            {
                var text = ReadManifestText(file);
                var manifest = JsonSerializer.Deserialize<PluginManifest>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id))
                {
                    manifest.SourcePath = file;
                    if (!text.Contains("\"defaultEnabled\"", StringComparison.OrdinalIgnoreCase))
                        manifest.DefaultEnabled = manifest.Enabled;
                    ApplyEnablePolicy(manifest);
                    result.Add((manifest, Path.GetDirectoryName(file)!));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken optional plugin must not prevent the open-source core from starting.
            }
        }
        return result;
    }

    /// <summary>
    /// 应用发行方默认启用策略：用户显式设置过的插件保持用户选择；
    /// 未设置过的按策略（plugins.defaults.json）决定启用与否，并记录启用来源。
    /// </summary>
    private static void ApplyEnablePolicy(PluginManifest manifest)
    {
        if (manifest.EnabledByUser)
        {
            manifest.EnableSource = manifest.Enabled ? PluginEnableSource.UserEnabled : PluginEnableSource.UserDisabled;
            return;
        }
        var policyDefault = Policy.GetDefaultEnabled(manifest.Id);
        if (policyDefault is null)
        {
            manifest.EnableSource = PluginEnableSource.Default;
            return;
        }
        manifest.Enabled = policyDefault.Value;
        manifest.EnableSource = policyDefault.Value ? PluginEnableSource.PolicyEnabled : PluginEnableSource.PolicyDisabled;
    }

    /// <summary>
    /// 读取 plugin.json 文本：剥掉 UTF-8 BOM。
    /// 用户用记事本等工具编辑后的文件常带 BOM，JsonSerializer 不接受，会导致插件加载失败。
    /// </summary>
    private static string ReadManifestText(string file)
    {
        var text = File.ReadAllText(file);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>
    /// 写回 plugin.json 的 enabled 开关（P5：设置窗口的插件启用/禁用）。
    /// 只改 enabled 字段，其余字段与未知字段原样保留（按 JSON 文本逐行定位，避免整份重写丢字段）。
    /// 同时写入 enabledByUser=true，标记这是用户的显式选择（优先于发行方策略）；
    /// 首次改动时把插件自带的默认启用值固化到 defaultEnabled，供「恢复默认」用。
    /// </summary>
    public static void SetEnabled(PluginManifest manifest, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(manifest.SourcePath) || !File.Exists(manifest.SourcePath))
            throw new FileNotFoundException("插件清单文件不存在", manifest.SourcePath);

        var json = ReadManifestText(manifest.SourcePath);
        var updated = RewriteEnabled(json, enabled);
        updated = RewriteEnabledByUser(updated, true);
        updated = RewriteDefaultEnabled(updated, manifest.DefaultEnabled);
        File.WriteAllText(manifest.SourcePath, updated, new System.Text.UTF8Encoding(false));
        manifest.Enabled = enabled;
        manifest.EnabledByUser = true;
        manifest.UserModified = true;
        manifest.EnableSource = enabled ? PluginEnableSource.UserEnabled : PluginEnableSource.UserDisabled;
    }

    /// <summary>
    /// 清除用户的显式选择（enabledByUser=false），让插件回到发行方策略 / 插件自带的默认值。
    /// 写回的 enabled 是「最终生效值」（发行方策略优先于插件自带默认值）。
    /// </summary>
    public static void ResetToDefault(PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.SourcePath) || !File.Exists(manifest.SourcePath))
            throw new FileNotFoundException("插件清单文件不存在", manifest.SourcePath);

        // 最终生效值：发行方策略优先，其次插件自带的默认值
        var policyDefault = Policy.GetDefaultEnabled(manifest.Id);
        var effective = policyDefault ?? manifest.DefaultEnabled;

        var json = ReadManifestText(manifest.SourcePath);
        var updated = RewriteEnabledByUser(json, false);
        updated = RewriteEnabled(updated, effective);
        updated = RewriteDefaultEnabled(updated, manifest.DefaultEnabled);
        File.WriteAllText(manifest.SourcePath, updated, new System.Text.UTF8Encoding(false));

        manifest.EnabledByUser = false;
        manifest.UserModified = true;
        manifest.Enabled = effective;
        manifest.EnableSource = policyDefault is null
            ? PluginEnableSource.Default
            : (policyDefault.Value ? PluginEnableSource.PolicyEnabled : PluginEnableSource.PolicyDisabled);
    }

    /// <summary>在 plugin.json 文本中替换/插入 enabled 字段（保留其余原文与缩进风格）。</summary>
    private static string RewriteEnabled(string json, bool enabled)
    {
        var lines = json.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("\"enabled\"", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            var indent = line[..(line.Length - trimmed.Length)];
            // 保留原行结尾的逗号（enabled 可能是最后一个成员）
            var hasComma = line.TrimEnd().EndsWith(",", StringComparison.Ordinal);
            lines[i] = $"{indent}\"enabled\": {(enabled ? "true" : "false")}{(hasComma ? "," : string.Empty)}";
            return string.Join('\n', lines);
        }

        // 无 enabled 字段：插到对象结尾之前
        var closing = json.LastIndexOf('}');
        if (closing < 0) return json;
        var body = json[..closing].TrimEnd();
        var tail = body.EndsWith(",", StringComparison.Ordinal) ? "" : ",";
        return $"{body}{tail}\n  \"enabled\": {(enabled ? "true" : "false")}\n}}\n";
    }

    /// <summary>在 plugin.json 文本中替换/插入 enabledByUser 字段（保留其余原文）。</summary>
    private static string RewriteEnabledByUser(string json, bool byUser)
    {
        var lines = json.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("\"enabledByUser\"", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            var indent = line[..(line.Length - trimmed.Length)];
            var hasComma = line.TrimEnd().EndsWith(",", StringComparison.Ordinal);
            lines[i] = $"{indent}\"enabledByUser\": {(byUser ? "true" : "false")}{(hasComma ? "," : string.Empty)}";
            return string.Join('\n', lines);
        }

        // 无 enabledByUser 字段：插到对象结尾之前
        var closing = json.LastIndexOf('}');
        if (closing < 0) return json;
        var body = json[..closing].TrimEnd();
        var tail = body.EndsWith(",", StringComparison.Ordinal) ? "" : ",";
        return $"{body}{tail}\n  \"enabledByUser\": {(byUser ? "true" : "false")}\n}}\n";
    }

    /// <summary>在 plugin.json 文本中替换/插入 defaultEnabled 字段（保留其余原文）。</summary>
    private static string RewriteDefaultEnabled(string json, bool defaultEnabled)
    {
        var lines = json.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("\"defaultEnabled\"", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            var indent = line[..(line.Length - trimmed.Length)];
            var hasComma = line.TrimEnd().EndsWith(",", StringComparison.Ordinal);
            lines[i] = $"{indent}\"defaultEnabled\": {(defaultEnabled ? "true" : "false")}{(hasComma ? "," : string.Empty)}";
            return string.Join('\n', lines);
        }

        var closing = json.LastIndexOf('}');
        if (closing < 0) return json;
        var body = json[..closing].TrimEnd();
        var tail = body.EndsWith(",", StringComparison.Ordinal) ? "" : ",";
        return $"{body}{tail}\n  \"defaultEnabled\": {(defaultEnabled ? "true" : "false")}\n}}\n";
    }
}

// ===== P1 插件宿主契约（docs/plugin-architecture.md §3）=====

/// <summary>插件入口：宿主加载程序集后实例化的第一个类型。</summary>
public interface IPluginEntryPoint
{
    /// <summary>展示名（日志与设置页用）。</summary>
    string DisplayName { get; }

    /// <summary>宿主加载后调用一次：插件在此注册元数据插件、UI 按钮等。</summary>
    void Init(IPluginHostContext context);

    /// <summary>宿主退出前调用（best-effort，异常由宿主吞掉）。</summary>
    void Shutdown();
}

/// <summary>宿主上下文：插件访问宿主能力的唯一入口。</summary>
public interface IPluginHostContext
{
    /// <summary>宿主版本（SemVer，如 "0.3.0"，用于 MinHostVersion 兼容判断）。</summary>
    string HostVersion { get; }

    /// <summary>元数据读写工具（Worker 与 App 均可用）。</summary>
    IMetadataWorkbench Metadata { get; }

    /// <summary>左侧栏只读状态快照。</summary>
    ISidebarState Sidebar { get; }

    /// <summary>文件夹浏览只读状态快照。</summary>
    IBrowserState Browser { get; }

    /// <summary>UI 注册面（App 内有效；Worker 内为 no-op）。</summary>
    IUiSurface Ui { get; }

    /// <summary>
    /// 宿主 Git 命令面（App 内有效；Worker 内为 null）。
    /// Git 插件用它把「拉取更新/保存并上传/历史/比对/远程配置」按钮挂上宿主命令，
    /// 行为留在宿主（对话框、配置写回），入口归插件（docs/plugin-architecture.md §5）。
    /// </summary>
    IGitUiCommands? GitUi { get; }

    /// <summary>
    /// 宿主 WIKI 命令面（App 内有效；Worker 内为 null）。
    /// WIKI 插件用它把「知识WIKI」按钮挂上宿主命令，元数据面板窗口留在宿主（docs/plugin-architecture.md §5）。
    /// </summary>
    IWikiUiCommands? WikiUi { get; }

    /// <summary>
    /// 宿主归档命令面（App 内有效；Worker 内为 null）。
    /// rsync 归档插件用它把「关键节点」「留档历史」按钮挂上宿主命令，
    /// 对话框与历史窗口留在宿主（docs/plugin-architecture.md §5）。
    /// </summary>
    IArchiveUiCommands? ArchiveUi { get; }
}

/// <summary>宿主暴露给 rsync 归档插件的命令集。</summary>
public interface IArchiveUiCommands
{
    /// <summary>标记关键节点（执行一次 rsync 归档）。</summary>
    System.Windows.Input.ICommand ArchiveCommand { get; }

    /// <summary>查看留档历史。</summary>
    System.Windows.Input.ICommand ArchiveHistoryCommand { get; }
}

/// <summary>宿主暴露给 WIKI 插件的工具栏命令集。</summary>
public interface IWikiUiCommands
{
    /// <summary>打开元数据构建面板（MetadataConfigWindow）。</summary>
    System.Windows.Input.ICommand OpenMetadataConfigCommand { get; }
}

/// <summary>宿主暴露给 Git 插件的工具栏命令集（均为可绑定 ICommand，带 CanExecute 状态）。</summary>
public interface IGitUiCommands
{
    /// <summary>版本比对（HEAD 基线 vs 工作区）。</summary>
    System.Windows.Input.ICommand CompareConflictCommand { get; }

    /// <summary>拉取更新。</summary>
    System.Windows.Input.ICommand PullCommand { get; }

    /// <summary>保存并上传。</summary>
    System.Windows.Input.ICommand SavePushCommand { get; }

    /// <summary>Git 状态及历史。</summary>
    System.Windows.Input.ICommand HistoryCommand { get; }

    /// <summary>Git 远程配置。</summary>
    System.Windows.Input.ICommand BindGitCommand { get; }
}

/// <summary>
/// 元数据工作台：给插件与 KnowledgeIngestService 同一套 catalog/describe/标签工具。
/// 所有方法按传入的空间根目录定位 .7z 元数据，实现本身无状态。
/// </summary>
public interface IMetadataWorkbench
{
    /// <summary>按内容 hash 读取单条 catalog 记录。</summary>
    CatalogEntry? LoadEntry(string spacePath, string hash);

    /// <summary>经 CatalogStore 原子写回 catalog 记录。</summary>
    void SaveEntry(string spacePath, CatalogEntry entry);

    /// <summary>把插件产出的要点追加到 describe 文件（人工正文不受影响；失败静默）。</summary>
    void AppendDescribeHighlights(string spacePath, string hash, IReadOnlyList<string> highlights);

    /// <summary>复用内置规则打标器产出命名空间标签。</summary>
    IReadOnlyList<string> BuildRuleTags(string relativePath, long size, DateTimeOffset mtime, bool gitTracked);

    /// <summary>请求重建 FTS 检索投影（best-effort）。</summary>
    void RequestReindex(string spacePath);
}

/// <summary>左侧栏状态快照。</summary>
public sealed record SidebarSnapshot(string Section, string? SelectedNodeName, int SpaceCount);

public interface ISidebarState
{
    SidebarSnapshot Current { get; }

    /// <summary>宿主状态变化后触发（插件据此自行拉取新快照）。</summary>
    event EventHandler? Changed;
}

/// <summary>文件浏览状态快照。</summary>
public sealed record BrowserSnapshot(string? CurrentPath, string? SpaceName, string? SourceKind, int SelectedFileCount, string? GitDisplay);

public interface IBrowserState
{
    BrowserSnapshot Current { get; }

    /// <summary>宿主状态变化后触发（插件据此自行拉取新快照）。</summary>
    event EventHandler? Changed;
}

/// <summary>
/// 工具栏约定图标集（P7）：插件**只能**从这里选择图标，宿主按统一的线性矢量图标渲染。
/// 禁止插件自带 emoji/字符图标——那会导致大小不一、风格不一、重复（⚙ 既是设置又是 Git 远程配置）。
/// 需要新图标时，在本枚举追加一项并同步实现宿主端绘制。
/// </summary>
public enum ToolbarIcon
{
    None = 0,

    // —— 导航 ——
    Back, Forward, Up, Refresh,
    Menu, Sidebar, Preview, Search, Clear,

    // —— 文件操作 ——
    Star, Folder, Tag, Catalog, Open, Copy, Cut, Paste, Delete, Rename, Edit, Save,

    // —— 版本与对比 ——
    Compare, History, Branch, Merge, Pull, Push, Upload, Download, Remote, Sync,

    // —— 知识与元数据 ——
    Wiki, Metadata, Catalog2, Index, Tag2, Layer, Chart,

    // —— 通用 ——
    Settings, Info, Warning, Error, Check, Close, Add, Remove, More, Play, Stop
}

/// <summary>
/// 工具栏按钮视图模型：插件经 IToolbarContribution 注册。
/// 插件只定义「约定图标 + 短文字 + 悬停提示 + 命令」；按钮如何排布、
/// 空间不足时如何降级（图标+文字 → 仅图标 → 首按钮+下拉 → 更多面板），
/// 全部由宿主框架负责（docs/plugin-architecture.md §4、§7 P7）。
/// </summary>
public sealed record ToolbarButtonVm
{
    /// <summary>约定图标（只能取 <see cref="ToolbarIcon"/>；宿主按统一线性样式绘制）。</summary>
    public ToolbarIcon Icon { get; init; } = ToolbarIcon.None;

    /// <summary>短文字（空间够时显示在图标右侧，建议 2–4 字，如「拉取」「比对」）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>悬停提示（说明按钮作用，完整句）。</summary>
    public string Tooltip { get; init; } = string.Empty;

    /// <summary>点击动作（宿主命令）。</summary>
    public System.Windows.Input.ICommand? Command { get; init; }
}

/// <summary>
/// 工具栏分组：同一插件的一组按钮（如 Git 的 5 个操作）。
/// 分组是框架级概念——宿主按组渲染，每组在空间不足时整体降级为「首按钮 + 下拉」，
/// 降级逻辑由框架实现，插件只需声明分组与按钮内容。
/// </summary>
public sealed record ToolbarGroupVm
{
    /// <summary>分组标题（悬停提示用，也用于无障碍名称）。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>组内按钮（按声明顺序）。</summary>
    public IReadOnlyList<ToolbarButtonVm> Buttons { get; init; } = [];
}

public interface IUiSurface
{
    void AddToolbarGroup(ToolbarGroupVm group);

    void RemoveToolbarGroup(ToolbarGroupVm group);

    void SetGlobalStatus(string message);
}

/// <summary>工具栏贡献（P1）。插件声明分组与按钮内容；排布与收起由宿主框架负责。</summary>
public interface IToolbarContribution
{
    IReadOnlyList<ToolbarGroupVm> GetToolbarGroups();
}

// ===== P4 扩展点（docs/plugin-architecture.md §4）：详情字段 / 预览 / 设置页 =====
// 发现方式与 IToolbarContribution 一致：宿主在加载后按 manifest 能力 + 接口实现识别，
// 插件无需显式注册；Worker 只加载 metadata 能力插件，这些 UI 贡献对 Worker 无意义。

/// <summary>设置页视图模型：插件经 ISettingsContribution 产出，宿主 SettingsWindow 渲染。</summary>
public sealed record SettingsPageVm
{
    /// <summary>左列表项标题（宿主显示为「插件：<Title>」）。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>页首说明文字（可空）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>页面内容：插件构建的 UI 元素（FrameworkElement）；宿主以 ContentControl 承载。
    /// 用 object 而非 FrameworkElement，保持 Core 不引入 PresentationFramework。</summary>
    public object? Content { get; init; }
}

/// <summary>
/// 设置页贡献：宿主 SettingsWindow 左列表追加「插件：<Title>」页。
/// GetSettingsPage 在窗口打开时（UI 线程）调用一次；OnSettingsSaved 在宿主「保存设置」时（UI 线程）调用。
/// v1 契约不含宿主配置存储，插件设置项的持久化由插件自行负责（写自己的数据文件）。
/// </summary>
public interface ISettingsContribution
{
    SettingsPageVm GetSettingsPage();

    void OnSettingsSaved();
}

/// <summary>
/// 文件浏览详情字段贡献：宿主详情面板在 6 个内置行（名称/类型/大小/创建/修改/路径）之后追加插件动态字段。
/// 每次选中文件变化时调用（UI 线程）；非本插件关注的格式返回空列表。
/// </summary>
public interface IMetadataFieldContribution
{
    IReadOnlyList<(string Name, string Value)> GetFields(string filePath);
}

/// <summary>
/// 预览贡献：宿主预览链先问插件（Supports(ext) 命中即由插件渲染），未命中再走内置文本/图片渲染。
/// 图片返回 PNG/JPEG 字节（宿主负责解码）；文本格式返回 (null, 文本)。
/// </summary>
public interface IPreviewContribution
{
    bool Supports(string extension);

    (byte[]? ImageData, string Text) Render(string filePath);
}
