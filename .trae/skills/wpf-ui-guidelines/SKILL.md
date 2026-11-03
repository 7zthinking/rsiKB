---
name: wpf-ui-guidelines
description: KBEA 项目（WPF/.NET 8）的界面样式与交互规范。修改 XAML 样式、主题色板、控件模板、深色模式、对比度、无障碍或布局时使用。适用于 App.xaml 资源字典、ThemeService 主题切换、各 View 窗口样式、DataGrid/TreeView/ListBox 模板等界面改动。
---

# KBEA WPF 界面规范

本 skill 为 KBEA（知识库自进化助手，WPF + .NET 8 + CommunityToolkit.Mvvm）定制。
内容来源：[ui-ux-pro-max-skill](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill) 的 WPF 专项规则与通用 UX 规则，已剔除 Web 专属内容，并按本项目实测对比度重写。

**只读参考数据（不要修改）**：
- `.trae/references/wpf-guidelines.csv` — 56 条 WPF 专项规则（原始出处，含官方文档链接）
- `.trae/references/ux-guidelines.csv` — 119 条通用 UX 规则（已过滤掉 Web/移动专属项后的判断依据）

---

## 一、本项目现状（改样式前必读）

样式资源集中在两处，改动必须二者同步：

| 位置 | 作用 | 改法 |
|---|---|---|
| [src/KBEA.App/App.xaml](file:///C:/Users/qizic/Desktop/Projects/rsiKB/src/KBEA.App/App.xaml) | 12 个色板键 + 全部控件隐式样式/模板 | 改色板键、控件模板、圆角、间距 |
| [src/KBEA.App/Services/ThemeService.cs](file:///C:/Users/qizic/Desktop/Projects/rsiKB/src/KBEA.App/Services/ThemeService.cs) | 亮/暗两套色板的 hex 值 | 改色值 |

**关键约束**：
- 色板只有 12 个语义键：`WindowBackground` `PanelBackground` `SidebarBackground` `CardBackground` `TextBrush` `SecondaryTextBrush` `BorderBrush` `AccentBrush` `AccentSoftBrush` `HoverBrush` `SuccessBrush` `WarningBrush`。**加色必须同时加进两处**。
- 主题是 `ThemeService.Apply(string)` 硬替换 brush 对象，只能亮/暗两套，**没有 HighContrast**。要加第三套主题必须改成 ResourceDictionary 合并方案（见第五节）。
- 现有视觉语言是**紧凑型**：圆角 6（按钮）/ 5（列表、树）/ 10,10,0,0（标签页）；按钮 Padding `12,6`，IconButton `7,3`，列表项 `7,5`。**不要照搬 Fluent 的 CornerRadius=4 / Padding=16,8**，那是 web/移动风味。
- 主色是橙色 `#E87924`（亮）/ `#FF9B55`（暗），不是蓝色。
- 字体 `Microsoft YaHei UI, Segoe UI`，正文 13px。设计文档明确要求"中文优先微软雅黑"。
- 图标用 Unicode 字形（☰ ← → ↑ ↻ ☆ ⇩ ⇧ ◷ ★ ✦）和 Windows Shell 系统图标（`FileIconConverter`），**不要引入 SVG 图标库**。

---

## 二、无障碍硬约束（实测对比度）

下表是 KBEA 当前色板的 WCAG 对比度实测值。**改色后必须重新核算**。

| 组合 | 比值 | 结论 |
|---|---|---|
| 亮 `TextBrush #1D2733` on `PanelBackground #FFFFFF` | 15.11:1 | AAA |
| 亮 `SecondaryTextBrush #667585` on `#FFFFFF` | 4.72:1 | PASS（≥4.5） |
| 亮 `TextBrush #1D2733` on `SidebarBackground #F0F3F7` | 13.57:1 | AAA |
| 暗 `TextBrush #F1F4F8` on `PanelBackground #1C222B` | 14.50:1 | AAA |
| 暗 `SecondaryTextBrush #9AA7B7` on `#1C222B` | 6.54:1 | PASS |
| 亮 `SuccessBrush #14804A` on `#FFFFFF` | 4.98:1 | PASS |
| 亮 `WarningBrush #B54708` on `#FFFFFF` | 5.43:1 | PASS |
| **亮 `AccentBrush #E87924` on `#FFFFFF`（作文字色）** | **2.92:1** | **FAIL — 不可作正文色** |
| **白字 on `AccentBrush #E87924`（主按钮）** | **2.92:1** | **FAIL — 需改深** |
| 白字 on 主按钮 hover `#D9661B` | 3.58:1 | 仅大字/UI 元素 |
| 白字 on 主按钮 pressed `#B94E12` | 5.05:1 | PASS |
| **暗 `#F1F4F8` on `AccentBrush #FF9B55`** | **1.89:1** | **FAIL — 暗色主按钮不能配白字** |

**硬规则**：

1. **`AccentBrush` 不可直接当前景色写正文。** 亮色下仅可用于 ≥19px 加粗文字或非文字元素（图标、边框、指示条）。需要可读文字时用 `TextBrush`。
2. **主按钮（`PrimaryButton`）前景色不能是纯白配当前主色。** 亮色下需把主按钮背景压深到 ≥4.5:1（例如 `#B94E12` 级别），或改用深色前景；暗色下主色本身很亮，应改用**深色**前景（`#1C222B` 系）而非白色。
3. **`AccentSoftBrush`（选中底色）不是文字色。** 选中态文字仍用 `TextBrush`，靠底色区分而不是靠彩色字。
4. **`HoverBrush` 在亮色下仅 1.10:1**，纯靠底色几乎不可见——这是悬停态的正常表现（不需要文字可读），但**边框必须同时变化**才不显得没响应。
5. **任何新增色**都要先算对比度再落地。公式：`(L1+0.05)/(L2+0.05)`，L 为 sRGB 线性化后 `0.2126R+0.7152G+0.0722B`。
6. **不能只用颜色传达信息。** 状态必须同时有文字或图标（Git 状态列已有中文"已修改/冲突"等，遵循此惯例）。

---

## 三、必须遵守的 WPF 规则

来自 `wpf-guidelines.csv`，是本项目相关的硬约束。

### 资源与样式
- **色板集中**：颜色、画刷、尺寸一律进 `App.xaml` 的资源字典，禁止在元素上内联 `#RRGGBB`（例外：`ThemeService` 的主题字典本身，以及 `PrimaryButton` 的 hover/pressed 派生色）。
- **隐式样式 + BasedOn**：按 `TargetType` 的无 key 隐式样式做基线，变体样式用 `x:Key` + `BasedOn="{StaticResource {x:Type Button}}"` 派生，禁止复制粘贴 Setter。
- **StaticResource vs DynamicResource**：
  - 主题切换时会变的 → `DynamicResource`
  - 加载后不变的 → `StaticResource`
  - `App.xaml` 里色板是运行时被替换的 brush 对象，**必须用 DynamicResource**；`StaticResource` 会在首次解析后固化，导致切换主题不生效。
  - `ControlTemplate` 内部引用用 `TemplateBinding`，不要走资源查找。
- **ControlTemplate 只在必要时用**。不要为了"看起来像"而在控件外套 Border 伪造样式。当前 Button / ToggleButton / TreeViewItem / ListBoxItem / DataGridCell 都已有模板，改造时在原模板上改 Trigger。
- **DataTemplate 管数据呈现**：ListBox/ComboBox/ItemsControl 的项一律用 DataTemplate，不要靠 `ToString()`。

### 布局
- **Grid 管复杂布局**：用 `RowDefinition`/`ColumnDefinition`，配 `Auto`/`*`/固定值。**禁止用 Margin 偏移做定位**（`Margin="200,100,0,0"` 这类）。
- **StackPanel 管线性内容**：工具栏、按钮组用 `Orientation="Horizontal"` 的 StackPanel，不要用 12 行 Auto 的 Grid。
- **DockPanel 管停靠区**：Shell 级"顶栏 + 左侧栏 + 内容区"适合 DockPanel；当前 MainWindow 用的是 Grid + ColumnDefinition，也完全可以，**关键是别用嵌套 StackPanel 模拟停靠**。
- **避免硬编码尺寸**：主内容区用 `*`，可折叠区用 `Auto` 或触发器切换宽度。当前侧栏用 `ColumnDefinition` + DataTrigger 切 260/0 是正确范式。
- **ScrollViewer 管溢出**：长列表必须包 ScrollViewer，不要让内容被裁掉。

### 绑定与命令
- **命令用 ICommand + RelayCommand/AsyncRelayCommand**，禁止在 code-behind 里写业务逻辑的 Click 处理器。本项目已全面采用（`MainViewModel` 里 40+ 个命令）。
- **`CanExecute` 管可用性**：`AsyncRelayCommand` 的第二个参数就是 canExecute，`RefreshCommands()` 统一刷新。状态变化后必须 `NotifyCanExecuteChanged()`。
- **异步用 AsyncRelayCommand**，`[RelayCommand] private async Task XxxAsync()`，不要写 `async void` 事件处理器。
- **`CommandParameter` 传上下文**：逐项操作（删除某条）用 `CommandParameter="{Binding}"`，不要在命令里读 `SelectedItem`。
- **键盘快捷键走 InputBindings**：当前 `MainWindow` 已有 `Ctrl+B`（侧栏）、`Ctrl+T`（新建标签），新增快捷键在 `Window.InputBindings` 里加，不要写 `PreviewKeyDown`。
- **转换器只做显示转换**：bool→Visibility、string→Visibility 用 `IValueConverter`。**ViewModel 里不要出现 `Visibility`/`Brush`/`ImageSource` 等 `System.Windows.*` 类型**。
- **ViewModel 不引用 WPF 类型**（`ICommand` 除外）。这条是硬红线。

### 性能与线程
- **UI 元素只能从 UI 线程访问**。后台任务回 UI 用 `Dispatcher.BeginInvoke`（`ChangeMonitor.Changed` 已这样做）。
- **优先 async/await**，会自动回到 UI 上下文；不要每个回调都手写 `Dispatcher.BeginInvoke`。
- **CPU 密集操作放 `Task.Run`**（挂接、索引、Git、归档都已如此）。
- **Freezable 要 Freeze**：静态画刷/几何 `Freeze()` 后跳过变更追踪。
- **大列表开虚拟化**：`TreeView` 需显式 `VirtualizingStackPanel.IsVirtualizing="True"` + `VirtualizationMode="Recycling"`（`ListBox`/`ListView` 默认已开）。侧栏的本地目录树是懒加载占位节点模型，保持现状。
- **不在 OnRender 里分配资源**。

### 无障碍
- **`AutomationProperties.Name`**：纯图标按钮必须有，否则读屏念不出。当前大量按钮用 `Content="☰"` 之类字形，**这些是真实的无障碍缺口**，补自动化名时优先补 `ToolTip` 已有的那些。
- **键盘可达**：Tab 顺序与视觉顺序一致；交互元素不能只靠鼠标。
- **语义化控件**：用 `Button` 而非 `TextBlock` + 鼠标事件伪装按钮。
- **DPI**：用设备无关单位 + `Path` 矢量图形，不用位图。
- **高对比模式**：硬编码颜色在高对比下会消失。当前色板全是硬编码 hex，属已知缺口（架构文档 §19 第 9 条已记录）。

---

## 四、UX 规则（来自 `ux-guidelines.csv`，已过滤 Web 专属）

保留部分按场景归类：

### 状态反馈（Windows 桌面同样适用）
- **加载状态**：异步操作必须有反馈。当前用 `IsBusy` + `ProgressBar IsIndeterminate=True` + 状态栏文本，符合。要点：近瞬时操作不要闪烁 spinner。
- **空状态**：必须给引导和下一步动作。当前空视图 `IsEmptyView` 提供了"＋新建工作空间"和"▰打开本地目录"两个按钮，符合。
- **错误反馈**：在问题发生处附近给出明确信息与恢复路径，不能静默失败。`MountFolderAsync` / `ArchiveAsync` 都用 `_dialogs.Info` 提示，符合。
- **成功反馈**：完成动作要有确认。当前是状态栏文本（`GlobalStatus`），够用。
- **禁用状态**：明确区分不可用元素。当前按钮 `IsEnabled=False` 降到 0.4 透明度 + 箭头光标，符合。
- **进度指示**：多步骤过程要给进度。当前 `ProgressBar` 是无限动画，rsync 归档有进度上报通道但未接 UI。

### 交互
- **焦点可见**：每个可交互控件都要有可见焦点环。**当前项目是明确缺口**——`App.xaml` 里大量 `FocusVisualStyle="{x:Null}"`（Button/ToggleButton/TreeViewItem/ListBoxItem/DataGridCell）把系统焦点框整个去掉了，键盘用户无法知道焦点在哪。这是本项目最该修的无障碍问题。修法：不要全局置 null，改为自定义与主题一致的焦点视觉。
- **悬停反馈**：可点击元素要有悬停变化。当前 Button/TreeViewItem/ListBoxItem/DataGridRow 都有 `IsMouseOver` Trigger，符合。
- **按下反馈**：要有按下态。当前 Button 有 `IsPressed`。
- **加载中按钮禁用**：异步动作进行中要禁用按钮防重复提交。当前靠 `!IsBusy` 的 canExecute，符合。

### 布局与排版
- **内容跳动**：徽标、异步状态更新不应推挤相邻内容。当前 `Badge`（工作空间树的告警徽标）会改变行宽，加/删时树项会跳。可改用固定宽度容器承载徽标。
- **容器宽度**：正文不宜铺满整个宽屏。当前 DataGrid 用 `Width="*"` 铺满，列表列本身有固定宽度，可接受；但预览面板固定 310px 是合理的。
- **长文本截断**：用省略号并提供完整查看途径。当前列表项、路径、地址均用 `TextTrimming="CharacterEllipsis"` + `ToolTip`，符合。
- **层级一致**：标题与正文要有明确字号/字重差。当前 `TextBlock` 隐式样式只有 `VerticalAlignment=Center`，字号靠各处散写（`FontSize="15"`/`"20"`/`"24"`）。建议收敛成 5 级字号资源（12/13/15/18/22）供全局引用。

### 内容与文案
- **按钮文案用动词短语**：中文界面避免名词化按钮。当前"☆ 收藏文件夹"、"⇩ 拉取更新"、"⇧ 保存并上传"、"◷ Git 历史"、"★ 关键节点"符合。
- **破坏性操作需二次确认**：删除工作空间、删除挂载已有 `Confirm` 对话框，符合。**不要给高频非破坏操作加确认**。
- **日期本地化**：用 `MM-dd HH:mm`（当前 DataGrid 修改时间列）符合。
- **大数字格式化**：`DisplaySize` 已做 KB/MB/GB 分级，符合。

### AI 交互（浮动助手相关）
- **明确标注 AI**：AI 生成内容需明示。当前助手窗在无插件时显示"智能服务未启用"，符合。
- **流式输出**：等待完整文本时应逐字输出而非长时间 spinner。插件接入后适用。

---

## 五、主题机制改造指引（若需支持第三套主题或 HighContrast）

当前 `ThemeService.Apply` 硬编码 hex 替换 brush 对象，是限制。改造路径：

```
src/KBEA.App/Themes/
├── Light.xaml          # 只放颜色与画刷
├── Dark.xaml           # 只放颜色与画刷
└── HighContrast.xaml   # 可选
src/KBEA.App/Styles/
├── Controls.xaml       # 控件样式，按控件类型拆文件
└── Typography.xaml
```

规则：
1. **主题字典只放颜色/画刷，不放控件样式。** 控件样式单独一份，避免每个主题复制一遍模板。
2. `App.xaml` 合并顺序：控件样式先，主题字典**最后**（主题资源覆盖默认值）。
3. 运行时切换 = 在 `Application.Current.Resources.MergedDictionaries` 里找到主题字典的 `Source` 并整体替换。
4. 主题内引用用 `DynamicResource`（主题会换），主题字典内部引用同文件资源用 `StaticResource`。
5. `ThemeService` 里 `Application.Current.Resources[key] = new SolidColorBrush(...)` 这种直接替换的写法要废弃——它不参与 `MergedDictionaries`，切主题时不会被回收。

---

## 六、动手前后的检查清单

**改之前**：
- [ ] 确认改动落在 `App.xaml`（样式/模板）还是 `ThemeService.cs`（色值），或两者都要改
- [ ] 新增/修改色值后，核算所有相关前景/背景组合的对比度
- [ ] 确认 `DynamicResource` vs `StaticResource` 用对了（主题相关必须 Dynamic）
- [ ] 圆角、间距沿用现有紧凑值（5/6、12,6、7,5），不要改成 Fluent 的大圆角大内边距

**改之后**：
- [ ] 亮色主题下逐控件检查
- [ ] 暗色主题下逐控件检查（`ThemeService.Apply("Dark")`）
- [ ] 主按钮/次要按钮的"文字 vs 背景"对比度 ≥ 4.5:1
- [ ] 键盘 Tab 走一遍，确认焦点可见
- [ ] 读屏抽查纯图标按钮的 `AutomationProperties.Name`
- [ ] 编译通过：`bash eng/dotnet.sh build KBEA.sln --configuration Release`

---

## 七、明确不要做的事

- ❌ 不要引入 MaterialDesignInXamlToolkit / WPF UI / DevExpress 等第三方控件库——会破坏现有紧凑风格，且 `publish.ps1` 是 `PublishSingleFile=true` 单文件发布，加库会明显撑大包体。DevExpress 还有商业授权问题。
- ❌ 不要把 `ThemeService` 的硬编码 hex 换成更多硬编码 hex 而不重构字典结构。
- ❌ 不要在 ViewModel 里引入 `System.Windows.*` 类型（`Visibility`、`Brush`、`ImageSource`、`Thickness`、`Window` 等），`ICommand` 除外。
- ❌ 不要在 code-behind 里写业务逻辑；`MainWindow.xaml.cs` 只应保留视图行为（拖拽、焦点、路由事件转发）。
- ❌ 不要用 `Margin` 偏移模拟布局定位。
- ❌ 不要全局置 `FocusVisualStyle="{x:Null}"`（当前是历史欠账，修的时候改，不要沿用）。
- ❌ 不要用 emoji 做功能图标（当前用的是 Unicode 几何字形，非 emoji，保持一致）。
