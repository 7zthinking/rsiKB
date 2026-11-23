using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KBEA.Core.Plugins;
using MahApps.Metro.IconPacks;

namespace KBEA.App.Controls;

/// <summary>
/// 工具栏图标库（P8 升级）：用 MahApps.Metro.IconPacks.Lucide 的 4000+ 高质量
/// 线性描边图标替代手写 Geometry。
///
/// 实现：
///  - Lucide 的 Data 属性暴露 SVG path 字符串 → Geometry.Parse() 转 Geometry
///  - DrawingImage + DrawingBrush 直接构造矢量 ImageSource，绕过控件渲染
///  - 画布 24×24（Lucide 原生坐标系），Brush 颜色从 Theme 取
/// </summary>
public static class ToolbarIconLibrary
{
    /// <summary>图标画布边长（px）。Lucide 原生 24×24。</summary>
    public const double Size = 16;

    private const double Viewbox = 24.0;

    /// <summary>Lucide 标准描边宽度（在 24×24 坐标系中）。</summary>
    private const double StrokeWidth = 2.0;

    /// <summary>ToolbarIcon → PackIconLucideKind 的映射表。</summary>
    private static readonly IReadOnlyDictionary<ToolbarIcon, PackIconLucideKind> Map =
        new Dictionary<ToolbarIcon, PackIconLucideKind>
        {
            // —— 导航 ——
            [ToolbarIcon.Back]    = PackIconLucideKind.ArrowLeft,
            [ToolbarIcon.Forward] = PackIconLucideKind.ArrowRight,
            [ToolbarIcon.Up]      = PackIconLucideKind.ArrowUp,
            [ToolbarIcon.Refresh] = PackIconLucideKind.RefreshCw,
            [ToolbarIcon.Menu]    = PackIconLucideKind.Menu,
            [ToolbarIcon.Sidebar] = PackIconLucideKind.PanelLeft,
            [ToolbarIcon.Preview] = PackIconLucideKind.Eye,
            [ToolbarIcon.Search]  = PackIconLucideKind.Search,
            [ToolbarIcon.Clear]   = PackIconLucideKind.X,

            // —— 文件操作 ——
            [ToolbarIcon.Star]    = PackIconLucideKind.Star,
            [ToolbarIcon.Folder]  = PackIconLucideKind.Folder,
            [ToolbarIcon.Tag]     = PackIconLucideKind.Tag,
            [ToolbarIcon.Catalog] = PackIconLucideKind.List,
            [ToolbarIcon.Open]    = PackIconLucideKind.FolderOpen,
            [ToolbarIcon.Copy]    = PackIconLucideKind.Copy,
            [ToolbarIcon.Cut]     = PackIconLucideKind.Scissors,
            [ToolbarIcon.Paste]   = PackIconLucideKind.ClipboardPaste,
            [ToolbarIcon.Delete]  = PackIconLucideKind.Trash2,
            [ToolbarIcon.Rename]  = PackIconLucideKind.Pencil,
            [ToolbarIcon.Edit]    = PackIconLucideKind.PencilLine,
            [ToolbarIcon.Save]    = PackIconLucideKind.Save,

            // —— 版本与对比 ——
            [ToolbarIcon.Compare]  = PackIconLucideKind.GitCompare,
            [ToolbarIcon.History]  = PackIconLucideKind.History,
            [ToolbarIcon.Branch]   = PackIconLucideKind.GitBranch,
            [ToolbarIcon.Merge]    = PackIconLucideKind.GitMerge,
            [ToolbarIcon.Pull]     = PackIconLucideKind.GitPullRequest,
            [ToolbarIcon.Push]     = PackIconLucideKind.Send,
            [ToolbarIcon.Upload]   = PackIconLucideKind.Upload,
            [ToolbarIcon.Download] = PackIconLucideKind.Download,
            [ToolbarIcon.Remote]   = PackIconLucideKind.Cloud,
            [ToolbarIcon.Sync]     = PackIconLucideKind.RefreshCcw,

            // —— 知识与元数据 ——
            [ToolbarIcon.Wiki]     = PackIconLucideKind.BookOpen,
            [ToolbarIcon.Metadata] = PackIconLucideKind.Database,
            [ToolbarIcon.Catalog2] = PackIconLucideKind.LayoutGrid,
            [ToolbarIcon.Index]    = PackIconLucideKind.ListOrdered,
            [ToolbarIcon.Tag2]     = PackIconLucideKind.Tags,
            [ToolbarIcon.Layer]    = PackIconLucideKind.Layers,
            [ToolbarIcon.Chart]    = PackIconLucideKind.ChartBar,

            // —— 通用 ——
            [ToolbarIcon.Settings] = PackIconLucideKind.Settings,
            [ToolbarIcon.Info]     = PackIconLucideKind.Info,
            [ToolbarIcon.Warning]  = PackIconLucideKind.TriangleAlert,
            [ToolbarIcon.Error]    = PackIconLucideKind.CircleX,
            [ToolbarIcon.Check]    = PackIconLucideKind.Check,
            [ToolbarIcon.Close]    = PackIconLucideKind.X,
            [ToolbarIcon.Add]      = PackIconLucideKind.Plus,
            [ToolbarIcon.Remove]   = PackIconLucideKind.Minus,
            [ToolbarIcon.More]     = PackIconLucideKind.Ellipsis,
            [ToolbarIcon.Play]     = PackIconLucideKind.Play,
            [ToolbarIcon.Stop]     = PackIconLucideKind.Square,
        };

    /// <summary>按当前前景色构造矢量图标（DrawingImage，不依赖 WPF Dispatcher）。</summary>
    public static ImageSource? Create(ToolbarIcon icon, Color foreground)
    {
        if (icon == ToolbarIcon.None) return null;
        if (!Map.TryGetValue(icon, out var kind)) return null;

        // 直接复用 MahApps 官方实现（LucideImageExtension → CreateImageSource）：
        // 官方把 Lucide 的 SVG 描边路径预转成了 1000×1000 坐标系的「填充轮廓」，
        // 用 GeometryDrawing.Brush 填充并包一层 DrawingGroup + DrawingImage。
        // 自己用 Pen 描边或按 24×24 坐标系解析都会导致图标几乎不可见。
        var brush = new SolidColorBrush(foreground);
        brush.Freeze();

        var extension = new LucideImageExtension(kind) { Brush = brush };
        var result = extension.ProvideValue(null) as ImageSource;
        result?.Freeze();
        return result;
    }
}

