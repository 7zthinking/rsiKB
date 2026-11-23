using System.Windows.Input;
using KBEA.Core.Git;
using KBEA.Core.Plugins;

namespace KBEA.Plugin.Git;

/// <summary>
/// Git 插件入口（P2，docs/plugin-architecture.md §5）：
/// Init 时把 GitService 实现注册进 GitOperationsProvider（宿主各消费点经 IGitOperations 解析），
/// 并经 IToolbarContribution 把 5 个 Git 工具栏按钮挂上宿主命令集（行为在宿主，入口归插件）。
/// 插件缺失/被禁用时：宿主 Git 操作优雅降级，工具栏不再出现 Git 按钮。
/// </summary>
public sealed class GitPlugin : IPluginEntryPoint, IToolbarContribution
{
    private IPluginHostContext? _context;

    public string DisplayName => "Git 插件";

    public void Init(IPluginHostContext context)
    {
        _context = context;
        GitOperationsProvider.Register(new GitService());
    }

    public void Shutdown() => _context = null;

    public IReadOnlyList<ToolbarGroupVm> GetToolbarGroups()
    {
        var ui = _context?.GitUi;
        if (ui is null)
        {
            // Worker / 无 UI 宿主：不产出按钮
            return [];
        }
        // 插件只声明「约定图标 + 短文字 + 提示 + 命令」；
        // 图标的线性风格、排布与放不下时的降级全部由宿主框架负责。
        return
        [
            new ToolbarGroupVm
            {
                Title = "Git",
                Buttons =
                [
                    new ToolbarButtonVm
                    {
                        Icon = ToolbarIcon.Compare,
                        Label = "比对",
                        Tooltip = "版本比对：对选中的冲突文件做 HEAD 基线与工作区的双屏比对，并可标记已解决",
                        Command = ui.CompareConflictCommand
                    },
                    new ToolbarButtonVm
                    {
                        Icon = ToolbarIcon.Download,
                        Label = "拉取",
                        Tooltip = "拉取更新：从远程仓库拉取最新版本",
                        Command = ui.PullCommand
                    },
                    new ToolbarButtonVm
                    {
                        Icon = ToolbarIcon.Upload,
                        Label = "上传",
                        Tooltip = "保存并上传：提交本地变更并上传到远程仓库",
                        Command = ui.SavePushCommand
                    },
                    new ToolbarButtonVm
                    {
                        Icon = ToolbarIcon.History,
                        Label = "历史",
                        Tooltip = "Git 状态及历史：查看当前仓库的提交历史与变更明细",
                        Command = ui.HistoryCommand
                    },
                    new ToolbarButtonVm
                    {
                        Icon = ToolbarIcon.Remote,
                        Label = "远程",
                        Tooltip = "Git 远程配置：初始化本地仓库并关联远程 origin",
                        Command = ui.BindGitCommand
                    }
                ]
            }
        ];
    }
}
