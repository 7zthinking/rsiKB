# KBEA 插件开发指南

面向第三方插件开发者。读完本文你将能独立写出一个可被 KBEA 加载、并在工具栏/设置页/详情面板/预览中展现功能的插件。

- **宿主版本**：0.5.0
- **契约命名空间**：`KBEA.Core.Plugins`
- **契约程序集**：`KBEA.Core.dll`（插件直接引用即可）
- **参考实现**：`src/KBEA.Plugin.Sample`（样例）、`src/KBEA.Plugin.Git`（完整功能插件）

---

## 1. 五分钟跑通第一个插件

### 1.1 创建工程

```
my-plugin/
├── MyPlugin.csproj
├── plugin.json          ← 必需：插件清单
└── MyPlugin.cs          ← 入口类
```

**MyPlugin.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <!-- 插件产物（含依赖）随 bin 输出，便于整目录拷贝 -->
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <!-- 只引用 Core：Core 是纯逻辑层，不含 UI 依赖 -->
    <ProjectReference Include="..\KBEA.Core\KBEA.Core.csproj" />
    <!-- 需要构建设置页 WPF 元素时才加（StackPanel/TextBox 等） -->
    <FrameworkReference Include="Microsoft.WindowsDesktop.App" />
  </ItemGroup>

  <ItemGroup>
    <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

> 只做工具栏按钮的插件不需要 `FrameworkReference`。
>
> `KBEA.Core` 自身带 `DiffPlex`、`Microsoft.Data.Sqlite`、`Makaretu.Dns.Multicast` 依赖，引用后会随插件输出。宿主加载插件时 `KBEA.Core` 由宿主提供、不重复加载，因此这些依赖不会造成版本冲突。

> **依赖分发规则**：插件目录里只放「插件自己直接使用」的依赖 + 入口 DLL，**不要**放 `KBEA.Core.dll` 副本。
> 插件 ALC 会优先从插件目录解析依赖，所以直接使用的第三方库必须随插件目录分发（例：`kbea-plugin-git` 带 `LibGit2Sharp.dll`）。
> 原生库按「插件目录 + `runtimes/win-x64/native/`」两处都放一份兜底。

**plugin.json**

```json
{
  "id": "my-plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "entryAssembly": "MyPlugin.dll",
  "capabilities": [ "toolbar" ],
  "minHostVersion": "0.5.0",
  "enabled": true
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `id` | ✅ | 全局唯一，建议 `kbea-plugin-xxx` 格式 |
| `name` | ✅ | 设置页与日志显示名 |
| `version` | ✅ | 插件自身版本（semver） |
| `entryAssembly` | ✅ | 入口 DLL 文件名（相对插件目录） |
| `capabilities` | ✅ | 能力声明，见 §4.2 |
| `minHostVersion` | ✅ | 兼容的宿主最低版本，低于则跳过加载并提示原因 |
| `enabled` | ✅ | 启用开关，用户可在设置页切换（写回本文件） |
| `enabledByUser` | — | 用户是否显式设置过。为 `true` 时用户的选择优先于发行方策略；宿主自动维护，你不用手写 |
| `defaultEnabled` | — | 插件自带的默认启用值（用户改动前的原值），供「恢复默认」用；宿主自动维护 |

> **发行方默认启用策略**：程序目录下的 `plugins.defaults.json`（固定命名）决定开箱默认启用哪些插件：

> ```json
> { "enabled": ["kbea-plugin-git"], "disabled": ["kbea-plugin-sample"] }
> ```

> `disabled` 优先于 `enabled`；未列出的插件沿用各自 `plugin.json` 的 `enabled`。**用户的显式选择优先于该策略** —— 用户在设置里改过的插件（`enabledByUser: true`）不受发行方预设影响。

### 1.2 写入口类

```csharp
using KBEA.Core.Plugins;

namespace MyPlugin;

public sealed class MyPlugin : IPluginEntryPoint, IToolbarContribution
{
    private IPluginHostContext? _context;

    public string DisplayName => "我的插件";

    public void Init(IPluginHostContext context) => _context = context;

    public void Shutdown() => _context = null;

