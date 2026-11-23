using System.Windows;
using System.Windows.Controls;
using KBEA.Core.Archive;
using KBEA.Core.Plugins;

namespace KBEA.Plugin.Rsync;

/// <summary>
/// rsync 归档插件入口：
/// Init 时把归档实现注册进 ArchiveOperationsProvider（宿主各消费点经 IArchiveOperations 解析），
/// 并经 IToolbarContribution 把「关键节点」「留档历史」按钮挂上宿主命令（行为在宿主，入口归插件）。
/// 插件缺失/被禁用时：宿主归档操作优雅降级，工具栏不再出现归档按钮。
/// </summary>
public sealed class RsyncPlugin : IPluginEntryPoint, IToolbarContribution, ISettingsContribution
{
    private IPluginHostContext? _context;
    private TextBox? _rsyncBox;
    private TextBox? _archiveBox;
    private TextBox? _timeoutBox;

    public string DisplayName => "rsync 归档插件";

    public void Init(IPluginHostContext context)
    {
        _context = context;
        ArchiveOperationsProvider.Register(new RsyncArchiveService());
    }

    public void Shutdown() => _context = null;

    // 插件只声明「约定图标 + 短文字 + 提示 + 命令」；图标风格、排布与放不下时的降级由宿主负责
    public IReadOnlyList<ToolbarGroupVm> GetToolbarGroups()
    {
        var ui = _context?.ArchiveUi;
        if (ui is null)
        {
            // Worker / 无 UI 宿主：不产出按钮
            return [];
        }
        return
        [
            new ToolbarGroupVm
            {
                Title = "归档",
                Buttons =
                [
                    new ToolbarButtonVm
                    {
                        Icon = ToolbarIcon.Save,
                        Label = "关键节点",
                        Tooltip = "标记关键节点：用 rsync 将当前空间归档到归档根目录（并保留上一版本备份）",
                        Command = ui.ArchiveCommand
                    },
                    new ToolbarButtonVm
                    {
                        Icon = ToolbarIcon.History,
                        Label = "留档历史",
                        Tooltip = "查看该空间的 rsync 留档历史记录（节点名称、目标位置、备注与时间）",
                        Command = ui.ArchiveHistoryCommand
                    }
                ]
            }
        ];
    }

    // ==================== 设置页 ====================

    /// <summary>
    /// 插件设置页：rsync 路径、归档根目录、超时。
    /// 配置持久化由插件自理（写 %AppData%\KBEA\plugins\kbea-plugin-rsync\settings.json）。
    /// </summary>
    public SettingsPageVm GetSettingsPage()
    {
        var settings = ArchiveSettings.Load();
        _rsyncBox = new TextBox { Text = settings.RsyncExecutable };
        _archiveBox = new TextBox { Text = settings.ArchiveRoot };
        _timeoutBox = new TextBox { Text = settings.TimeoutHours.ToString() };

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Text = "rsync 归档插件：配置关键节点归档所用的 rsync 可执行文件与归档根目录。插件需自备可信的 Windows 便携版 rsync。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(MakeLabel("rsync.exe 路径"));
        panel.Children.Add(_rsyncBox);
        panel.Children.Add(new TextBlock
        {
            Text = "相对路径按程序目录解析，默认 tools/rsync/rsync.exe。",
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 8)
        });
        panel.Children.Add(MakeLabel("归档根目录"));
        panel.Children.Add(_archiveBox);
        panel.Children.Add(new TextBlock
        {
            Text = "留档写入 <归档根>/<项目>/current，历史版本留在同目录的节点子目录。",
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 8)
        });
        panel.Children.Add(MakeLabel("超时（小时）"));
        panel.Children.Add(_timeoutBox);

        return new SettingsPageVm
        {
            Title = "rsync 归档",
            Description = "由 kbea-plugin-rsync 经 ISettingsContribution 贡献。",
            Content = panel
        };
    }

    private static TextBlock MakeLabel(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 4, 0, 2)
    };

    public void OnSettingsSaved()
    {
        var settings = ArchiveSettings.Load();
        if (_rsyncBox is not null) settings.RsyncExecutable = _rsyncBox.Text.Trim();
        if (_archiveBox is not null) settings.ArchiveRoot = _archiveBox.Text.Trim();
        if (_timeoutBox is not null && int.TryParse(_timeoutBox.Text.Trim(), out var hours) && hours > 0)
            settings.TimeoutHours = hours;
        settings.Save();
        _context?.Ui.SetGlobalStatus("rsync 归档插件设置已保存。");
    }
}
