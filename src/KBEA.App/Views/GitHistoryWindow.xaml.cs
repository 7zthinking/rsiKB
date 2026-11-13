using System.Collections.Generic;
using System.Windows;
using KBEA.Core.Models;

namespace KBEA.App.Views;

public partial class GitHistoryWindow : Window
{
    public GitHistoryWindow(string title, GitStatusInfo status, IEnumerable<GitStatusEntry> entries, IEnumerable<GitCommitInfo> commits)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;

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
}