    public IReadOnlyList<ToolbarGroupVm> GetToolbarGroups() =>
    [
        new ToolbarGroupVm
        {
            Title = "我的插件",
            Buttons =
            [
                new ToolbarButtonVm
                {
                    Icon = ToolbarIcon.Star,
                    Label = "收藏",
                    Tooltip = "收藏当前文件到我的插件清单",
                    Command = new RelayCommand(() =>
                        _context?.Ui.SetGlobalStatus("已收藏"))
                }
            ]
        }
    ];
}
```

**极简 ICommand**（避免引入额外依赖）：

```csharp
public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
```

### 1.3 安装与验证

1. 编译插件，把**整个输出目录**（含 DLL 与 plugin.json）复制到 KBEA 安装目录下的 `plugins/<你的插件id>/`
2. 启动 KBEA → 设置 → 插件管理，确认你的插件状态为「已加载」
3. 工具栏应出现你的分组

**加载失败排查**：插件加载失败不会导致 KBEA 崩溃，失败原因显示在设置页「插件管理」中（如"未加载：宿主版本过低"、"加载失败：xxx"），同时写入诊断日志。

---

## 2. 插件生命周期

```
KBEA 启动
  └─ PluginCatalog.DiscoverWithDirectories()
       └─ 递归扫描 plugins/**/plugin.json
  └─ PluginLoader.Load()
       ├─ 校验 enabled / minHostVersion / 入口程序集存在
       ├─ PluginLoadContext（独立 ALC）加载 DLL
       └─ 反射查找 IPluginEntryPoint 实现
  └─ IPluginEntryPoint.Init(context)      ← 你在这里注册能力
  └─ （运行期）宿主在对应时机回调你的贡献接口
KBEA 退出
  └─ IPluginEntryPoint.Shutdown()          ← 异常会被宿主吞掉
```

### 关键约定

| 事项 | 规则 |
|---|---|
| `Init` 调用时机 | 宿主加载完成后调用一次 |
| `Init` 线程 | 宿主加载线程（App 启动阶段），**不是** UI 线程 |
| `Shutdown` | 宿主退出前 best-effort 调用，抛异常不影响退出 |
| 重复 Init | 不会发生；但 `PluginManager.Register` 等注册表自带去重 |
| 运行时热重载 | **不支持**。禁用/启用插件需重启 KBEA |
| 依赖隔离 | 插件的第三方依赖从插件目录加载；但 `KBEA.Core` 不重复加载（保证契约类型同源） |

---

## 3. 你能拿到什么：宿主上下文

`IPluginHostContext` 是插件访问宿主能力的**唯一入口**。

```csharp
public interface IPluginHostContext
{
    string HostVersion { get; }              // "0.5.0"

    IMetadataWorkbench Metadata { get; }     // 元数据读写
    ISidebarState   Sidebar { get; }         // 左侧栏状态（只读快照）
    IBrowserState   Browser { get; }         // 文件浏览状态（只读快照）
    IUiSurface      Ui { get; }              // UI 注册面

    IGitUiCommands? GitUi { get; }           // 宿主 Git 命令（Worker 内为 null）
    IWikiUiCommands? WikiUi { get; }         // 宿主 WIKI 命令（Worker 内为 null）
    IArchiveUiCommands? ArchiveUi { get; }   // 宿主归档命令（Worker 内为 null）
}
```

### 3.1 IMetadataWorkbench（元数据读写）

按传入的空间根目录定位 `.7z` 元数据，实现无状态。

```csharp
CatalogEntry? LoadEntry(string spacePath, string hash);
void SaveEntry(string spacePath, CatalogEntry entry);
void AppendDescribeHighlights(string spacePath, string hash, IReadOnlyList<string> highlights);
IReadOnlyList<string> BuildRuleTags(string relativePath, long size, DateTimeOffset mtime, bool gitTracked);
void RequestReindex(string spacePath);
```

| 方法 | 用途 |
|---|---|
| `LoadEntry` | 按内容 hash 读取单条编目记录 |
| `SaveEntry` | 经 CatalogStore 原子写回 |
| `AppendDescribeHighlights` | 把插件产出的要点追加到 describe 文件（人工正文不受影响；失败静默） |
| `BuildRuleTags` | 复用内置规则打标器产出命名空间标签 |
| `RequestReindex` | 请求重建 FTS 检索投影（best-effort） |

> ⚠️ 插件**不能**直接访问 SQLite（`MetadataStore` 未开放）。所有元数据读写走这个接口。

### 3.2 状态快照（ISidebarState / IBrowserState）

```csharp
public sealed record SidebarSnapshot(string Section, string? SelectedNodeName, int SpaceCount);
public sealed record BrowserSnapshot(string? CurrentPath, string? SpaceName, string? SourceKind,
                                     int SelectedFileCount, string? GitDisplay);
