using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KBEA.Core.Git;
using KBEA.Core.Models;

namespace KBEA.App.Views;

/// <summary>
/// 冲突/变更版本比对窗口（DiffPlex 双屏 diff）。
/// 左 = HEAD 基线（只读），右 = 工作区当前内容（**可直接编辑**，颜色由底层 diff 行渲染）；
/// 编辑后实时重算 diff 并检测冲突标记，无冲突才允许「保存并标记已解决」。
/// 历史版本对比（allowEdit=false）时右栏只读、隐藏解决按钮。
/// 加载异步化：窗口立即显示，Loading 完成后再呈现内容。
/// </summary>
public partial class ConflictDiffWindow : Window
{
    /// <summary>双屏 diff 的一行：左右各携带文本与变更类型（Unchanged/Deleted/Inserted/Imaginary）。</summary>
    public sealed record DiffRow(string LeftText, string LeftKind, string RightText, string RightKind)
    {
        public bool IsChanged => LeftKind != "Unchanged" || RightKind != "Unchanged";
        /// <summary>同一行左右分别为删除/新增 = 修改冲突对（琥珀色突出）。</summary>
        public bool IsConflictPair => LeftKind == "Deleted" && RightKind == "Inserted";
    }

    public ObservableCollection<DiffRow> Rows { get; } = [];

    private readonly IGitOperations _git;
    private readonly string _repoRoot;
    private readonly string _relativePath;
    private readonly bool _allowEdit;
    private HeadFileContent _head = HeadFileContent.Missing;
    private bool _worktreeExists;
    private bool _syncingScroll;
    private bool _loaded;
    private DispatcherTimer? _editTimer;
    private List<int> _diffIndexes = [];
    private int _diffCursor = -1;

    public ConflictDiffWindow(IGitOperations git, string repoRoot, string relativePath, bool allowEdit = true)
    {
        InitializeComponent();
        DataContext = this;
        _git = git;
        _repoRoot = repoRoot;
        _relativePath = relativePath;
        _allowEdit = allowEdit;

        FileText.Text = relativePath;
        Title = $"版本比对 · {Path.GetFileName(relativePath)}";
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>窗口已显示后异步加载基线、工作区与 diff，加载期间展示 Loading。</summary>
    private async Task LoadAsync()
    {
        try
        {
            var head = await Task.Run(() => _git.GetHeadFileText(_repoRoot, _relativePath));
            var (exists, text) = await Task.Run(() =>
            {
                var fullPath = Path.Combine(_repoRoot, _relativePath);
                return (File.Exists(fullPath), File.Exists(fullPath) ? File.ReadAllText(fullPath) : null);
            });
            _head = head;
            _worktreeExists = exists;

            HeadInfoText.Text = $"基线：HEAD 中 {(_head.Found ? (_head.IsBinary ? "二进制" : $"{_head.Text.Length} 字符") : "不存在（新文件）")}"
                + $"    工作区：{(exists ? $"{text?.Length ?? 0} 字符" : "已删除")}"
                + "    解决冲突后需提交才会进入下一次同步。";

            if (_head.IsBinary)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                BinaryPanel.Visibility = Visibility.Visible;
                ResolveHint.Text = "二进制文件：选择「保留工作区版本」或「还原为 HEAD 版本」，点击后即标记冲突已解决并提交。";
                SaveResolvedButton.Visibility = Visibility.Collapsed;
                RevertHeadButton.IsEnabled = _head.Found;
                return;
            }

            var rows = await Task.Run(() => BuildRows(_head.Found ? _head.Text : string.Empty, text ?? string.Empty));
            ReplaceRows(rows);

            RightEdit.Text = text ?? string.Empty;
            RightEdit.IsReadOnly = !_allowEdit || !exists;
            RightHeaderText.Text = _allowEdit ? "工作区（当前内容，可直接编辑）" : "历史/工作区（只读对照）";
            KeepWorktreeButton.IsEnabled = exists;
            SaveResolvedButton.IsEnabled = _allowEdit && exists && !ContainsConflictMarkers(RightEdit.Text);
            if (!exists)
            {
                ResolveHint.Text = "工作区中该文件已被删除。「还原为 HEAD 版本」可恢复文件并标记已解决。";
            }
            else if (!_head.Found)
            {
                ResolveHint.Text = "HEAD 中不存在该文件（新文件）。确认内容无误后点击「保留工作区版本」即可标记已解决。";
            }

            LoadingPanel.Visibility = Visibility.Collapsed;
            DiffPanel.Visibility = Visibility.Visible;
            _loaded = true;
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"加载失败：{ex.Message}";
        }
    }

    private static List<DiffRow> BuildRows(string headText, string currentText)
    {
        var diff = new KBEA.Core.Diff.TextDiffService().Compare(headText, currentText);
        var rows = new List<DiffRow>(Math.Max(diff.OldText.Lines.Count, diff.NewText.Lines.Count));
        for (var i = 0; i < diff.OldText.Lines.Count || i < diff.NewText.Lines.Count; i++)
        {
            var left = i < diff.OldText.Lines.Count ? diff.OldText.Lines[i] : null;
            var right = i < diff.NewText.Lines.Count ? diff.NewText.Lines[i] : null;
            rows.Add(new DiffRow(
                left?.Text ?? string.Empty,
                left?.Type.ToString() ?? "Imaginary",
                right?.Text ?? string.Empty,
                right?.Type.ToString() ?? "Imaginary"));
        }
        return rows;
    }

