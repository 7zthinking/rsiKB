using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using KBEA.Core.Plugins;

namespace KBEA.App.Controls;

/// <summary>
/// XAML 用法：&lt;Image Source="{toolbar:ToolbarIconSource Image=core:ToolbarIcon.Search}" /&gt;
/// 从约定图标集取线性矢量图标，颜色跟随宿主前景色（TextBrush）。
/// </summary>
[MarkupExtensionReturnType(typeof(ImageSource))]
public sealed class ToolbarIconSource : MarkupExtension
{
    /// <summary>图标（枚举名）。</summary>
    public ToolbarIcon Image { get; set; } = ToolbarIcon.None;

    /// <summary>图标颜色；留空则跟随主题前景色。</summary>
    public Color? Foreground { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (Image == ToolbarIcon.None) return null;
        var color = Foreground ?? ResolveForeground();
        return ToolbarIconLibrary.Create(Image, color);
    }

    private static Color ResolveForeground()
    {
        var resource = Application.Current?.TryFindResource("TextBrush") as SolidColorBrush;
        return resource?.Color ?? Colors.Black;
    }
}