```

```csharp
public interface ISidebarState
{
    SidebarSnapshot Current { get; }
    event EventHandler? Changed;    // 宿主状态变化后触发
}
```

用法：订阅 `Changed`，在回调里重新拉 `Current`（**不要缓存快照值**）。

```csharp
public void Init(IPluginHostContext context)
{
    _context = context;
    context.Browser.Changed += (_, _) =>
    {
        var snap = context.Browser.Current;
        _status.Text = $"当前目录：{snap.CurrentPath ?? "（无）"}";
    };
}
```

### 3.3 IUiSurface（UI 注册面）

```csharp
public interface IUiSurface
{
    void AddToolbarGroup(ToolbarGroupVm group);
    void RemoveToolbarGroup(ToolbarGroupVm group);
    void SetGlobalStatus(string message);
}
```

- 三个方法**任意线程可调**（宿主内部会切回 UI 线程）
- `AddToolbarGroup` 内部已去重（同一实例重复添加不会重复显示）

---

## 4. 扩展点

### 4.1 工具栏（IToolbarContribution）

```csharp
public interface IToolbarContribution
{
    IReadOnlyList<ToolbarGroupVm> GetToolbarGroups();
}
```

```csharp
public sealed record ToolbarGroupVm
{
    public string Title { get; init; } = string.Empty;              // 分组标题（悬停提示/无障碍名）
    public IReadOnlyList<ToolbarButtonVm> Buttons { get; init; } = [];
}

public sealed record ToolbarButtonVm
{
    public ToolbarIcon Icon { get; init; } = ToolbarIcon.None;      // 约定图标（见 §4.2）
    public string Label { get; init; } = string.Empty;              // 短文字，建议 2-4 字
    public string Tooltip { get; init; } = string.Empty;            // 悬停提示，完整句
    public ICommand? Command { get; init; }                         // 点击动作
}
```

**宿主负责排布，你只管声明**：空间不足时宿主自动四档降级 ——

```
Full (图标+文字) → IconsOnly (仅图标) → Collapsed (首按钮 + ▾ 下拉) → Overflow (进「⋮更多」面板)
```

降级逻辑由 `ToolbarOverflowPlanner` 计算，插件**不需要也不应该**感知。

#### 图标规范（重要）

插件**只能**从 `ToolbarIcon` 枚举取值，宿主用统一的线性矢量图标渲染（Lucide 风格）。

**禁止**插件自带 emoji 或字符图标（`⚙`、`★`、`📁`）——那会导致大小不一、风格不一、功能重复（历史上 ⚙ 同时被"设置"和"Git 远程配置"用过）。

可用枚举值：

| 分类 | 值 |
|---|---|
| 导航 | `Back` `Forward` `Up` `Refresh` `Menu` `Sidebar` `Preview` `Search` `Clear` |
| 文件操作 | `Star` `Folder` `Tag` `Catalog` `Open` `Copy` `Cut` `Paste` `Delete` `Rename` `Edit` `Save` |
| 版本对比 | `Compare` `History` `Branch` `Merge` `Pull` `Push` `Upload` `Download` `Remote` `Sync` |
| 知识元数据 | `Wiki` `Metadata` `Catalog2` `Index` `Tag2` `Layer` `Chart` |
| 通用 | `Settings` `Info` `Warning` `Error` `Check` `Close` `Add` `Remove` `More` `Play` `Stop` |

需要新图标：向主仓库提 issue 请求在枚举中追加（追加是兼容变更）。

#### 分组建议

同一插件的按钮放在一个 `ToolbarGroupVm` 里，宿主会在组间自动绘制分隔线。

### 4.2 能力声明与识别方式

宿主按 **manifest 能力 + 接口实现** 双匹配识别扩展点——你实现了接口就自动生效，**无需显式注册**。

| 能力字符串 | 对应接口 | 生效方式 |
|---|---|---|
| `toolbar` | `IToolbarContribution` | 加载后立即调用 `GetToolbarGroups()`，注册工具栏组 |
| `settings` | `ISettingsContribution` | 设置窗口打开时回调 |
| `fields` | `IMetadataFieldContribution` | 选中文件变化时回调 |
| `preview` | `IPreviewContribution` | 预览时优先询问插件 |
| `about` | `IAboutContribution` | 设置 → 关于我们 页追加插件卡片 |
| `git` | `IGitOperations` 实现 | 通过 `GitOperationsProvider.Register()` 替换宿主 Git 服务 |
| `wiki` | `IWikiOperations` 实现 | 通过 `WikiOperationsProvider.Register()` 替换宿主 WIKI 服务 |

> 能力字符串是**提示性**的：真正决定是否接入的是接口是否实现。`capabilities` 写对有助于可读性与宿主优化。

### 4.3 设置页（ISettingsContribution）

```csharp
public interface ISettingsContribution
{
    SettingsPageVm GetSettingsPage();   // 设置窗口打开时（UI 线程）调用一次
    void OnSettingsSaved();              // 宿主「保存设置」时（UI 线程）调用
}

