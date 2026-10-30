using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using KBEA.Core.Models;

namespace KBEA.App.Converters;

public sealed class FileIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FileEntry file) return "□";
        if (file.IsDirectory) return "▰";
        return Path.GetExtension(file.Name).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" => "▧",
            ".md" or ".txt" or ".doc" or ".docx" or ".pdf" => "▤",
            ".xls" or ".xlsx" or ".csv" => "▦",
            ".zip" or ".7z" or ".rar" => "◈",
            _ => "□"
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
