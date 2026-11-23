using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KBEA.Core.Plugins;

namespace KBEA.App.Controls;

/// <summary>
/// 工具栏分组控件（框架级，docs/plugin-architecture.md §4、§7 P7）：
/// 一组按钮横向排一行。显示档位由宿主按可用宽度决定：
/// <see cref="ToolbarGroupLayout.Full"/>（图标+短文字）→
/// <see cref="ToolbarGroupLayout.IconsOnly"/>（仅图标）→
/// <see cref="ToolbarGroupLayout.Collapsed"/>（首按钮 + ▾ 下拉）→
/// <see cref="ToolbarGroupLayout.Overflow"/>（整体进「⋮ 更多」面板）。
///
/// 插件只声明约定图标（<see cref="ToolbarIcon"/>）+ 短文字 + 命令；
/// 图标统一由宿主按线性矢量风格绘制，排布与降级也全在宿主。
/// </summary>
public sealed class ToolbarGroupControl : ContentControl
{
    private readonly StackPanel _full = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _iconsOnly = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _collapsedHost = new() { Orientation = Orientation.Horizontal };
    private readonly Popup _popup = new();
    private readonly StackPanel _popupItems = new() { Orientation = Orientation.Vertical };
    private IReadOnlyList<ToolbarButtonVm> _buttonsVm = [];
    private ToolbarGroupLayout _layout = ToolbarGroupLayout.Full;
    private bool _useIconsOnly;

