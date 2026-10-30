using System.Text.Json;
using System.Windows;
using KBEA.Core.Models;
using Microsoft.Win32;

namespace KBEA.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfiguration _configuration;

    public SettingsWindow(AppConfiguration configuration)
    {
        InitializeComponent();
        _configuration = configuration;
        var json = JsonSerializer.Serialize(configuration);
        DataContext = JsonSerializer.Deserialize<AppConfiguration>(json) ?? configuration;
        ThemeBox.ItemsSource = new[] { "Light", "Dark" };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var edited = (AppConfiguration)DataContext;
        _configuration.AuthorName = edited.AuthorName;
        _configuration.AuthorEmail = edited.AuthorEmail;
        _configuration.RsyncExecutable = edited.RsyncExecutable;
        _configuration.ArchiveRoot = edited.ArchiveRoot;
        _configuration.AutoDetect = edited.AutoDetect;
        _configuration.Theme = edited.Theme;
        DialogResult = true;
    }

    private void BrowseRsync_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "rsync.exe|rsync.exe|可执行文件|*.exe" };
        if (dialog.ShowDialog(this) == true) RsyncBox.Text = dialog.FileName;
    }

    private void BrowseArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择归档根目录" };
        if (dialog.ShowDialog(this) == true) ArchiveBox.Text = dialog.FolderName;
    }
}
