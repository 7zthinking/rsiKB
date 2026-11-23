using KBEA.Core.Knowledge;
using KBEA.Core.Plugins;

namespace KBEA.Plugin.Wiki;

/// <summary>
/// 知识 WIKI 插件入口（P3，docs/plugin-architecture.md §5）：
/// Init 时把 Ingest 引擎实现注册进 WikiOperationsProvider（Worker 调度器经 IWikiOperations 解析），
/// 并经 IToolbarContribution 把「知识WIKI」工具栏按钮挂上宿主命令（行为在宿主，入口归插件）。
/// 插件缺失/被禁用时：Worker 元数据任务标记失败（提示插件未加载），工具栏不再出现知识WIKI 按钮。
/// </summary>
public sealed class WikiPlugin : IPluginEntryPoint, IToolbarContribution
{
    private IPluginHostContext? _context;

    public string DisplayName => "知识 WIKI 插件";

    public void Init(IPluginHostContext context)
    {
        _context = context;
        WikiOperationsProvider.Register(new KnowledgeIngestService());
    }

    public void Shutdown() => _context = null;

    public IReadOnlyList<ToolbarGroupVm> GetToolbarGroups()
    {
        var ui = _context?.WikiUi;
        if (ui is null)
        {
            // Worker / 无 UI 宿主：不产出按钮
            return [];
        }
        // 插件只声明「约定图标 + 短文字 + 提示 + 命令」；图标风格与排布由宿主框架负责
        return
        [
            new ToolbarGroupVm
            {
                Title = "知识 WIKI",
                Buttons =
                [
                    new ToolbarButtonVm
                    {
                        Icon = ToolbarIcon.Wiki,
                        Label = "WIKI",
                        Tooltip = "知识WIKI：查看当前工作空间各层元数据的构建进度、更新时间，手动重跑或配置定时计划",
                        Command = ui.OpenMetadataConfigCommand
                    }
                ]
            }
        ];
    }
}