    public ToolbarGroupControl()
    {
        Focusable = false;
        var root = new StackPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(_full);
        root.Children.Add(_iconsOnly);
        root.Children.Add(_collapsedHost);
        Content = root;

        // 插件组：DataContext 为 ToolbarGroupVm，收到后重建按钮
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ToolbarGroupVm vm)
            {
                Title = vm.Title;
                SetButtons(vm.Buttons);
            }
        };
        if (DataContext is ToolbarGroupVm initial) { Title = initial.Title; SetButtons(initial.Buttons); }

        _popup.Child = new Border
        {
            Background = TryFindResource("CardBackground") as Brush,
            BorderBrush = TryFindResource("BorderBrush") as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            MinWidth = 170,
            Child = new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _popupItems
            }
        };
        _popup.Placement = PlacementMode.Bottom;
        _popup.AllowsTransparency = true;
    }

    /// <summary>分组标题（悬停提示与无障碍名称用）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>分组内按钮（按声明顺序）。</summary>
    public IReadOnlyList<ToolbarButtonVm> Buttons => _buttonsVm;

    /// <summary>
    /// 显示档位（宿主按可用宽度设置）。IconsOnly 与 Full 共存时以 IconsOnly 为准。
    /// </summary>
    public ToolbarGroupLayout Layout
    {
        get => _layout;
        set
        {
            if (_layout == value) return;
            _layout = value;
            ApplyState();
        }
    }

    /// <summary>是否省略文字（仅显示图标）。</summary>
    public bool UseIconsOnly
    {
        get => _useIconsOnly;
        set
        {
            if (_useIconsOnly == value) return;
            _useIconsOnly = value;
            ApplyState();
        }
    }

    /// <summary>宿主在插件注册/窗口尺寸变化后调用：重建按钮内容并按当前形态刷新。</summary>
    public void SetButtons(IReadOnlyList<ToolbarButtonVm> buttons)
    {
        _buttonsVm = buttons;
        RebuildButtons();
        ApplyState();
    }

    /// <summary>主题切换后调用：图标颜色在构建时固化，重建以取新主题前景色。</summary>
    public void RefreshIcons()
    {
        RebuildButtons();
        ApplyState();
    }

    private void RebuildButtons()
    {
        _full.Children.Clear();
        _iconsOnly.Children.Clear();
        _collapsedHost.Children.Clear();
        _popupItems.Children.Clear();

        var index = 0;
        foreach (var vm in _buttonsVm)
        {
            _full.Children.Add(CreateButton(vm, withLabel: true));
            _iconsOnly.Children.Add(CreateButton(vm, withLabel: false));
            if (index == 0)
            {
                // 收起形态：首按钮直接可见（点击执行命令）
                _collapsedHost.Children.Add(CreateButton(vm, withLabel: false));
            }
            else
            {
                // 其余按钮进下拉菜单
                _popupItems.Children.Add(CreateMenuItem(vm));
            }
            index++;
        }

        // 收起形态的下拉箭头（组内多于一个按钮时才出现）
        if (_buttonsVm.Count > 1)
        {
            var arrow = new Button
            {
                Content = "▾",
                FontSize = 11,
                Width = 22,
                Padding = new Thickness(0),
                Height = 28,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                ToolTip = $"{Title}（更多）"
            };
            System.Windows.Automation.AutomationProperties.SetName(arrow, $"{Title}（更多）");
            arrow.Click += (_, _) =>
            {
                _popup.PlacementTarget = arrow;
                _popup.IsOpen = !_popup.IsOpen;
            };
            _collapsedHost.Children.Add(arrow);
        }
    }

    private void ApplyState()
    {
        var collapsed = _layout == ToolbarGroupLayout.Collapsed;
        _full.Visibility = !collapsed && !_useIconsOnly ? Visibility.Visible : Visibility.Collapsed;
        _iconsOnly.Visibility = !collapsed && _useIconsOnly ? Visibility.Visible : Visibility.Collapsed;
        _collapsedHost.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        if (!collapsed) _popup.IsOpen = false;
    }

    private Button CreateButton(ToolbarButtonVm vm, bool withLabel)
    {
        var button = new Button
        {
            Command = vm.Command,
            ToolTip = vm.Tooltip,
            Style = TryFindResource("ToolbarButton") as Style,
            Padding = withLabel ? new Thickness(8, 5, 10, 5) : new Thickness(8, 5, 8, 5),
            Margin = new Thickness(2, 0, 0, 0),
            Content = BuildContent(vm, withLabel)
        };
        // UIA 名称取 tooltip（插件只提供图标 + 提示 + 短文字）
        System.Windows.Automation.AutomationProperties.SetName(button, vm.Tooltip);
        return button;
    }

    /// <summary>按钮内容：线性图标（+ 可选短文字）。</summary>
    private object BuildContent(ToolbarButtonVm vm, bool withLabel)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Image
        {
            Source = ToolbarIconLibrary.Create(vm.Icon, ResolveForeground()),
            Width = ToolbarIconLibrary.Size,
            Height = ToolbarIconLibrary.Size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (withLabel && !string.IsNullOrWhiteSpace(vm.Label))
        {
            panel.Children.Add(new TextBlock
            {
                Text = vm.Label,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            });
        }
        return panel;
    }

    /// <summary>图标颜色跟随宿主前景色。</summary>
    private Color ResolveForeground()
    {
        if (TryFindResource("TextBrush") is SolidColorBrush brush) return brush.Color;
        return Colors.Black;
    }

    private Button CreateMenuItem(ToolbarButtonVm vm)
    {
        var button = new Button
        {
            Command = vm.Command,
            ToolTip = vm.Tooltip,
            Style = TryFindResource("ToolbarButton") as Style,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(2),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinWidth = 160,
            Content = BuildContent(vm, withLabel: true)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, vm.Tooltip);
        return button;
    }

    /// <summary>组内所有展开态按钮（供宿主放进「更多」面板）。</summary>
    public IEnumerable<Button> AllButtons => _full.Children.OfType<Button>();

    /// <summary>该组在各档位下的宽度（供宿主做溢出规划）。用 DesiredSize 更可靠：
    /// 切换 Visibility 后 ActualWidth 可能仍是上一档的旧值。</summary>
    public ToolbarOverflowPlanner.GroupWidths MeasureWidths()
    {
        var previous = _useIconsOnly;

        _useIconsOnly = false;
        ApplyState();
        UpdateLayout();
        var full = DesiredWidth(_full);

        _useIconsOnly = true;
        ApplyState();
        UpdateLayout();
        var icons = DesiredWidth(_iconsOnly);

        _useIconsOnly = previous;
        ApplyState();
        UpdateLayout();

        return new ToolbarOverflowPlanner.GroupWidths(full, icons, CollapsedWidth, _buttonsVm.Count);
    }

    /// <summary>取容器期望宽度（优先 DesiredSize，回退 ActualWidth）。</summary>
    private static double DesiredWidth(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = element.DesiredSize.Width;
        if (width <= 0) width = element.ActualWidth;
        return width;
    }

    /// <summary>收起形态的宽度（首按钮 + 下拉箭头）；未测量时按经验值估算。</summary>
    public double CollapsedWidth
    {
        get
        {
            _collapsedHost.UpdateLayout();
            var width = _collapsedHost.ActualWidth;
            return width > 0 ? width : 48;
        }
    }
}
