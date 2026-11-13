using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using KBEA.Core.Models;
using KBEA.Core.Services;
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

    // Browse 按钮需要直接访问的 TextBox 字段
    private TextBox _rsyncBox = null!;
    private TextBox _archiveBox = null!;

    public ObservableCollection<LlmEndpointConfig> LlmEndpoints { get; } = new();

    private const string CurrentVersion = "v0.3.0";

    public SettingsWindow(AppConfiguration configuration)
    {
        InitializeComponent();

        _configuration = configuration;

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
        _panels["about"] = BuildAboutPanel();

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

        _rsyncBox = new TextBox();
        _rsyncBox.SetBinding(TextBox.TextProperty,
            new System.Windows.Data.Binding(nameof(AppConfiguration.RsyncExecutable))
            {
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
                Source = dc
            });
        panel.Children.Add(MakeBrowseRow("rsync.exe", _rsyncBox, BrowseRsync_Click));

        _archiveBox = new TextBox();
        _archiveBox.SetBinding(TextBox.TextProperty,
            new System.Windows.Data.Binding(nameof(AppConfiguration.ArchiveRoot))
            {
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
                Source = dc
            });
        panel.Children.Add(MakeBrowseRow("归档根目录", _archiveBox, BrowseArchive_Click));

        var tip = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 10, 0, 0)
        };
        tip.Child = new TextBlock
        {
            Text = "默认查找程序目录下的 tools/rsync/rsync.exe。绿色发布时请放入便携版。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush")
        };
        panel.Children.Add(tip);

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
            Text = "知识库自进化助手 · 开源版",
            FontSize = 20,
            FontWeight = FontWeights.Bold
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"版本 {CurrentVersion}",
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            Margin = new Thickness(0, 2, 0, 10)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "一个以 Git / rsync 为底座的知识工作台，把本地目录、远程仓库和归档节点串成一张可追溯、可对比的知识图谱。开源核心可独立使用，插件接入后提供智能助手与报表能力。",
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

            var response = await _httpClient.GetAsync("https://api.github.com/repos/7zthinking/rsiKB/releases/latest");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var latestTag = doc.RootElement.TryGetProperty("tag_name", out var tagProp)
                ? tagProp.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(latestTag))
            {
                MessageBox.Show(this, "无法读取远程版本号，请稍后重试。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var currentNum = NormalizeVersion(CurrentVersion);
            var latestNum = NormalizeVersion(latestTag);

            string message;
            if (CompareVersionNumbers(currentNum, latestNum) < 0)
                message = $"发现新版本 {latestTag}（当前 {CurrentVersion}）。请到 GitHub 下载最新版。";
            else if (CompareVersionNumbers(currentNum, latestNum) == 0)
                message = $"已是最新版本 {CurrentVersion}。";
            else
                message = $"当前版本 {CurrentVersion} 领先于远程 {latestTag}，可能是预发布版。";

            MessageBox.Show(this, message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private static string NormalizeVersion(string v) =>
        Regex.Replace(v, @"^[vV]", string.Empty).Trim();

    private static int CompareVersionNumbers(string a, string b)
    {
        var aParts = a.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var bParts = b.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int len = Math.Max(aParts.Length, bParts.Length);
        for (int i = 0; i < len; i++)
        {
            int ai = i < aParts.Length && int.TryParse(aParts[i], out var x1) ? x1 : 0;
            int bi = i < bParts.Length && int.TryParse(bParts[i], out var x2) ? x2 : 0;
            if (ai != bi) return ai.CompareTo(bi);
        }
        return 0;
    }

    // ==================== Save & Browse Handlers ====================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var edited = (AppConfiguration)DataContext;
        _configuration.MachineId = edited.MachineId;
        _configuration.MachineName = edited.MachineName;
        _configuration.AuthorName = edited.AuthorName;
        _configuration.AuthorEmail = edited.AuthorEmail;
        _configuration.RsyncExecutable = edited.RsyncExecutable;
        _configuration.ArchiveRoot = edited.ArchiveRoot;
        _configuration.AutoDetect = edited.AutoDetect;
        _configuration.Theme = edited.Theme;
        _configuration.LlmEndpoints = [.. LlmEndpoints];
        _configuration.Organizations = edited.Organizations;
        _configuration.OrganizationShares = edited.OrganizationShares;
        DialogResult = true;
    }

    private void BrowseRsync_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "rsync.exe|rsync.exe|可执行文件|*.exe" };
        if (dialog.ShowDialog(this) == true) _rsyncBox.Text = dialog.FileName;
    }

    private void BrowseArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择归档根目录" };
        if (dialog.ShowDialog(this) == true) _archiveBox.Text = dialog.FolderName;
    }
}
