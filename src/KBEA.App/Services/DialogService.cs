using System.Windows;
using KBEA.App.Views;
using KBEA.Core.Models;
using Microsoft.Win32;

namespace KBEA.App.Services;

public sealed class DialogService : IDialogService
{
    public string? Prompt(string title, string label, string defaultValue = "")
    {
        var dialog = new InputDialog(title, label, defaultValue) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public string? SelectFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }

    public void Info(string message, string title = "知识库自进化助手") => MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool Confirm(string message, string title = "请确认") => MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool ShowSettings(AppConfiguration configuration)
    {
        var dialog = new SettingsWindow(configuration) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true;
    }

    public void ShowHistory(IEnumerable<GitCommitInfo> commits, string title)
    {
        var dialog = new RecordListWindow(title, commits) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
    }

    public void ShowArchives(IEnumerable<ArchiveRecord> records, string title)
    {
        var dialog = new RecordListWindow(title, records) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
    }
}
