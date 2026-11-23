using System.IO;
using System.Windows.Media.Imaging;

namespace KBEA.App.ViewModels;

/// <summary>文件预览辅助：文本 / 图片二选一加载，供文件浏览与语义检索视图共用。</summary>
internal static class PreviewHelper
{
    public static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".json", ".xml", ".csv", ".log", ".cs", ".xaml", ".sql", ".py", ".js", ".html", ".css", ".yml", ".yaml" };

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>加载预览。返回 (图片, 文本)：图片非空时显示图片，否则显示文本。
    /// pluginPreview（P4）：预览链第一棒——先问插件（按扩展名接手），未命中再走内置文本/图片渲染。</summary>
    public static (BitmapImage? Image, string Text) Load(string fullPath, KBEA.Core.Files.FileBrowserService files,
        Func<string, (byte[]? ImageData, string Text)?>? pluginPreview = null)
    {
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return (null, "选择左侧命中文件可在此预览。");
        try
        {
            if (pluginPreview is not null)
            {
                var contributed = pluginPreview(fullPath);
                if (contributed is { } hit)
                    return (Decode(hit.ImageData), hit.ImageData is { Length: > 0 } ? string.Empty : hit.Text);
            }
            if (ImageExtensions.Contains(Path.GetExtension(fullPath)))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(fullPath);
                image.EndInit();
                image.Freeze();
                return (image, string.Empty);
            }
            if (TextExtensions.Contains(Path.GetExtension(fullPath))) return (null, files.ReadTextPreview(fullPath));
            return (null, "此格式暂不支持内置预览。点击「打开」可使用系统默认程序。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (null, $"预览失败：{ex.Message}");
        }
    }

    /// <summary>把插件返回的图片字节解码为可跨线程显示的 BitmapImage；空/失败返回 null。</summary>
    private static BitmapImage? Decode(byte[]? data)
    {
        if (data is not { Length: > 0 }) return null;
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(data);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return null;
        }
    }
}