    /// <summary>整体替换 diff 行并重算差异定位索引。</summary>
    private void ReplaceRows(List<DiffRow> rows)
    {
        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        _diffIndexes = rows.Select((r, i) => (r, i)).Where(x => x.r.IsChanged).Select(x => x.i).ToList();
        _diffCursor = -1;
        PrevDiffButton.IsEnabled = _diffIndexes.Count > 0;
        NextDiffButton.IsEnabled = _diffIndexes.Count > 0;
    }

    /// <summary>右栏编辑：防抖 600ms 后重算 diff + 实时冲突检测。</summary>
    private void RightEdit_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded || !_allowEdit || RightEdit.IsReadOnly) return;
        _editTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _editTimer.Tick -= EditTimer_Tick;
        _editTimer.Tick += EditTimer_Tick;
        _editTimer.Stop();
        _editTimer.Start();
    }

    private async void EditTimer_Tick(object? sender, EventArgs e)
    {
        _editTimer!.Stop();
        var text = RightEdit.Text;
        var hasMarkers = ContainsConflictMarkers(text);
        SaveResolvedButton.IsEnabled = !hasMarkers;
        ResolveHint.Text = hasMarkers
            ? "⚠ 内容仍包含未解决的冲突标记（<<<<<<< / >>>>>>>），请处理完再保存。"
            : _head.Found
                ? "✓ 无冲突。可直接保存并标记已解决（保存会把右栏当前内容写回文件）。"
                : "✓ 无冲突。确认内容无误后保存即可标记已解决。";

        var rows = await Task.Run(() => BuildRows(_head.Found ? _head.Text : string.Empty, text));
        ReplaceRows(rows);
    }

    /// <summary>git 冲突标记检测：文本中出现任一侧尖括号标记即视为仍有冲突。</summary>
    private static bool ContainsConflictMarkers(string text)
        => text.Contains("<<<<<<<", StringComparison.Ordinal) || text.Contains(">>>>>>>", StringComparison.Ordinal);

    // ---------- 差异定位 ----------

    private void PrevDiff_Click(object sender, RoutedEventArgs e) => NavigateDiff(-1);

    private void NextDiff_Click(object sender, RoutedEventArgs e) => NavigateDiff(1);

    /// <summary>滚动到上/下一个差异行（按容器实际位置定位，左右两侧与编辑层同步滚动）。</summary>
    private void NavigateDiff(int step)
    {
        if (_diffIndexes.Count == 0) return;
        _diffCursor = (_diffCursor + step + _diffIndexes.Count) % _diffIndexes.Count;
        var rowIdx = _diffIndexes[_diffCursor];
        double target;
        if (LeftList.ItemContainerGenerator.ContainerFromIndex(rowIdx) is FrameworkElement el)
        {
            var pos = el.TransformToVisual(LeftScroll).Transform(new Point(0, 0));
            target = Math.Max(0, LeftScroll.VerticalOffset + pos.Y - LeftScroll.ViewportHeight / 2 + el.ActualHeight / 2);
        }
        else
        {
            target = LeftScroll.VerticalOffset; // 容器尚未虚拟化出来时保持当前位置
        }
        SyncVertical(target);
        StatusTextFlash($"第 {_diffCursor + 1}/{_diffIndexes.Count} 处差异");
    }

    private void StatusTextFlash(string message)
    {
        var original = HeadInfoText.Text;
        HeadInfoText.Text = message;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { HeadInfoText.Text = original; timer.Stop(); };
        timer.Start();
    }

    // ---------- 同步滚动 ----------

    /// <summary>左 ScrollViewer / 右 ScrollViewer / 右编辑框三者同步滚动。</summary>
    private void DiffScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        try
        {
            switch (sender)
            {
                case TextBox tb:
                    SyncVertical(tb.VerticalOffset, tb.HorizontalOffset);
                    break;
                case ScrollViewer sv when ReferenceEquals(sv, LeftScroll):
                    RightScroll.ScrollToVerticalOffset(sv.VerticalOffset);
                    RightScroll.ScrollToHorizontalOffset(sv.HorizontalOffset);
                    RightEdit.ScrollToVerticalOffset(sv.VerticalOffset);
                    break;
                default:
                    LeftScroll.ScrollToVerticalOffset(RightScroll.VerticalOffset);
                    LeftScroll.ScrollToHorizontalOffset(RightScroll.HorizontalOffset);
                    RightEdit.ScrollToVerticalOffset(RightScroll.VerticalOffset);
                    break;
            }
        }
        finally
        {
            Dispatcher.BeginInvoke(() => _syncingScroll = false, DispatcherPriority.Background);
        }
    }

    private void SyncVertical(double vertical, double horizontal = 0)
    {
        LeftScroll.ScrollToVerticalOffset(vertical);
        LeftScroll.ScrollToHorizontalOffset(horizontal);
        RightScroll.ScrollToVerticalOffset(vertical);
        RightScroll.ScrollToHorizontalOffset(horizontal);
        RightEdit.ScrollToVerticalOffset(vertical);
    }

    // ---------- 解决动作 ----------

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

    private void SaveResolved_Click(object sender, RoutedEventArgs e)
    {
        if (ContainsConflictMarkers(RightEdit.Text))
        {
            MessageBox.Show(this, "内容仍包含未解决的冲突标记，请处理完再保存。", "版本比对", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Resolve(worktreeText: false, resolvedText: RightEdit.Text);
    }

    private void Resolve(bool worktreeText, string? resolvedText)
    {
        try
        {
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
