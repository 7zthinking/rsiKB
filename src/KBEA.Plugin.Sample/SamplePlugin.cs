using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using KBEA.Core.Plugins;

namespace KBEA.Plugin.Sample;

/// <summary>
/// 打样插件：演示 manifest 发现 → ALC 加载 → Init 注入 → 工具栏注册 的完整链路。
/// Init 时把示例元数据插件注册进 PluginManager，使 .sampletxt 文件在整理时获得扩展属性。
/// P4：再演示三个扩展点——详情字段（IMetadataFieldContribution）、预览接手（IPreviewContribution）、
/// 设置页（ISettingsContribution）。设置项持久化由插件自行负责（示例为内存态，重启还原默认）。
/// </summary>
public sealed class SamplePlugin : IPluginEntryPoint, IToolbarContribution,
    IMetadataFieldContribution, IPreviewContribution, ISettingsContribution
{
    private IPluginHostContext? _context;

    // 设置页示例状态（内存态）：真实插件应自行落盘（如写自己的数据文件）
    private string _sampleNote = "示例备注（可编辑，点保存设置后写入状态栏）";

    public string DisplayName => "示例插件";

    public void Init(IPluginHostContext context)
    {
        _context = context;
        // Register 按 PluginId 去重，重复 Init 不会叠加
        PluginManager.Instance.Register(new SampleMetadataPlugin());
    }

    public void Shutdown() => _context = null;

    // 插件只声明「约定图标 + 短文字 + 提示 + 命令」；图标风格与排布由宿主框架负责
    public IReadOnlyList<ToolbarGroupVm> GetToolbarGroups() =>
    [
        new ToolbarGroupVm
        {
            Title = "示例",
            Buttons =
            [
                new ToolbarButtonVm
                {
                    Icon = ToolbarIcon.Info,
                    Label = "示例",
                    Tooltip = "示例插件：读取宿主当前浏览状态并写入全局状态栏",
                    Command = new RelayCommand(() =>
                    {
                        var browser = _context?.Browser.Current;
                        _context?.Ui.SetGlobalStatus(
                            $"示例插件按钮已点击，当前目录：{browser?.CurrentPath ?? "（无）"}");
                    })
                }
            ]
        }
    ];

    // ==================== P4 扩展点 ====================

    /// <summary>详情字段：对 .sampletxt 追加一行（其余格式返回空，不污染内置 6 行）。</summary>
    public IReadOnlyList<(string Name, string Value)> GetFields(string filePath) =>
        Path.GetExtension(filePath).Equals(".sampletxt", StringComparison.OrdinalIgnoreCase)
            ? [("示例插件", ".sampletxt 文件，示例字段贡献生效")]
            : [];

    /// <summary>预览：接手 .sampletxt，返回文件内容前缀说明（其余格式不支持）。</summary>
    public bool Supports(string extension) => extension.Equals(".sampletxt", StringComparison.OrdinalIgnoreCase);

    public (byte[]? ImageData, string Text) Render(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            return (null, $"示例插件预览：\n{content}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"示例插件预览失败：{ex.Message}");
        }
    }

    /// <summary>设置页：一个说明 + 一个备注输入框（内存态，保存时写状态栏演示回调链）。</summary>
    public SettingsPageVm GetSettingsPage()
    {
        var noteBox = new TextBox { Text = _sampleNote, AcceptsReturn = true, TextWrapping = System.Windows.TextWrapping.Wrap, Height = 64 };
        // 保存回调时从控件取最新值：用 Tag 挂到插件字段（GetSettingsPage 每次开窗重建）
        _noteBoxRef = noteBox;
        var panel = new StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Text = "示例插件设置页：演示插件可向设置窗口追加自己的配置页。",
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock { Text = "示例备注", FontWeight = System.Windows.FontWeights.SemiBold });
        panel.Children.Add(noteBox);
        return new SettingsPageVm
        {
            Title = "示例设置",
            Description = "由 kbea-plugin-sample 经 ISettingsContribution 贡献。",
            Content = panel
        };
    }

    private TextBox? _noteBoxRef;

    public void OnSettingsSaved()
    {
        if (_noteBoxRef is not null) _sampleNote = _noteBoxRef.Text;
        _context?.Ui.SetGlobalStatus($"示例插件设置已保存：{_sampleNote}");
    }
}

/// <summary>极简 ICommand 实现，避免插件引入额外依赖。</summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
