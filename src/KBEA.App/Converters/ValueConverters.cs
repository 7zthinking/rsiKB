using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KBEA.App.ViewModels;
using KBEA.Core.Models;

namespace KBEA.App.Converters;

public sealed class FileIconConverter : IValueConverter
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;
    private static readonly ConcurrentDictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var (path, isDirectory) = value switch
        {
            FileEntry file => (file.FullPath, file.IsDirectory),
            LocalFolderViewModel folder => (folder.FullPath, true),
            FolderNodeViewModel folder => (folder.FullPath, true),
            string folderPath => (folderPath, true),
            _ => (string.Empty, false)
        };
        if (string.IsNullOrWhiteSpace(path)) return null!;

        var extension = Path.GetExtension(path);
        var key = isDirectory
            ? $"dir:{Path.GetFullPath(path)}"
            : string.IsNullOrEmpty(extension) ? "<file>" : extension;
        if (IconCache.TryGetValue(key, out var cached)) return cached;

        var icon = LoadSystemIcon(path);
        if (icon is null) return null!;
        IconCache[key] = icon;
        return icon;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    private static ImageSource? LoadSystemIcon(string path)
    {
        var info = new ShellFileInfo();
        var flags = ShgfiIcon | ShgfiSmallIcon;
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), flags);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.IconHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
