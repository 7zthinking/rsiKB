using System.Collections.Generic;
using System.Windows;
using KBEA.Core.Git;
using KBEA.Core.Models;

namespace KBEA.App.Views;

public partial class GitHistoryWindow : Window
{
    private readonly string? _repositoryPath;

    public GitHistoryWindow(string title, string? repositoryPath, GitStatusInfo status, IEnumerable<GitStatusEntry> entries, IEnumerable<GitCommitInfo> commits)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        _repositoryPath = repositoryPath;

        if (!status.IsRepository)
        {
            BranchText.Text = "不在 Git 仓库中";
            ChangeCountText.Text = string.Empty;
            StatusGrid.ItemsSource = Array.Empty<GitStatusEntry>();
            HistoryGrid.ItemsSource = Array.Empty<GitCommitInfo>();
            return;
        }

        BranchText.Text = $"分支：{status.Branch}";
        var parts = new List<string>();
        if (status.ChangeCount > 0) parts.Add($"{status.ChangeCount} 项变更");
        if (!string.IsNullOrWhiteSpace(status.RemoteUrl)) parts.Add($"远程 {status.RemoteUrl}");
        ChangeCountText.Text = parts.Count > 0 ? string.Join(" · ", parts) : "工作区干净";
        if (status.ConflictCount > 0)
        {
            ConflictText.Text = $"{status.ConflictCount} 个冲突，需要解决";
            ConflictText.Visibility = Visibility.Visible;
        }

        StatusGrid.ItemsSource = entries;
        HistoryGrid.ItemsSource = commits;
    }

    /// <summary>只有选中"冲突"文件时才允许版本比对。</summary>
    private void StatusGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        CompareButton.IsEnabled = StatusGrid.SelectedItem is GitStatusEntry entry
            && entry.State.Contains("冲突");
    }

    private void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        if (StatusGrid.SelectedItem is not GitStatusEntry entry || string.IsNullOrWhiteSpace(_repositoryPath)) return;
        var dialog = new ConflictDiffWindow(new GitService(), _repositoryPath, entry.RelativePath) { Owner = this };
        dialog.ShowDialog();
    }
}
