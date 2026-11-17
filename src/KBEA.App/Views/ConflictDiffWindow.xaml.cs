using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DiffPlex.DiffBuilder;
using KBEA.Core.Git;
using KBEA.Core.Models;

namespace KBEA.App.Views;

/// <summary>
/// 冲突/变更版本比对窗口（设计文档 §5.7 M6 选型：DiffPlex 双屏 diff）。
/// 左 = HEAD 基线，右 = 工作区；文本文件支持编辑合并结果并标记已解决，
/// 二进制文件提供"保留工作区 / 还原 HEAD"两个选择。
/// </summary>
public partial class ConflictDiffWindow : Window
{
    /// <summary>双屏 diff 的一行：左右各携带文本与变更类型（Unchanged/Deleted/Inserted/Imaginary）。</summary>
    public sealed record DiffRow(string LeftText, string LeftKind, string RightText, string RightKind);

    public ObservableCollection<DiffRow> Rows { get; } = [];

    private readonly GitService _git;
    private readonly string _repoRoot;
    private readonly string _relativePath;
    private HeadFileContent _head = HeadFileContent.Missing;
    private bool _syncingScroll;

    public ConflictDiffWindow(GitService git, string repoRoot, string relativePath)
    {
        InitializeComponent();
        DataContext = this;
        _git = git;
        _repoRoot = repoRoot;
        _relativePath = relativePath;

        FileText.Text = relativePath;
        Title = $"版本比对 · {Path.GetFileName(relativePath)}";
        Load();
    }

    private void Load()
    {
        _head = _git.GetHeadFileText(_repoRoot, _relativePath);
        var fullPath = Path.Combine(_repoRoot, _relativePath);
        var worktreeExists = File.Exists(fullPath);
        var worktreeText = worktreeExists ? File.ReadAllText(fullPath) : null;

        var headDesc = _head.Found ? (_head.IsBinary ? "二进制" : $"{_head.Text.Length} 字符") : "不存在（新文件）";
        var workDesc = worktreeExists ? $"{worktreeText?.Length ?? 0} 字符" : "已删除";
        HeadInfoText.Text = $"基线：HEAD 中 {headDesc}    工作区：{workDesc}    解决冲突后需提交才会进入下一次同步。";

        // 二进制文件：按设计文档 §5.7 走三选一中的保留/还原分支，不做文本 diff
        if (_head.IsBinary)
        {
            BinaryPanel.Visibility = Visibility.Visible;
            ResolveHint.Text = "二进制文件：选择「保留工作区版本」或「还原为 HEAD 版本」，点击后即标记冲突已解决并提交。";
            ResolvedBox.Visibility = Visibility.Collapsed;
            SaveResolvedButton.Visibility = Visibility.Collapsed;
            RevertHeadButton.IsEnabled = _head.Found;
            return;
        }

        DiffPanel.Visibility = Visibility.Visible;

        var headText = _head.Found ? _head.Text : string.Empty;
        var currentText = worktreeText ?? string.Empty;
        var diff = new KBEA.Core.Diff.TextDiffService().Compare(headText, currentText);

        for (var i = 0; i < diff.OldText.Lines.Count || i < diff.NewText.Lines.Count; i++)
        {
            var left = i < diff.OldText.Lines.Count ? diff.OldText.Lines[i] : null;
            var right = i < diff.NewText.Lines.Count ? diff.NewText.Lines[i] : null;
            Rows.Add(new DiffRow(
                left?.Text ?? string.Empty,
                left?.Type.ToString() ?? "Imaginary",
                right?.Text ?? string.Empty,
                right?.Type.ToString() ?? "Imaginary"));
        }

        if (!_head.Found)
        {
            ResolveHint.Text = "HEAD 中不存在该文件（新文件）。确认内容无误后点击「保留工作区版本」即可标记已解决。";
            RevertHeadButton.IsEnabled = false;
        }
        else if (!worktreeExists)
        {
            ResolveHint.Text = "工作区中该文件已被删除。「还原为 HEAD 版本」可恢复文件并标记已解决。";
            ResolvedBox.Text = string.Empty;
            SaveResolvedButton.IsEnabled = false;
            KeepWorktreeButton.IsEnabled = false;
        }
        else
        {
            ResolvedBox.Text = worktreeText ?? string.Empty;
        }
    }

    /// <summary>两列 diff 同步滚动。</summary>
    private void DiffScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        try
        {
            if (ReferenceEquals(sender, LeftScroll))
            {
                RightScroll.ScrollToVerticalOffset(LeftScroll.VerticalOffset);
                RightScroll.ScrollToHorizontalOffset(LeftScroll.HorizontalOffset);
            }
            else
            {
                LeftScroll.ScrollToVerticalOffset(RightScroll.VerticalOffset);
                LeftScroll.ScrollToHorizontalOffset(RightScroll.HorizontalOffset);
            }
        }
        finally { _syncingScroll = false; }
    }

    private void KeepWorktree_Click(object sender, RoutedEventArgs e) => Resolve(worktreeText: true, resolvedText: null);

    private void RevertHead_Click(object sender, RoutedEventArgs e)
    {
        if (!_head.Found)
        {
            MessageBox.Show(this, "HEAD 中没有该文件的基线版本，无法还原。", "版本比对", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Resolve(worktreeText: false, resolvedText: _head.IsBinary ? null : _head.Text);
    }

    private void SaveResolved_Click(object sender, RoutedEventArgs e) => Resolve(worktreeText: false, resolvedText: ResolvedBox.Text);

    private void Resolve(bool worktreeText, string? resolvedText)
    {
        try
        {
            // 二进制 + 还原 HEAD：GitService 需要文件内容写回；二进制场景只允许保留工作区（直接暂存）
            if (!worktreeText && _head.IsBinary)
            {
                MessageBox.Show(this, "二进制文件请使用「保留工作区版本」，或在文件管理器中手动替换内容后再试。", "版本比对", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _git.ResolveConflict(_repoRoot, _relativePath, resolvedText, keepWorktree: worktreeText);
            MessageBox.Show(this, "冲突已标记解决并提交。", "版本比对", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"标记解决失败：{ex.Message}", "版本比对", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
