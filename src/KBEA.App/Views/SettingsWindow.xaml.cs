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
        _panels["model"] = BuildModelPanel();
        _panels["organization"] = BuildOrganizationPanel();
        _panels["sharing"] = BuildSharingPanel();
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
            Text = "提交记录会使用下面的署名和邮箱；rsync 路径和归档根目录用于远程同步。",
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

        return panel;
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

    private FrameworkElement BuildSharingPanel()
    {
        var dc = (AppConfiguration)DataContext;
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "共享设置",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "团队共享的本地参数：内容缓存限额、同步节奏和服务端口。修改后重启应用生效。",
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        panel.Children.Add(MakeNumberRow("内容缓存限额 (GB)",
            nameof(AppConfiguration.TeamCacheLimitGb), dc, "取回过的团队文件按 LRU 缓存在本地，超出限额自动淘汰。0.1–100。"));

        panel.Children.Add(MakeNumberRow("同步间隔 (分钟)",
            nameof(AppConfiguration.TeamSyncIntervalMinutes), dc,
            "定时拉取团队成员元数据的周期；成员上线和手动刷新也会触发。1–120。"));

        panel.Children.Add(MakeNumberRow("服务端口",
            nameof(AppConfiguration.TeamServicePort), dc,
            "文件服务起始端口（默认 52235），被占用时自动向后尝试 6 个。防火墙放行规则会按此端口添加。1–65535。"));

        var tip = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 12, 0, 0)
        };
        tip.Child = new TextBlock
        {
            Text = "提示：昵称在「团队管理」窗口修改；防火墙放行引导在「新建团队」时弹出，之后也可在团队管理里重试。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush")
        };
        panel.Children.Add(tip);

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

        panel.Children.Add(new TextBlock
        {
            Text = BrandingLoader.Current.WindowTitle,
            FontSize = 20,
            FontWeight = FontWeights.Bold
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"版本 {AppVersion.Display}",
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            Margin = new Thickness(0, 2, 0, 10)
        });

        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(BrandingLoader.Current.Tagline)
                ? "一个以 Git / rsync 为底座的知识工作台，把本地目录、远程仓库和归档节点串成一张可追溯、可对比的知识图谱。开源核心可独立使用，插件接入后提供智能助手与报表能力。"
                : BrandingLoader.Current.Tagline,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            Margin = new Thickness(0, 0, 0, 14)
        });

        // GitHub link
        var linkText = new TextBlock();
        var runPrefix = new Run("项目地址：");
        var link = new Hyperlink
        {
            NavigateUri = new Uri("https://github.com/7zthinking/rsiKB")
        };
        link.Inlines.Add(new Run("https://github.com/7zthinking/rsiKB"));
        link.RequestNavigate += (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                /* ignore */
            }
            e.Handled = true;
        };
        linkText.Inlines.Add(runPrefix);
        linkText.Inlines.Add(link);
        panel.Children.Add(linkText);

        // Check for Updates button
        var updateBtn = new Button
        {
            Content = "检查更新",
            Style = (Style)FindResource("PrimaryButton"),
            Width = 110,
            Margin = new Thickness(0, 12, 0, 0)
        };
        updateBtn.Click += async (_, _) => await CheckForUpdatesAsync();
        panel.Children.Add(updateBtn);

        return panel;
    }

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
            Text = "组织是局域网中共享工作空间的一组 KBEA 实例。发现协议基于 UDP 广播（端口 4719），当前为预留 stub。",
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

        // 共享开关说明区（预留，暂仅配置占位）
        var shareTip = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 16, 0, 0)
        };
        shareTip.Child = new TextBlock
        {
            Text = "空间级共享将在主界面的「工作空间」标签中提供开关；此处 OrganizationShares 列表仅作配置占位。",
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

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            Cursor = Cursors.Wait;
            Mouse.OverrideCursor = Cursors.Wait;

            // 用 Releases 列表接口（而不是 /releases/latest）：后者会跳过预发布版，
            // 测试版发布时用户永远看不到更新。
            var response = await _httpClient.GetAsync(UpdateChecker.ReleasesApiUrl);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = UpdateChecker.Evaluate(json, CurrentVersion);

            if (result.Status == UpdateStatus.Failed)
            {
                MessageBox.Show(this, result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(this, result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
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