public sealed record SettingsPageVm
{
    public string Title { get; init; } = string.Empty;        // 宿主显示为「插件：<Title>」
    public string Description { get; init; } = string.Empty;  // 页首说明
    public object? Content { get; init; }                    // 你构建的 UI 元素（FrameworkElement）
}
```

宿主以 `ContentControl` 承载你的 `Content`，设置窗口左侧会追加「插件：<Title>」列表项。

```csharp
public SettingsPageVm GetSettingsPage()
{
    var box = new TextBox { Text = _note };
    _noteRef = box;
    return new SettingsPageVm
    {
        Title = "示例设置",
        Description = "由我的插件贡献。",
        Content = new StackPanel { Children = { new TextBlock { Text = "备注" }, box } }
    };
}

public void OnSettingsSaved()
{
    if (_noteRef is not null) _note = _noteRef.Text;   // 取最新值
    SaveToMyOwnFile(_note);                             // 持久化由插件自理
}
```

**设置持久化**：v1 契约不含宿主配置存储，插件需自行写自己的数据文件（如 `%APPDATA%/my-plugin/settings.json`）。`GetSettingsPage` 每次开窗都会被调用，不要假设控件实例还在。

### 4.4 详情字段（IMetadataFieldContribution）

```csharp
public interface IMetadataFieldContribution
{
    IReadOnlyList<(string Name, string Value)> GetFields(string filePath);
}
```

宿主详情面板在 6 个内置行（名称/类型/大小/创建时间/修改时间/路径）之后追加你的行。每次选中文件变化时调用（UI 线程）。

**只处理你关心的格式**，其余返回空列表，避免污染内置信息：

```csharp
public IReadOnlyList<(string, string)> GetFields(string filePath) =>
    Path.GetExtension(filePath).Equals(".myfmt", StringComparison.OrdinalIgnoreCase)
        ? [("我的插件", "格式版本 2")]
        : [];
```

### 4.5 预览（IPreviewContribution）

```csharp
public interface IPreviewContribution
{
    bool Supports(string extension);
    (byte[]? ImageData, string Text) Render(string filePath);
}
```

预览链先问插件：`Supports` 命中即由插件渲染；未命中走内置文本/图片渲染。

- 图片格式：返回 `(pngOrJpegBytes, "")`，宿主负责解码
- 文本格式：返回 `(null, "内容")`

```csharp
public bool Supports(string extension) =>
    extension.Equals(".myfmt", StringComparison.OrdinalIgnoreCase);

public (byte[]? ImageData, string Text) Render(string filePath)
{
    try
    {
        return (null, File.ReadAllText(filePath));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return (null, $"预览失败：{ex.Message}");
    }
}
```

### 4.6 元数据插件（IMetadataPlugin）—— 文件整理阶段

如果你要参与"整理/扫描"阶段的元数据抽取，实现这个接口：

```csharp
public interface IMetadataPlugin
{
    string PluginId { get; }
    string PluginName { get; }
    string[] SupportedExtensions { get; }
    FileMetadata Scan(string filePath);
    string[] ExtendedProperties { get; }        // 扩展属性键，如 ["word_count"]

    MetadataLevel Level => MetadataLevel.Basic; // 默认基础层
    bool Enabled => true;                        // 用户可按库关闭
}

