using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using KBEA.App.Branding;
using KBEA.App.Services;
using KBEA.Core.Models;
using KBEA.Core.Plugins;
using KBEA.Core.Services;
using KBEA.Core.Versioning;
using Microsoft.Win32;

namespace KBEA.App.Views;

public partial class SettingsWindow : Window
{
    private static readonly HttpClient _httpClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "rsiKB-settings-window" } }
    };

    private readonly AppConfiguration _configuration;
    private readonly Dictionary<string, FrameworkElement> _panels = new();
    private readonly Action? _onPluginSave;
    private TextBlock? _updateStatusText;

    public ObservableCollection<LlmEndpointConfig> LlmEndpoints { get; } = new();

    /// <summary>当前版本号（来自程序集，唯一真相源在 Directory.Build.props）。</summary>
    private static string CurrentVersion => AppVersion.Version;

    /// <param name="pluginPages">P4 插件设置页（经 PluginHostService 收集，UI 线程构建）。</param>
    /// <param name="onPluginSave">宿主「保存设置」时回调插件保存（SettingsWindow 不感知具体插件）。</param>
    public SettingsWindow(AppConfiguration configuration,
        IReadOnlyList<KBEA.Core.Plugins.SettingsPageVm>? pluginPages = null, Action? onPluginSave = null)
    {
        InitializeComponent();

        _configuration = configuration;
        _onPluginSave = onPluginSave;

        // 深拷贝，编辑不直接影响原配置
        var json = JsonSerializer.Serialize(configuration);
        var clone = JsonSerializer.Deserialize<AppConfiguration>(json) ?? configuration;

        // 首次运行时初始化 MachineId
        if (string.IsNullOrWhiteSpace(clone.MachineId))
            clone.MachineId = Guid.NewGuid().ToString();
        if (string.IsNullOrWhiteSpace(clone.MachineName))
            clone.MachineName = Environment.UserName;

        DataContext = clone;

        // 把 LlmEndpoints 同步到可观察集合供 DataGrid 使用
        foreach (var ep in clone.LlmEndpoints)
            LlmEndpoints.Add(ep);

        // 构建所有分类面板
        _panels["profile"] = BuildProfilePanel();
        _panels["git"] = BuildGitPanel();
        _panels["appearance"] = BuildAppearancePanel();
        // 「模型设置」暂时隐藏：BuildModelPanel 保留，恢复时在此重新登记
        _panels["organization"] = BuildOrganizationPanel(); // 已并入原「共享设置」内容
        _panels["plugins"] = BuildPluginsPanel();
        _panels["about"] = BuildAboutPanel();

        // P4：插件设置页追加到左列表（「插件：<Title>」），内容由插件构建的元素承载
        if (pluginPages is not null)
        {
            for (var i = 0; i < pluginPages.Count; i++)
            {
                var page = pluginPages[i];
                var tag = $"plugin:{i}";
                var panel = page.Content as FrameworkElement
                    ?? new TextBlock { Text = page.Content?.ToString() ?? string.Empty, TextWrapping = TextWrapping.Wrap };
                _panels[tag] = panel;
                CategoryList.Items.Add(new ListBoxItem { Content = $"插件：{page.Title}", Tag = tag });
            }
        }

        // 默认选中第一个分类
        CategoryList.SelectedIndex = 0;
    }

    // ==================== 分类切换 ====================

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is ListBoxItem item && item.Tag is string tag)
        {
            if (_panels.TryGetValue(tag, out var panel))
                ContentHost.Content = panel;
        }
    }

    // ==================== 面板构建 ====================

    private FrameworkElement BuildProfilePanel()
    {
        var dc = (AppConfiguration)DataContext;
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "个人信息",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "这台机器的唯一标识和显示名称，仅用于内部区分不同设备。",
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        panel.Children.Add(MakeRow("机器 ID (只读)",
            new TextBox
            {
                Text = dc.MachineId,
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 0, 10)
            }));

        var nameBox = new TextBox();
        nameBox.SetBinding(TextBox.TextProperty,
            new System.Windows.Data.Binding(nameof(AppConfiguration.MachineName))
            {
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
                Source = dc
            });
        panel.Children.Add(MakeRow("显示名称", nameBox));

        return panel;
    }

    private FrameworkElement BuildGitPanel()
    {
        var dc = (AppConfiguration)DataContext;
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "Git 与凭证",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "署名与邮箱用于 Git 提交记录；下方可视化当前机器的 credential.helper 配置与已保存的 Git 凭证（凭证密码不读取、不展示，仅支持删除）。",
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var authorBox = new TextBox();
        authorBox.SetBinding(TextBox.TextProperty,
            new System.Windows.Data.Binding(nameof(AppConfiguration.AuthorName))
            {
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
                Source = dc
            });
        panel.Children.Add(MakeRow("署名", authorBox));

        var emailBox = new TextBox();
        emailBox.SetBinding(TextBox.TextProperty,
            new System.Windows.Data.Binding(nameof(AppConfiguration.AuthorEmail))
            {
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
                Source = dc
            });
        panel.Children.Add(MakeRow("邮箱", emailBox));

        // ===== 凭证管理（credential.helper 可视化 + 凭证分组管理） =====
        var credSection = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        BuildCredentialSection(credSection, dc);
        panel.Children.Add(credSection);

        return panel;
    }

    /// <summary>
    /// 凭证管理区：先展示 credential.helper 配置；已保存凭证分两组——
    /// 优先展示「工作空间远程仓库」涉及的凭证（标明来源空间），其余收在「其他凭证」折叠区。
    /// </summary>
    private void BuildCredentialSection(StackPanel host, AppConfiguration dc)
    {
        host.Children.Clear();

        TextBlock Hint(string text) => new()
        {
            Text = text,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11
        };

        // ---- credential.helper 配置可视化 ----
        host.Children.Add(new TextBlock
        {
            Text = "credential.helper 配置",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 6)
        });

        var helpers = GitCredentialService.ReadCredentialHelpers();
        if (!GitCredentialService.GitCliAvailable)
            host.Children.Add(Hint("未检测到 git 命令行，无法读取 credential.helper 配置（下方凭证列表仍来自 Windows 凭据管理器）。"));
        else if (helpers.Count == 0)
            host.Children.Add(Hint("未配置 credential.helper；已保存凭证可能由其他工具管理。"));
        else
            foreach (var helper in helpers)
                host.Children.Add(new TextBlock
                {
                    Text = $"[{helper.Scope}] {helper.Helper}    （{helper.Origin}）",
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
                    Margin = new Thickness(0, 0, 0, 2)
                });

        // ---- 已保存凭证：按「工作空间相关 / 其他」分组 ----
        var entries = GitCredentialService.ListGitCredentials();
        var hostToSpaces = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var space in dc.Spaces.Where(s => !string.IsNullOrWhiteSpace(s.RemoteUrl)))
        {
            var remoteHost = GitCredentialService.ExtractRemoteHost(space.RemoteUrl);
            if (remoteHost is null) continue;
            if (!hostToSpaces.TryGetValue(remoteHost, out var names)) hostToSpaces[remoteHost] = names = [];
            names.Add(space.Name);
        }

        var related = entries.Where(e => hostToSpaces.ContainsKey(e.Host)).ToList();
        var others = entries.Where(e => !hostToSpaces.ContainsKey(e.Host)).ToList();

        // 列表标题行 + 刷新
        var listHeader = new Grid { Margin = new Thickness(0, 14, 0, 4) };
        listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerText = new TextBlock
        {
            Text = $"已保存的 Git 凭证（{entries.Count} 条）",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(headerText, 0);
        var refreshBtn = new Button
        {
            Content = "刷新",
            Padding = new Thickness(12, 2, 12, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        refreshBtn.Click += (_, _) => BuildCredentialSection(host, dc);
        Grid.SetColumn(refreshBtn, 1);
        listHeader.Children.Add(headerText);
        listHeader.Children.Add(refreshBtn);
        host.Children.Add(listHeader);

        // 第一组（优先）：工作空间远程仓库涉及的凭证
        host.Children.Add(new TextBlock
        {
            Text = "工作空间相关凭证",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 4)
        });
        if (related.Count == 0)
            host.Children.Add(Hint("未找到与工作空间远程仓库关联的凭证；绑定远程仓库并完成登录后，对应凭证会出现在这里。"));
        else
            foreach (var entry in related)
                host.Children.Add(MakeCredentialRow(entry, string.Join("、", hostToSpaces[entry.Host]), host, dc));

        // 第二组：其他凭证（折叠展示）
        var expander = new Expander
        {
            Header = $"其他凭证（{others.Count} 条）",
            IsExpanded = false,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var othersPanel = new StackPanel();
        if (others.Count == 0)
            othersPanel.Children.Add(Hint("没有其他已保存的 Git 凭证。"));
        else
            foreach (var entry in others)
                othersPanel.Children.Add(MakeCredentialRow(entry, null, host, dc));
        expander.Content = othersPanel;
        host.Children.Add(expander);

        var tip = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 12, 0, 0)
        };
        tip.Child = new TextBlock
        {
            Text = "提示：凭证保存在 Windows 凭据管理器（Git Credential Manager / wincred 写入 git:* 条目）。删除凭证不影响仓库数据，Git 下次访问对应仓库时会重新要求登录。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush")
        };
        host.Children.Add(tip);
    }

    /// <summary>凭证条目行：地址 + 用户名 + 来源空间 + 最后写入时间 + 删除按钮。</summary>
    private FrameworkElement MakeCredentialRow(GitCredentialEntry entry, string? source, StackPanel sectionHost, AppConfiguration dc)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.UserName)
                ? entry.Url
                : $"{entry.Url}  ·  {entry.UserName}",
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        info.Children.Add(new TextBlock
        {
            Text = source is null
                ? $"最后写入 {entry.LastWritten:yyyy-MM-dd}"
                : $"{source} · 最后写入 {entry.LastWritten:yyyy-MM-dd}",
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            Margin = new Thickness(0, 1, 0, 0)
        });
        Grid.SetColumn(info, 0);

        var deleteBtn = new Button
        {
            Content = "删除",
            Padding = new Thickness(12, 2, 12, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        deleteBtn.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    $"确定删除凭证 {entry.Url} 吗？\n删除后 Git 下次访问该仓库会重新要求登录。",
                    "删除凭证", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (!GitCredentialService.DeleteCredential(entry.Target))
            {
                MessageBox.Show(this, "删除失败（可能被系统策略保护或需要相应权限）。",
                    "删除凭证", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            BuildCredentialSection(sectionHost, dc);
        };
        Grid.SetColumn(deleteBtn, 1);

        row.Children.Add(info);
        row.Children.Add(deleteBtn);
        return row;
    }

    private FrameworkElement BuildAppearancePanel()
    {
        var dc = (AppConfiguration)DataContext;
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "外观与行为",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "在这里切换主题和调整一些自动行为。",
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            Margin = new Thickness(0, 0, 0, 14)
        });

        var themeBox = new ComboBox
        {
            ItemsSource = new[] { "Light", "Dark" },
            SelectedItem = dc.Theme,
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        themeBox.SetBinding(ComboBox.SelectedItemProperty,
            new System.Windows.Data.Binding(nameof(AppConfiguration.Theme))
            {
                Source = dc,
                Mode = System.Windows.Data.BindingMode.TwoWay
            });
        panel.Children.Add(MakeRow("主题", themeBox));

        var autoCheck = new CheckBox
        {
            Content = "监听文件变化并提示（默认不自动上传）",
            IsChecked = dc.AutoDetect,
            Margin = new Thickness(0, 10, 0, 0)
        };
        autoCheck.SetBinding(CheckBox.IsCheckedProperty,
            new System.Windows.Data.Binding(nameof(AppConfiguration.AutoDetect))
            {
                Source = dc,
                Mode = System.Windows.Data.BindingMode.TwoWay
            });
        panel.Children.Add(autoCheck);

        return panel;
    }

    /// <summary>模型设置（暂时隐藏：未登记到分类列表，功能恢复时在构造函数重新登记）。</summary>
    private FrameworkElement BuildModelPanel()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "模型设置",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "添加多个 LLM 端点以供插件和智能助手使用。Token 会以明文保存在本地配置文件中。",
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        // DataGrid
        var grid = new DataGrid
        {
            ItemsSource = LlmEndpoints,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            AutoGenerateColumns = false,
            IsReadOnly = false,
            Height = 220
        };

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "名称",
            Binding = new System.Windows.Data.Binding(nameof(LlmEndpointConfig.Name)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "端点 URL",
            Binding = new System.Windows.Data.Binding(nameof(LlmEndpointConfig.Endpoint)),
            Width = new DataGridLength(1.5, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "API Token",
            Binding = new System.Windows.Data.Binding(nameof(LlmEndpointConfig.Token)),
            Width = new DataGridLength(1.2, DataGridLengthUnitType.Star)
        });

        panel.Children.Add(grid);

        // 按钮区
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        var addBtn = new Button { Content = "＋ 添加端点", Margin = new Thickness(0, 0, 8, 0) };
        addBtn.Click += (_, _) =>
        {
            LlmEndpoints.Add(new LlmEndpointConfig
            {
                Name = $"端点 {LlmEndpoints.Count + 1}",
                Endpoint = "https://api.example.com/v1"
            });
        };

        var removeBtn = new Button { Content = "－ 移除选中" };
        removeBtn.Click += (_, _) =>
        {
            if (grid.SelectedItem is LlmEndpointConfig ep)
                LlmEndpoints.Remove(ep);
        };

        btnPanel.Children.Add(addBtn);
        btnPanel.Children.Add(removeBtn);
        panel.Children.Add(btnPanel);

        return panel;
    }

    /// <summary>
    /// P5 插件管理页：列出已发现的插件（名称/版本/能力/加载状态）+ 启用开关。
    /// 开关变更在「保存设置」时写回各自 plugin.json 的 enabled 字段；禁用/启用需重启后生效
    /// （插件 ALC 不可热卸载，见 docs/plugin-architecture.md §6）。
    /// </summary>
    private FrameworkElement BuildPluginsPanel()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "插件管理",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "在此启用或禁用随程序分发的插件。保存设置后写入各插件目录的 plugin.json，重启应用后生效。",
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var statuses = PluginBridge.GetPluginStatuses();
        if (statuses.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "未发现插件。插件位于程序目录下的 plugins/ 子目录中。",
                Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush")
            });
            return panel;
        }

        foreach (var status in statuses)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = $"{status.Name}  v{status.Version}",
                FontWeight = FontWeights.SemiBold
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{status.Id} · 能力：{status.Capabilities} · {status.Status}",
                Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
            info.Children.Add(new TextBlock
            {
                Text = $"启用来源：{status.EnableSourceText}",
                Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(info, 0);

            // 恢复发行方默认：清除用户显式选择，回到策略/清单自带的默认值
            var toggle = new CheckBox
            {
                Content = "启用",
                IsChecked = status.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = status
            };
            // 变更记录到 manifest（保存时统一写回 plugin.json）
            toggle.Checked += (_, _) => { status.Manifest.Enabled = true; status.Manifest.UserModified = true; };
            toggle.Unchecked += (_, _) => { status.Manifest.Enabled = false; status.Manifest.UserModified = true; };

            var reset = new Button
            {
                Content = "恢复默认",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 2, 8, 2),
                ToolTip = "清除本机对该插件的显式设置，回到发行方策略或插件自带的默认值",
                Tag = status
            };
            reset.Click += (_, _) => ResetPluginToDefault(status, toggle);
            Grid.SetColumn(reset, 1);
            Grid.SetColumn(toggle, 2);

            row.Children.Add(info);
            row.Children.Add(reset);
            row.Children.Add(toggle);
            panel.Children.Add(row);
        }

        var tip = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 12, 0, 0)
        };
        tip.Child = new TextBlock
        {
            Text = "提示：插件的启用状态保存在其 plugin.json 中。禁用后重启应用不再加载该插件（工具栏按钮、扩展点一并消失）。发行方可在程序目录的 plugins.defaults.json 中预设默认启用的插件；用户在此处的显式选择优先于该预设，可用「恢复默认」清除。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush")
        };
        panel.Children.Add(tip);

        return panel;
    }

    /// <summary>「恢复默认」：清除用户对该插件的显式设置，回到发行方策略或插件自带的默认值。</summary>
    private void ResetPluginToDefault(PluginStatusVm status, CheckBox toggle)
    {
        try
        {
            KBEA.Core.Plugins.PluginCatalog.ResetToDefault(status.Manifest);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"恢复默认失败：{ex.Message}", "插件管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 勾选框同步到策略给出的状态
        toggle.IsChecked = status.Enabled;
    }

    /// <summary>数字输入行：TextBox 绑定数值配置项（WPF 默认转换器，非法输入保持原值）。</summary>
    private FrameworkElement MakeNumberRow(string label, string propertyName, AppConfiguration dc, string hint)
    {
        var box = new TextBox();
        box.SetBinding(TextBox.TextProperty,
            new System.Windows.Data.Binding(propertyName)
            {
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
                Source = dc
            });
        box.Height = 32;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.Width = 140;

        var row = new StackPanel();
        row.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        box.Margin = new Thickness(0, 6, 0, 2);
        row.Children.Add(box);
        row.Children.Add(new TextBlock
        {
            Text = hint,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 12)
        });
        return row;
    }

    private FrameworkElement BuildAboutPanel()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "关于我们",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        });

        // ===== 主卡片：rsiKB =====
        var updateBtn = new Button
        {
            Content = "检查更新",
            Style = (Style)FindResource("PrimaryButton"),
            Width = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0)
        };
        updateBtn.Click += async (_, _) => await CheckForUpdatesAsync();

        var footer = new StackPanel();
        footer.Children.Add(updateBtn);
        _updateStatusText = new TextBlock
        {
            Text = GetUpdateStatusSummary(),
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = string.IsNullOrEmpty(GetUpdateStatusSummary()) ? Visibility.Collapsed : Visibility.Visible
        };
        footer.Children.Add(_updateStatusText);

        panel.Children.Add(MakeAboutCard(
            BrandingLoader.Current.WindowTitle,
            AppVersion.Display,
            string.IsNullOrWhiteSpace(BrandingLoader.Current.Tagline)
                ? "一个以 Git / rsync 为底座的知识工作台，把本地目录、远程仓库和归档节点串成一张可追溯、可对比的知识图谱。开源核心可独立使用，插件接入后提供智能助手与报表能力。"
                : BrandingLoader.Current.Tagline,
            "https://github.com/7zthinking/rsiKB",
            footer));

        // ===== 开源依赖卡片 =====
        panel.Children.Add(new TextBlock
        {
            Text = "开源依赖",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 8)
        });
        foreach (var dep in BuildDependencyCards())
            panel.Children.Add(MakeAboutCard(dep.Title, dep.Version, dep.Description, dep.LinkUrl));

        // ===== 插件提供的卡片 =====
        var pluginCards = PluginBridge.GetAboutCards();
        if (pluginCards.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "插件提供",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 8)
            });
            foreach (var card in pluginCards)
                panel.Children.Add(MakeAboutCard(card.Title, card.Version, card.Description, card.LinkUrl));
        }

        return panel;
    }

    /// <summary>构建一张「关于我们」卡片：标题（有链接时可点击）+ 版本 + 说明 + 可选底部内容。</summary>
    private FrameworkElement MakeAboutCard(string title, string? version, string description, string? linkUrl, FrameworkElement? footer = null)
    {
        var content = new StackPanel();

        // 标题行：标题（左）+ 版本（右）
        var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (!string.IsNullOrEmpty(linkUrl))
        {
            var linkBlock = new TextBlock();
            var hyperlink = new Hyperlink { NavigateUri = new Uri(linkUrl) };
            hyperlink.Inlines.Add(new Run(title) { FontWeight = FontWeights.SemiBold });
            hyperlink.RequestNavigate += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch { /* 忽略 */ }
                e.Handled = true;
            };
            linkBlock.Inlines.Add(hyperlink);
            Grid.SetColumn(linkBlock, 0);
            titleRow.Children.Add(linkBlock);
        }
        else
        {
            var titleBlock = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(titleBlock, 0);
            titleRow.Children.Add(titleBlock);
        }

        if (!string.IsNullOrEmpty(version))
        {
            var verBlock = new TextBlock
            {
                Text = version.StartsWith('v') ? version : $"版本 {version}",
                Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(verBlock, 1);
            titleRow.Children.Add(verBlock);
        }
        content.Children.Add(titleRow);

        content.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11
        });

        if (footer is not null) content.Children.Add(footer);

        return new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("CardBackground"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = content
        };
    }

    /// <summary>核心开源依赖清单（与 THIRD-PARTY-NOTICES.md 对应，运行时依赖）。</summary>
    private static IReadOnlyList<AboutCardInfo> BuildDependencyCards() =>
    [
        new()
        {
            Title = ".NET 8 / WPF",
            Version = "8.0",
            Description = "应用运行时与 UI 框架，绿色版自包含分发。",
            LinkUrl = "https://github.com/dotnet/runtime"
        },
        new()
        {
            Title = "CommunityToolkit.Mvvm",
            Version = "8.4.0",
            Description = "MVVM 框架：可观察属性、命令与消息机制。",
            LinkUrl = "https://github.com/CommunityToolkit/dotnet"
        },
        new()
        {
            Title = "Microsoft.Data.Sqlite",
            Version = "8.0.11",
            Description = "本地元数据与任务存储（SQLite）。",
            LinkUrl = "https://learn.microsoft.com/dotnet/standard/data/sqlite"
        },
        new()
        {
            Title = "DiffPlex",
            Version = "1.7.2",
            Description = "文本差异对比（版本历史与冲突比较）。",
            LinkUrl = "https://github.com/mmanela/diffplex"
        },
        new()
        {
            Title = "LibGit2Sharp",
            Version = "0.31.0",
            Description = "Git 操作核心（保存推送、拉取、历史），随 Git 插件分发。",
            LinkUrl = "https://github.com/libgit2/libgit2sharp"
        },
        new()
        {
            Title = "Makaretu.Dns.Multicast",
            Version = "0.27.0",
            Description = "mDNS 组播发现，用于团队/组织设备发现。",
            LinkUrl = "https://github.com/richardschneider/net-mdns"
        },
        new()
        {
            Title = "YamlDotNet",
            Version = "13.7.1",
            Description = "branding 固化配置（logo/标题/主题色）解析。",
            LinkUrl = "https://github.com/aaubry/YamlDotNet"
        },
        new()
        {
            Title = "Microsoft.Extensions.DependencyInjection",
            Version = "8.0.1",
            Description = "宿主服务依赖注入容器。",
            LinkUrl = "https://github.com/dotnet/runtime"
        }
    ];

    private FrameworkElement BuildOrganizationPanel()
    {
        var dc = (AppConfiguration)DataContext;
        var service = OrganizationService.Instance;
        service.AttachConfiguration(dc);
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "组织与共享",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "组织是局域网中共享工作空间的一组 KBEA 实例（发现协议基于 UDP 广播，端口 4719），当前为预留 stub。下方同时包含团队共享的本地参数，修改后重启应用生效。",
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        // 本机 MachineId 只读展示
        panel.Children.Add(MakeRow("本机 MachineId (只读)",
            new TextBox
            {
                Text = dc.MachineId,
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 0, 10)
            }));

        // 操作按钮行
        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 12)
        };

        // itemsControl 提前定义，按钮回调需要引用它
        var itemsControl = new ItemsControl();

        var newOrgBtn = new Button { Content = "＋ 新建组织", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 10, 0) };
        newOrgBtn.Click += (_, _) =>
        {
            var dialog = new TextBox
            {
                Height = 32,
                Width = 260,
                Margin = new Thickness(0, 6, 0, 10)
            };
            var win = new Window
            {
                Title = "新建组织",
                Width = 340,
                Height = 140,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Children =
                    {
                        new TextBlock { Text = "组织名称", FontWeight = FontWeights.SemiBold },
                        dialog,
                        new Button
                        {
                            Content = "创建",
                            Width = 100,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Padding = new Thickness(14, 6, 14, 6),
                            IsDefault = true
                        }
                    }
                }
            };
            dialog.Focus();
            ((Button)((StackPanel)win.Content).Children[2]).Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(dialog.Text))
                {
                    service.CreateOrganization(dialog.Text.Trim());
                    win.DialogResult = true;
                    win.Close();
                    RebindOrganizationList(itemsControl, dc, service);
                }
            };
            win.ShowDialog();
        };

        var joinOrgBtn = new Button { Content = "＋ 按 ID 加入组织", Padding = new Thickness(14, 6, 14, 6) };
        joinOrgBtn.Click += (_, _) =>
        {
            var dialog = new TextBox
            {
                Height = 32,
                Width = 260,
                Margin = new Thickness(0, 6, 0, 10)
            };
            var win = new Window
            {
                Title = "加入组织",
                Width = 340,
                Height = 140,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Children =
                    {
                        new TextBlock { Text = "组织名称或 ID", FontWeight = FontWeights.SemiBold },
                        dialog,
                        new Button
                        {
                            Content = "加入",
                            Width = 100,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Padding = new Thickness(14, 6, 14, 6),
                            IsDefault = true
                        }
                    }
                }
            };
            dialog.Focus();
            ((Button)((StackPanel)win.Content).Children[2]).Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(dialog.Text))
                {
                    var joined = service.JoinOrganization(dialog.Text.Trim());
                    if (joined is null)
                        MessageBox.Show(this, "未找到该组织。", "加入组织", MessageBoxButton.OK, MessageBoxImage.Warning);
                    win.DialogResult = true;
                    win.Close();
                    RebindOrganizationList(itemsControl, dc, service);
                }
            };
            win.ShowDialog();
        };

        actionRow.Children.Add(newOrgBtn);
        actionRow.Children.Add(joinOrgBtn);
        panel.Children.Add(actionRow);

        // 组织列表标题
        panel.Children.Add(new TextBlock
        {
            Text = "我所在的组织",
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            Margin = new Thickness(0, 6, 0, 6)
        });

        RebindOrganizationList(itemsControl, dc, service);
        panel.Children.Add(itemsControl);

        // ===== 共享参数（原「共享设置」页并入本页） =====
        panel.Children.Add(new TextBlock
        {
            Text = "共享参数",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 8)
        });

        panel.Children.Add(MakeNumberRow("内容缓存限额 (GB)",
            nameof(AppConfiguration.TeamCacheLimitGb), dc, "取回过的团队文件按 LRU 缓存在本地，超出限额自动淘汰。0.1–100。"));

        panel.Children.Add(MakeNumberRow("同步间隔 (分钟)",
            nameof(AppConfiguration.TeamSyncIntervalMinutes), dc,
            "定时拉取团队成员元数据的周期；成员上线和手动刷新也会触发。1–120。"));

        panel.Children.Add(MakeNumberRow("服务端口",
            nameof(AppConfiguration.TeamServicePort), dc,
            "文件服务起始端口（默认 52235），被占用时自动向后尝试 6 个。防火墙放行规则会按此端口添加。1–65535。"));

        // 共享开关说明区（预留，暂仅配置占位）
        var shareTip = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 12, 0, 0)
        };
        shareTip.Child = new TextBlock
        {
            Text = "提示：昵称在「团队管理」窗口修改；防火墙放行引导在「新建团队」时弹出，之后也可在团队管理里重试。空间级共享开关在主界面的「工作空间」标签中提供。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush")
        };
        panel.Children.Add(shareTip);

        return panel;
    }

    private void RebindOrganizationList(ItemsControl host, AppConfiguration dc, OrganizationService service)
    {
        host.Items.Clear();
        var myOrgs = service.GetMyOrganizations();
        foreach (var org in myOrgs)
        {
            var row = new Grid
            {
                Margin = new Thickness(0, 4, 0, 4)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = $"{org.Name}  ·  {org.MemberIds.Count} 名成员",
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameBlock, 0);

            var leaveBtn = new Button
            {
                Content = "退出",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(8, 0, 0, 0),
                Tag = org
            };
            leaveBtn.Click += (_, _) =>
            {
                service.LeaveOrganization(org.Id);
                RebindOrganizationList(host, dc, service);
            };
            Grid.SetColumn(leaveBtn, 1);

            row.Children.Add(nameBlock);
            row.Children.Add(leaveBtn);
            host.Items.Add(row);
        }
        if (myOrgs.Count == 0)
        {
            host.Items.Add(new TextBlock
            {
                Text = "当前未加入任何组织。",
                Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
                Margin = new Thickness(0, 4, 0, 4)
            });
        }
    }

    // ==================== UI 构建辅助 ====================

    private static StackPanel MakeRow(string label, Control input)
    {
        var row = new StackPanel();
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold
        });
        input.Height = 32;
        input.Margin = new Thickness(0, 6, 0, 10);
        row.Children.Add(input);
        return row;
    }

    private static StackPanel MakeBrowseRow(string label, TextBox textBox, RoutedEventHandler browseHandler)
    {
        var row = new StackPanel();

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold
        });

        var inputRow = new Grid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        textBox.Height = 32;
        textBox.Margin = new Thickness(0, 6, 8, 10);
        Grid.SetColumn(textBox, 0);

        var btn = new Button
        {
            Content = "浏览",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 6, 0, 10)
        };
        btn.Click += browseHandler;
        Grid.SetColumn(btn, 1);

        inputRow.Children.Add(textBox);
        inputRow.Children.Add(btn);
        row.Children.Add(inputRow);

        return row;
    }

    // ==================== Check for Updates ====================

    /// <summary>
    /// 检查更新主流程（状态持久化在 %LOCALAPPDATA%\KBEA\Update\state.json）：
    /// 已下载 → 提示启动更新；下载中断 → 提示断点续传；否则远程检查 → 询问下载
    /// → 下载窗口（可暂停，断点保留）→ 下载完成询问启动更新。
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            Cursor = Cursors.Wait;
            Mouse.OverrideCursor = Cursors.Wait;

            // 1) 本地已下载完成（或更新脚本已启动但应用未退出）→ 直接进入启动更新
            var state = UpdateService.LoadState();
            if (state is { Phase: UpdatePhase.Downloaded or UpdatePhase.Updating }
                && !string.IsNullOrEmpty(state.ZipPath)
                && VersionComparer.Compare(CurrentVersion, state.TargetVersion) < 0)
            {
                if (MessageBox.Show(this,
                        $"新版本 {state.TargetVersion} 已下载完成。是否立即启动更新？\n\n启动后将自动关闭应用、应用更新并重新打开。",
                        "启动更新", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    LaunchUpdateNow(state.ZipPath, state);
                return;
            }

            // 2) 有未完成的下载 → 提示断点续传
            if (state is { Phase: UpdatePhase.Paused }
                && !string.IsNullOrEmpty(state.AssetUrl) && !string.IsNullOrEmpty(state.ZipPath)
                && File.Exists(state.ZipPath)
                && VersionComparer.Compare(CurrentVersion, state.TargetVersion) < 0)
            {
                var percent = state.TotalBytes > 0
                    ? $"（已完成 {state.DownloadedBytes * 100 / state.TotalBytes:0}%）" : "";
                if (MessageBox.Show(this,
                        $"上次下载 {state.TargetVersion} 中断{percent}。是否继续下载？",
                        "继续下载", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    await DownloadUpdateAsync(state);
                return;
            }

            // 3) 远程检查：用 Releases 列表接口（/releases/latest 会跳过预发布版）
            var response = await _httpClient.GetAsync(UpdateChecker.ReleasesApiUrl);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = UpdateChecker.Evaluate(json, CurrentVersion);

            if (result.Status is UpdateStatus.Failed or UpdateStatus.UpToDate or UpdateStatus.Ahead)
            {
                // 远程没有可下的更新 → 清掉残留的待下载/中断状态
                if (result.Status is not UpdateStatus.Failed) UpdateService.ClearState();
                RefreshUpdateStatus();
                MessageBox.Show(this, result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // NewVersion / NewPrerelease
            if (string.IsNullOrEmpty(result.AssetUrl))
            {
                var open = MessageBox.Show(this,
                    $"{result.Message}\n\n未找到自动更新安装包，是否打开 GitHub 发布页手动下载？",
                    "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes && result.ReleaseUrl is not null)
                    Process.Start(new ProcessStartInfo(result.ReleaseUrl) { UseShellExecute = true });
                return;
            }

            if (MessageBox.Show(this,
                    $"{result.Message}\n\n是否现在下载更新包？",
                    "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var newState = new UpdateState
            {
                Phase = UpdatePhase.Available,
                TargetVersion = result.LatestVersion,
                IsPrerelease = result.IsPrerelease,
                AssetUrl = result.AssetUrl,
                ReleaseUrl = result.ReleaseUrl,
                ZipPath = UpdateService.GetZipPath(result.LatestVersion!)
            };
            UpdateService.SaveState(newState);
            await DownloadUpdateAsync(newState);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"检查更新失败：{ex.Message}", "检查更新", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = Cursors.Arrow;
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>打开下载窗口（断点续传）；结束后按结果持久化状态并询问是否启动更新。</summary>
    private async Task DownloadUpdateAsync(UpdateState state)
    {
        var resumeFrom = state.Phase == UpdatePhase.Paused && File.Exists(state.ZipPath!)
            ? new FileInfo(state.ZipPath!).Length : 0;

        var dialog = new UpdateDownloadWindow(state.TargetVersion ?? "新版本", state.AssetUrl!, state.ZipPath!, resumeFrom)
        {
            Owner = this
        };
        dialog.ShowDialog();

        if (dialog.Completed)
        {
            state.Phase = UpdatePhase.Downloaded;
            state.DownloadedBytes = state.TotalBytes = new FileInfo(state.ZipPath!).Length;
            UpdateService.SaveState(state);
            RefreshUpdateStatus();

            if (MessageBox.Show(this,
                    $"新版本 {state.TargetVersion} 下载完成。是否立即启动更新？\n\n启动后将自动关闭应用、应用更新并重新打开。",
                    "下载完成", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                LaunchUpdateNow(state.ZipPath!, state);
        }
        else
        {
            // 暂停/中断/失败：记录断点，下次「检查更新」时提示继续
            state.Phase = UpdatePhase.Paused;
            state.DownloadedBytes = File.Exists(state.ZipPath!) ? new FileInfo(state.ZipPath!).Length : 0;
            UpdateService.SaveState(state);
            RefreshUpdateStatus();
        }
        await Task.CompletedTask;
    }

    /// <summary>启动更新脚本并退出应用（脚本等待本进程退出后覆盖安装目录并重启）。</summary>
    private void LaunchUpdateNow(string zipPath, UpdateState state)
    {
        state.Phase = UpdatePhase.Updating;
        UpdateService.SaveState(state);
        if (!UpdateService.LaunchUpdater(zipPath))
        {
            state.Phase = UpdatePhase.Downloaded;
            UpdateService.SaveState(state);
            MessageBox.Show(this, "更新程序启动失败，请稍后重试或到 GitHub 发布页手动下载。",
                "启动更新", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        // 更新脚本已接管：等待本进程退出后覆盖安装目录并重新打开应用
        Application.Current.Shutdown();
    }

    /// <summary>持久化状态 → 「关于我们」面板上的摘要文字。</summary>
    private string GetUpdateStatusSummary()
    {
        var state = UpdateService.LoadState();
        if (state is null || string.IsNullOrEmpty(state.TargetVersion)) return string.Empty;
        if (VersionComparer.Compare(CurrentVersion, state.TargetVersion) >= 0) return string.Empty;
        return state.Phase switch
        {
            UpdatePhase.Downloaded or UpdatePhase.Updating => $"新版本 {state.TargetVersion} 已下载完成，点击「检查更新」启动更新。",
            UpdatePhase.Paused => $"新版本 {state.TargetVersion} 下载中断，点击「检查更新」可从断点继续。",
            UpdatePhase.Available => $"远程有新版本 {state.TargetVersion} 可下载。",
            _ => string.Empty
        };
    }

    private void RefreshUpdateStatus()
    {
        if (_updateStatusText is not null) _updateStatusText.Text = GetUpdateStatusSummary();
    }

    // ==================== Save & Browse Handlers ====================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var edited = (AppConfiguration)DataContext;
        _configuration.MachineId = edited.MachineId;
        _configuration.MachineName = edited.MachineName;
        _configuration.AuthorName = edited.AuthorName;
        _configuration.AuthorEmail = edited.AuthorEmail;
        _configuration.AutoDetect = edited.AutoDetect;
        _configuration.Theme = edited.Theme;
        _configuration.LlmEndpoints = [.. LlmEndpoints];
        _configuration.Organizations = edited.Organizations;
        _configuration.OrganizationShares = edited.OrganizationShares;
        _configuration.TeamCacheLimitGb = edited.TeamCacheLimitGb;
        _configuration.TeamSyncIntervalMinutes = edited.TeamSyncIntervalMinutes;
        _configuration.TeamServicePort = edited.TeamServicePort;
        // P5：插件启用状态写回各自 plugin.json（失败只提示，不阻断其他设置保存）
        var pluginErrors = SavePluginEnableStates();
        // P4：先回调插件保存（插件自行持久化），再关闭窗口
        _onPluginSave?.Invoke();
        if (pluginErrors.Count > 0)
            MessageBox.Show(this,
                "以下插件的启用状态未能写入（文件可能只读或已损坏）：\n" + string.Join("\n", pluginErrors),
                "插件管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        DialogResult = true;
    }

    /// <summary>
    /// 把插件启用开关写回各自 plugin.json；返回失败的插件说明。
    /// 只写「用户本次改动过」的插件：点过「恢复默认」的插件已在点击时落盘（enabledByUser=false），
    /// 这里不能再来一次 SetEnabled —— 那会把它重新标记成用户显式设置。
    /// </summary>
    private List<string> SavePluginEnableStates()
    {
        var errors = new List<string>();
        foreach (var status in PluginBridge.GetPluginStatuses())
        {
            // 用户本次没动过 → 保持发行方策略/清单默认值，不写回
            if (!status.Manifest.UserModified) continue;
            try { KBEA.Core.Plugins.PluginCatalog.SetEnabled(status.Manifest, status.Manifest.Enabled); }
            catch (Exception ex) { errors.Add($"{status.Name}：{ex.Message}"); }
        }
        return errors;
    }
}