public interface ITypeMetadataPlugin : IMetadataPlugin
{
    IReadOnlyList<string> DescribeHighlights(string filePath, FileMetadata metadata);
}
```

`MetadataLevel` 三层：

| 层级 | 含义 |
|---|---|
| `Basic` | 通用规则标签与事实提取，对所有文件生效 |
| `Type` | 按文件格式专用解析（PPT 大纲、PDF 目录、Word 标题） |
| `Semantic` | 业务专题语义元数据（摘要、语义标签、关联），依赖模型 |

注册方式（`PluginManager` 按 PluginId 去重）：

```csharp
public void Init(IPluginHostContext context)
{
    PluginManager.Instance.Register(new MyMetadataPlugin());
}
```

### 4.7 关于我们卡片（IAboutContribution）

插件可在 设置 → 关于我们 页的「插件提供」分组下追加自己的卡片（如插件介绍、作者信息、文档链接）：

```csharp
public interface IAboutContribution
{
    AboutCardInfo? GetAboutCard();  // 返回 null 表示本次不展示
}

public sealed record AboutCardInfo
{
    public string Title { get; init; }       // 卡片标题
    public string Description { get; init; } // 一行说明
    public string? Version { get; init; }    // 版本号（可空，展示为「版本 x」）
    public string? LinkUrl { get; init; }    // 标题跳转链接（可空）
    public AboutCardPlacement Placement { get; init; }  // 摆放位置（默认 Plugin）
    public int SortOrder { get; init; }      // 组内排序，小者在前
}

public enum AboutCardPlacement
{
    Plugin = 0,        // 「插件提供」分组（默认）
    Dependencies = 1,  // 并入「开源依赖」分组，与内置依赖卡一起排序
    First = 2,         // 置顶：排在品牌主卡片之前（页面最顶部）
}
```

**排序规则**：各组内按 `SortOrder` 升序**稳定排序**（同值保持相对顺序）。内置依赖卡全部为 0、按声明顺序排列——插件卡想插在它们中间就填对应的权重值（如 50 插到中间、100 排到最后），填 0 则排在内置依赖之前。

示例（打样插件 SamplePlugin 的实现）：

```csharp
public AboutCardInfo? GetAboutCard() => new()
{
    Title = "示例插件",
    Version = "0.1.0",
    Description = "框架打样插件：演示各扩展点的接入方式。",
    LinkUrl = "https://github.com/7zthinking/rsiKB",
    Placement = AboutCardPlacement.Dependencies, // 并入「开源依赖」组（不填则进「插件提供」组）
    SortOrder = 100                              // 排在内置依赖卡之后
};
```

`GetAboutCard` 在设置窗口打开时（UI 线程）调用一次；单个插件失败不影响其余卡片渲染。

---

## 5. 服务替换模式：接管宿主某项能力

宿主允许插件**整体替换**某类操作（如 Git 版本控制），实现方式是一个静态 Provider。

```csharp
// src/KBEA.Core/Git/GitContracts.cs
public static class GitOperationsProvider
{
    public static IGitOperations? Instance { get; private set; }
    public static void Register(IGitOperations implementation) => Instance = implementation;
}
```

你的插件在 `Init` 里注册实现：

```csharp
public void Init(IPluginHostContext context)
{
    GitOperationsProvider.Register(new MyGitService());
}
```

宿主的消费点（`MainViewModel`、`DialogService`、`SpaceWindow` 等）都经 `GitOperationsProvider.Instance` 解析，**插件缺失时为 null → 宿主优雅降级**（相关按钮不出现，操作静默跳过）。

这是"把宿主某个可选功能整体插件化"的推荐做法。Git 插件（`src/KBEA.Plugin.Git`）是完整范例。

---

## 6. 宿主命令面：挂按钮不写逻辑

如果你的功能需要宿主已有能力（如打开元数据面板），宿主提供了命令面——**行为留在宿主，入口归插件**。

```csharp
public interface IGitUiCommands
{
    ICommand CompareConflictCommand { get; }   // 版本比对
    ICommand PullCommand { get; }               // 拉取更新
    ICommand SavePushCommand { get; }           // 保存并上传
    ICommand HistoryCommand { get; }            // Git 状态及历史
    ICommand BindGitCommand { get; }            // Git 远程配置
}

public interface IWikiUiCommands
{
    ICommand OpenMetadataConfigCommand { get; } // 打开元数据构建面板
}

public interface IArchiveUiCommands
{
    ICommand ArchiveCommand { get; }            // 标记关键节点（归档）
    ICommand ArchiveHistoryCommand { get; }     // 查看留档历史
}
```

用法：

```csharp
public IReadOnlyList<ToolbarGroupVm> GetToolbarGroups()
{
    var ui = _context?.GitUi;
    if (ui is null) return [];      // Worker 等无 UI 宿主环境：返回空组，不报错
    return [new ToolbarGroupVm { Title = "Git", Buttons = [ /* … */ ] }];
}
```

### 6.1 接管「关键节点留档」（服务替换范例）

宿主为归档能力预留了完整契约与 UI：**你实现逻辑，宿主负责对话框、进度条与留档历史窗口**。

```csharp
// KBEA.Core.Archive/ArchiveContracts.cs
public sealed record ArchiveRecord(string Id, string SpaceName, string NodeName,
    string Destination, string Note, DateTimeOffset CreatedAt, string Status);

public interface IArchiveOperations
{
    Task<ArchiveRecord> CreateAsync(string spaceName, string spacePath, string note,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    IReadOnlyList<ArchiveRecord> GetHistory(string spacePath);
}
```

插件里实现它并在 `Init` 注册：

```csharp
public void Init(IPluginHostContext context)
{
    ArchiveOperationsProvider.Register(new MyArchiveService());   // 静态单例注册
}
```

再挂上工具栏按钮：

```csharp
public IReadOnlyList<ToolbarGroupVm> GetToolbarGroups()
{
    var ui = _context?.ArchiveUi;
    if (ui is null) return [];
    return [new ToolbarGroupVm
    {
        Title = "归档",
        Buttons =
        [
            new ToolbarButtonVm { Icon = ToolbarIcon.Save, Label = "关键节点",
                Tooltip = "标记关键节点：把当前空间归档到归档根目录", Command = ui.ArchiveCommand },
            new ToolbarButtonVm { Icon = ToolbarIcon.History, Label = "留档历史",
                Tooltip = "查看该空间的留档历史记录", Command = ui.ArchiveHistoryCommand }
        ]
    }];
}
```

宿主会自动处理：备注输入对话框、进度回调（`IProgress<string>`）、成功/失败提示、留档历史窗口，以及插件未加载时的优雅降级提示。

> 留档历史建议存自己的数据库（如空间目录下的 `.7z/` 子目录），别写进宿主的 `meta.db` —— 那是宿主的内部存储。

---

## 7. 线程模型

| 回调 | 线程 | 你该做什么 |
|---|---|---|
| `Init` / `Shutdown` | 宿主加载线程（非 UI 线程） | 不要直接操作 UI 控件 |
| `GetToolbarGroups` | 加载线程 | 只构造数据，不碰 UI |
| `GetSettingsPage` | **UI 线程** | 可以创建 WPF 元素 |
| `OnSettingsSaved` | **UI 线程** | 可以读控件值 |
| `GetFields` | **UI 线程** | 快速返回，别做重 IO |
| `Render`（预览） | **UI 线程** | 大文件注意耗时 |
| `ISidebarState.Changed` / `IBrowserState.Changed` | 宿主触发线程 | 回调里重新拉 `Current` |
| `IUiSurface.*` | 任意线程可调 | 宿主内部会切回 UI 线程 |

**黄金法则**：耗时操作（读大文件、跑外部命令）放后台线程，回 UI 时用 `Dispatcher.Invoke`。

---

## 8. 宿主环境差异（Worker 模式）

同一套契约在两个宿主中加载：

| 宿主 | `GitUi`/`WikiUi` | `Ui` | 用途 |
|---|---|---|---|
| **App**（主程序） | 有 | 有 | 完整功能 |
| **Worker**（后台进程） | `null` | no-op | 只跑元数据扫描/整理 |

插件在 Worker 中加载时：
- `context.GitUi` / `context.WikiUi` 为 `null` → **必须判空**，返回空工具栏组
- `context.Ui.*` 调用无效果（no-op，不报错）

```csharp
public IReadOnlyList<ToolbarGroupVm> GetToolbarGroups()
{
    var ui = _context?.GitUi;
    if (ui is null) return [];     // ← 关键：无 UI 宿主降级
    // …
}
```

---

## 9. 开发规范与限制

### 必须遵守

1. **图标只从 `ToolbarIcon` 枚举取**，禁止 emoji/字符图标
2. **`Label` 2-4 字**，功能用 `Tooltip` 写完整句
3. **不感知排布**——只声明分组与按钮内容
4. **`GitUi`/`WikiUi`/`ArchiveUi` 必须判空**（Worker 环境）
5. **插件设置自行持久化**（v1 契约无宿主配置存储）
6. **异常自己兜住**——宿主会吞掉你的异常并写日志，但用户看不到效果

### 当前不支持

| 需求 | 现状 |
|---|---|
| 自定义对话框 / 模态窗口 | 无接口，需通过命令面让宿主弹 |
| 主菜单项 / 右键菜单注册 | 无接口 |
| 左侧栏自定义节点 | 无接口 |
| 运行时热重载（卸载插件） | 不支持，需重启 |
| 直接访问宿主的 SQLite | 未开放，走 `IMetadataWorkbench`；插件可自建独立数据库 |
| 团队共享（TeamShare） | 未开放 |
| 插件权限 / 信任模型 | 暂无（插件可执行任意代码与文件 IO） |

> 想要其中某项能力？向主仓库提 issue 描述场景——这正是扩展点设计的输入。

### 安全提醒

插件是**任意代码**。宿主当前不做签名校验（计划中）。请只安装来源可信的插件。

---

## 10. 完整能力速查表

| 我想…… | 实现接口 | capability |
|---|---|---|
| 在工具栏加按钮 | `IToolbarContribution` | `toolbar` |
| 在设置窗口加配置页 | `ISettingsContribution` | `settings` |
| 在文件详情里加字段 | `IMetadataFieldContribution` | `fields` |
| 接管某种文件的预览 | `IPreviewContribution` | `preview` |
| 在「关于我们」页加介绍卡片 | `IAboutContribution` | `about` |
| 参与文件整理的元数据抽取 | `IMetadataPlugin` / `ITypeMetadataPlugin` | — |
| 替换宿主的某类操作（如 Git、归档） | 实现对应 `IXxxOperations` + 静态 `Provider.Register` | `git` / `wiki` / `archive` |
| 读宿主当前浏览位置 | `context.Browser.Current` | — |
| 读宿主当前侧栏状态 | `context.Sidebar.Current` | — |
| 读/写编目元数据 | `context.Metadata` | — |
| 在状态栏显示提示 | `context.Ui.SetGlobalStatus()` | — |
| 挂宿主已有命令 | `context.GitUi` / `context.WikiUi` / `context.ArchiveUi` | — |

---

## 11. 常见问题

**Q：插件没被加载？**
1. 确认 `plugin.json` 在插件目录下且 `enabled: true`
2. 确认 `entryAssembly` 文件名与实际 DLL 一致
3. 确认 `minHostVersion` 不高于宿主版本（设置页会显示具体原因）
4. 设置 → 插件管理看状态描述；诊断日志有详细异常

**Q：工具栏按钮没出现？**
1. `plugin.json` 的 `capabilities` 是否含 `toolbar`
2. 是否实现了 `IToolbarContribution`
3. `GetToolbarGroups()` 是否返回了空列表（例如 `GitUi` 为 null 提前 return）

**Q：怎么在插件里弹窗？**
当前无对话框接口。做法：把逻辑放在宿主命令面（宿主弹窗），插件只提供入口；或用 `SetGlobalStatus` 做轻提示。

**Q：能改宿主界面吗？**
不能。宿主 UI 由宿主统一管理（图标、排布、主题），插件只贡献声明式内容。

**Q：怎么调试？**
把插件 DLL 拷到 `plugins/` 后用 `dotnet run` 启动宿主会更快迭代；异常写宿主诊断日志。

---

## 12. 相关文件

| 路径 | 内容 |
|---|---|
| `src/KBEA.Core/Plugins/PluginContracts.cs` | 全部契约定义（权威来源） |
| `src/KBEA.Core/Archive/ArchiveContracts.cs` | 归档契约（`IArchiveOperations` + `ArchiveOperationsProvider`）——实现由插件提供 |
| `src/KBEA.Plugin.Sample/` | 样例插件（覆盖全部扩展点） |
| `src/KBEA.Plugin.Git/` | 完整功能插件（服务替换 + 工具栏） |
| `src/KBEA.Plugin.Wiki/` | 最小 UI 入口范例 |
| `src/KBEA.Core/Plugins/PluginEnablePolicy.cs` | 发行方默认启用策略（plugins.defaults.json） |
| `src/KBEA.Core/Plugins/PluginLoader.cs` | ALC 加载与校验实现 |
| `src/KBEA.App/Services/PluginHostService.cs` | 宿主侧接入实现 |
