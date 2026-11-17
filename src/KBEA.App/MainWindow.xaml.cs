using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KBEA.App.Services;
using KBEA.App.ViewModels;
using KBEA.Core.Models;

namespace KBEA.App;

/// <summary>工作空间 TreeView 拖拽放置区域：Before=目标前、Into=嵌套进去、After=目标后。</summary>
internal enum DropTargetZone { Before, Into, After }

public partial class MainWindow : Window
{
    private TabItem? _dragSource;
    private Point _dragStart;
    private bool _autoSidebarCollapsed;
    private bool _syncingSidebarSelection;
    private TreeViewItem? _selectedLocalTreeItem;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        AssistantHost.MouseLeftButtonUp += (_, _) => viewModel.IsAssistantOpen = !viewModel.IsAssistantOpen;
        WindowChromeService.Attach(this);
        // 最小宽度 = 屏幕 1/3
        MinWidth = SystemParameters.PrimaryScreenWidth / 3.0;
        SizeChanged += OnWindowSizeChanged;
        MainTabs.SizeChanged += (_, _) => UpdateTabWidths();
        ViewModel.Tabs.CollectionChanged += (_, _) => UpdateTabWidths();
        Loaded += (_, _) => UpdateTabWidths();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        // 用 PreviewKeyDown 隧道事件拦截文件操作快捷键...
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    /// <summary>侧栏切换时，清空 WorkspaceTree 的视觉选中高亮。</summary>
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 视觉同步延迟到 Dispatcher：VM 侧「先清后设」会在同一个事件栈里发出两次变更，
        // 同步执行会与 WPF 选择状态机（TreeViewItem.IsSelected / ListBox.SelectedItem 的事件重入）打架，
        // 导致第一次点击的高亮被清掉。延迟执行保证最终视觉状态 = 最后一次 VM 赋值。
        if (e.PropertyName == nameof(MainViewModel.SelectedNode))
        {
            Dispatcher.BeginInvoke(new Action(() => SyncTreeSelection(ViewModel.SelectedNode)), System.Windows.Threading.DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedLocalFolder))
        {
            Dispatcher.BeginInvoke(new Action(() => SyncLocalSidebarSelection(ViewModel.SelectedLocalFolder)), System.Windows.Threading.DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedRemoteLocation))
        {
            Dispatcher.BeginInvoke(new Action(() => SyncRemoteSidebarSelection(ViewModel.SelectedRemoteLocation)), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>把 TreeView 的视觉选中同步到 VM 的选中节点；target 为 null 时清空全部选中。</summary>
    private void SyncTreeSelection(KBEA.App.ViewModels.WorkspaceNodeViewModel? target)
    {
        _syncingSidebarSelection = true;
        try
        {
            var found = target is not null && SelectTreeItem(WorkspaceTree, target);
            if (!found) ClearTreeViewSelection(WorkspaceTree);
        }
        finally { _syncingSidebarSelection = false; }
    }

    private static bool SelectTreeItem(ItemsControl parent, KBEA.App.ViewModels.WorkspaceNodeViewModel target)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            if (ReferenceEquals(container.DataContext, target) || item == target)
            {
                if (!container.IsSelected) container.IsSelected = true;
                return true;
            }
            if (SelectTreeItem(container, target)) return true;
        }
        return false;
    }

    /// <summary>把本地侧栏的选中项同步到标签切换带来的目标目录（不触发打开）。target 为 null 时清空选中。</summary>
    private void SyncLocalSidebarSelection(LocalFolderViewModel? target)
    {
        _syncingSidebarSelection = true;
        try
        {
            foreach (var list in new[] { QuickList, RecentList })
            {
                var current = list.SelectedItem as LocalFolderViewModel;
                // 已是目标（同一路径）：不动。逐项遍历设值会先清后设，造成"点击后高亮闪一下"的观感
                if (current is not null && target is not null && current.FullPath.Equals(target.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
                // 同一路径可能在 Quick/Recent 两个集合里各有一个实例（非同一引用），按路径匹配
                LocalFolderViewModel? matchInList = target is null
                    ? null
                    : list.Items.OfType<LocalFolderViewModel>().FirstOrDefault(x => x.FullPath.Equals(target.FullPath, StringComparison.OrdinalIgnoreCase));
                list.SelectedItem = matchInList;
            }
        }
        finally { _syncingSidebarSelection = false; }
    }

    /// <summary>把共享目录面板的选中项同步到当前标签绑定的远程位置（不触发打开）。target 为 null 时清空选中。</summary>
    private void SyncRemoteSidebarSelection(KBEA.Core.Models.RemoteLocationDefinition? target)
    {
        _syncingSidebarSelection = true;
        try
        {
            foreach (var list in new[] { RemoteListInLocal, RemoteListInShared })
            {
                var current = list.SelectedItem as KBEA.Core.Models.RemoteLocationDefinition;
                if (current is not null && target is not null && ReferenceEquals(current, target)) continue;
                KBEA.Core.Models.RemoteLocationDefinition? matchInList = target is null
                    ? null
                    : list.Items.OfType<KBEA.Core.Models.RemoteLocationDefinition>().FirstOrDefault(x => ReferenceEquals(x, target));
                list.SelectedItem = matchInList;
            }
        }
        finally { _syncingSidebarSelection = false; }
    }

    private static void ClearTreeViewSelection(TreeView tree)
    {
        foreach (var item in tree.Items.OfType<object>())
        {
            if (tree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
            {
                tvi.IsSelected = false;
                ClearTreeViewItemSelection(tvi);
            }
        }
    }

    private static void ClearTreeViewItemSelection(TreeViewItem parent)
    {
        foreach (var item in parent.Items.OfType<object>())
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
            {
                tvi.IsSelected = false;
                ClearTreeViewItemSelection(tvi);
            }
        }
    }

    /// <summary>
    /// 判断当前焦点元素是否是"文本输入类控件"（TextBox / ComboBox / RichTextBox）。
    /// 在这类控件里拦截 Ctrl+C/V/X 会破坏用户的文本编辑体验，所以放行给控件自己处理。
    /// DataGridCell 里可能正处于编辑模式（IsEditing），也需要放行。
    /// </summary>
    private static bool FocusIsEditingControl(DependencyObject? focused)
    {
        if (focused is null) return false;
        return focused is TextBox or ComboBox or RichTextBox;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var tab = ViewModel.SelectedTab;
        if (tab is null) return;

        var focused = Keyboard.FocusedElement as DependencyObject;
        if (FocusIsEditingControl(focused)) return; // 文本输入时放行

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        switch (e.Key)
        {
            case Key.C when ctrl && !shift && !alt:
                if (tab.CopyCommand.CanExecute(null)) { tab.CopyCommand.Execute(null); e.Handled = true; }
                break;
            case Key.X when ctrl && !shift && !alt:
                if (tab.CutCommand.CanExecute(null)) { tab.CutCommand.Execute(null); e.Handled = true; }
                break;
            case Key.V when ctrl && !shift && !alt:
                if (tab.PasteCommand.CanExecute(null)) { tab.PasteCommand.Execute(null); e.Handled = true; }
                break;
            case Key.A when ctrl && !shift && !alt:
                // 文件列表全选：焦点在文件表格内时由这里接管（右键菜单的「全选」走 FilesSelectAll_Click）
                if (FilesDataGrid.IsKeyboardFocusWithin)
                {
                    FilesDataGrid.SelectAll();
                    e.Handled = true;
                }
                break;
            case Key.Delete when !shift && !alt:
                // Delete → 放回收站（默认）
                if (tab.DeleteCommand.CanExecute(null)) { tab.DeleteCommand.Execute(null); e.Handled = true; }
                break;
            case Key.Delete when shift && !alt && !ctrl:
                // Shift+Delete → 永久删除（跳过回收站），通过带参数的命令传递
                if (tab.PermanentDeleteCommand.CanExecute(null)) { tab.PermanentDeleteCommand.Execute(null); e.Handled = true; }
                break;
            case Key.Delete when ctrl && !alt:
                // Ctrl+Delete → 删除整个文件夹（如果选中是文件），WPF 标准行为；
                // 如果选中是文件夹则直接删除，与普通 Delete 行为一致（都走回收站）
                if (tab.DeleteCommand.CanExecute(null)) { tab.DeleteCommand.Execute(null); e.Handled = true; }
                break;
            case Key.F2 when !ctrl && !alt:
                if (tab.RenameCommand.CanExecute(null)) { tab.RenameCommand.Execute(null); e.Handled = true; }
                break;
        }
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        if (width < 900)
        {
            if (ViewModel.IsSidebarVisible)
            {
                ViewModel.IsSidebarVisible = false;
                _autoSidebarCollapsed = true;
            }
        }
        else if (_autoSidebarCollapsed && width >= 1050)
        {
            ViewModel.IsSidebarVisible = true;
            _autoSidebarCollapsed = false;
        }
        else if (width >= 1050)
        {
            _autoSidebarCollapsed = false;
        }
    }

    private void ToolbarHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Dispatcher 延迟一帧，等按钮 ActualWidth 有值
        Dispatcher.BeginInvoke(new Action(UpdateToolbarOverflow), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateToolbarOverflow()
    {
        if (ToolbarPanel is null || ToolbarHost is null || ToolbarOverflowButton is null || ToolbarOverflowPanel is null) return;

        // 先假设所有主按钮都在可见区
        ToolbarOverflowButton.Visibility = Visibility.Collapsed;
        ToolbarOverflowPanel.Children.Clear();

        var mainButtons = new List<Button>();
        foreach (var child in ToolbarPanel.Children)
        {
            if (child is ToggleButton) continue;     // 跳过 OverflowButton
            if (child is ItemsControl) continue;       // 跳过插件区
            if (child is Button btn)
            {
                btn.Visibility = Visibility.Visible;
                mainButtons.Add(btn);
            }
        }

        if (mainButtons.Count == 0) return;

        // 确保所有按钮至少被 Measure 一次
        foreach (var btn in mainButtons)
        {
            if (btn.ActualWidth <= 0)
                btn.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        // 用 ActualWidth（优先）或 DesiredSize.Width 累加
        var totalWidth = mainButtons.Sum(b => b.ActualWidth > 0 ? b.ActualWidth : b.DesiredSize.Width);
        var available = ToolbarHost.ActualWidth;
        if (available <= 0 || totalWidth <= available + 2) return; // 留 2px 容差

        // 需要溢出：从最后一个开始移到溢出面板，直到放得下
        ToolbarOverflowButton.Visibility = Visibility.Visible;
        var overflowBtnWidth = ToolbarOverflowButton.ActualWidth > 0 ? ToolbarOverflowButton.ActualWidth : 28;
        var room = available - overflowBtnWidth - 2;

        // 从后往前移，但至少保留第 1 个（收藏文件夹）在主区
        for (int i = mainButtons.Count - 1; i >= 1 && totalWidth > room; i--)
        {
            var btn = mainButtons[i];
            btn.Visibility = Visibility.Collapsed;
            totalWidth -= (btn.ActualWidth > 0 ? btn.ActualWidth : btn.DesiredSize.Width);

            var overflowBtn = new Button
            {
                Content = btn.Content,
                Command = btn.Command,
                CommandParameter = btn.CommandParameter,
                ToolTip = btn.ToolTip,
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(2),
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            overflowBtn.Style = btn.FindResource("ToolbarButton") as Style ?? overflowBtn.Style;
            ToolbarOverflowPanel.Children.Insert(0, overflowBtn);
        }
    }

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindParent<Button>(source) is not null)
        {
            _dragSource = null;
            return;
        }
        _dragSource = FindParent<TabItem>(source);
        _dragStart = e.GetPosition(MainTabs);
    }

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null) return;
        var position = e.GetPosition(MainTabs);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var source = _dragSource;
        _dragSource = null;
        if (source.DataContext is MountTabViewModel tab)
            DragDrop.DoDragDrop(source, tab, DragDropEffects.Move);
    }

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(MountTabViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(MountTabViewModel)) is not MountTabViewModel sourceTab ||
            e.OriginalSource is not DependencyObject source) return;
        if (FindParent<TabItem>(source)?.DataContext is not MountTabViewModel targetTab || ReferenceEquals(sourceTab, targetTab)) return;

        var oldIndex = ViewModel.Tabs.IndexOf(sourceTab);
        var newIndex = ViewModel.Tabs.IndexOf(targetTab);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;
        ViewModel.Tabs.Move(oldIndex, newIndex);
        ViewModel.SelectedTab = sourceTab;
        e.Handled = true;
    }

    private void UpdateTabWidths()
    {
        var count = ViewModel.Tabs.Count;
        if (count == 0) return;
        // TabControl 内含 ScrollViewer，用 MainTabs 自身宽度做约束
        var available = MainTabs.ActualWidth;
        if (available <= 0) return;
        // 占位标签固定 36，其余标签均分剩余宽度，最小一个 icon 宽度。
        var rest = Math.Max(available - 36, count * 44);
        var width = Math.Clamp(rest / count - 2, 44, 240);
        var compact = width < 64;
        foreach (var item in MainTabs.Items)
        {
            if (item is not TabItem tab) continue;
            if (tab.DataContext is MountTabViewModel { IsPlaceholder: true }) { tab.Width = 36; continue; }
            tab.Width = width;
            tab.Tag = compact ? "compact" : null;
        }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl tabs) return;
        if (tabs.SelectedItem is not MountTabViewModel tab) return;
        if (tab.IsPlaceholder) { tabs.SelectedItem = ViewModel.SelectedTab; return; }
        ViewModel.SelectedTab = tab;
    }

    private void Tabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var scroller = source as ScrollViewer ?? FindParent<ScrollViewer>(source);
        if (scroller is null) return;
        e.Handled = true;
        scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - Math.Sign(e.Delta) * 120);
    }

    private void WorkspaceTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // 程序化同步（SyncTreeSelection 设 IsSelected）时不回写 VM，避免重入打开
        if (_syncingSidebarSelection) return;
        if (e.NewValue is WorkspaceNodeViewModel node) ViewModel.SelectedNode = node;
    }

    private void WorkspaceTreeArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 只处理 ScrollViewer 内部但不在 TreeViewItem 上的点击 → 取消选中
        if (e.OriginalSource is not DependencyObject source) return;
        // 如果点在 TreeView 内部，让 TreeView 自己处理（不要取消）
        var el = source as DependencyObject;
        while (el is not null)
        {
            if (el is TreeViewItem) return; // 点在节点上，保留 TreeView 选中
            if (el is TreeView) return;     // 点在 TreeView 自身（列表头空白），保留默认选中
            el = VisualTreeHelper.GetParent(el);
        }
        // 到这里说明点在 ScrollViewer 的 Border/StackPanel 空白区
        ClearWorkspaceTreeSelection();
    }

    private void WorkspaceTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 先记录拖拽起点（用于后续 PreviewMouseMove 判断）
        if (e.OriginalSource is DependencyObject source)
        {
            _treeDragItem = FindParent<TreeViewItem>(source);
            _treeDragNode = _treeDragItem?.DataContext as WorkspaceNodeViewModel;
            _treeDragStart = e.GetPosition(WorkspaceTree);
        }

        // 再处理已选中节点的 toggle 取消选中行为
        if (WorkspaceTree.SelectedItem is null) return;
        var clickedItem = _treeDragItem;
        if (clickedItem is null) return;
        var clickedNode = _treeDragNode;
        if (clickedNode is null) return;
        // 如果点的就是已选中的那个节点（且不是点在节点内的按钮上）→ 取消选中
        // 注意：不能吞掉节点内按钮（◈ 元数据、✎ 重命名）的点击，否则按钮永远收不到 Click
        if (ReferenceEquals(clickedNode, WorkspaceTree.SelectedItem)
            && (e.OriginalSource as DependencyObject) is not null
            && FindParent<Button>(e.OriginalSource as DependencyObject) is null)
        {
            e.Handled = true;
            clickedItem.IsSelected = false;
            ViewModel.SelectedNode = null;
        }
    }

    private void ClearWorkspaceTreeSelection()
    {
        if (WorkspaceTree.SelectedItem is null) { ViewModel.SelectedNode = null; return; }
        var container = (TreeViewItem?)WorkspaceTree.ItemContainerGenerator.ContainerFromItem(WorkspaceTree.SelectedItem);
        if (container is not null) container.IsSelected = false;
        ViewModel.SelectedNode = null;
    }

    private void WorkspaceTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) is not null) return;
        ViewModel.OpenNode(WorkspaceTree.SelectedItem as WorkspaceNodeViewModel);
    }

    private void LocalTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingSidebarSelection) return;
        if (e.NewValue is not LocalFolderViewModel node) return;
        ClearLocalSidebarSelection(clearQuick: true, clearRecent: true, clearTree: false);
        ViewModel.OpenLocal(node.FullPath);
    }

    private void LocalTree_ItemSelected(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender)) _selectedLocalTreeItem = (TreeViewItem)sender;
    }

    private void LocalLocationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSidebarSelection) return;
        if (sender is not ListBox { SelectedItem: LocalFolderViewModel node }) return;
        ClearLocalSidebarSelection(clearQuick: !ReferenceEquals(sender, QuickList), clearRecent: !ReferenceEquals(sender, RecentList), clearTree: true);
        ViewModel.OpenLocalLocationCommand.Execute(node.FullPath);
    }

    private void ClearLocalSidebarSelection(bool clearQuick, bool clearRecent, bool clearTree)
    {
        _syncingSidebarSelection = true;
        try
        {
            if (clearQuick && QuickList.SelectedIndex >= 0) QuickList.SelectedIndex = -1;
            if (clearRecent && RecentList.SelectedIndex >= 0) RecentList.SelectedIndex = -1;
            if (clearTree && _selectedLocalTreeItem is { IsSelected: true } item) item.IsSelected = false;
        }
        finally { _syncingSidebarSelection = false; }
    }

    private void RemoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSidebarSelection) return;
        if (sender is ListBox { SelectedItem: KBEA.Core.Models.RemoteLocationDefinition location })
            ViewModel.OpenRemoteLocationCommand.Execute(location);
    }

    /// <summary>点击团队共享条目 = 打开（取回内容；完整浏览用「＋我的」建立引用空间）。</summary>
    private void SharedSpace_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.SharedSpaceItem item })
            ViewModel.OpenTeamShareNodeCommand.Execute(item);
    }

    private void FavoriteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string path }) ViewModel.OpenFavoriteCommand.Execute(path);
    }

    private void LocalNode_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: LocalFolderViewModel node }) node.Expand();
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel node && ViewModel.SelectedTab == node.Owner) node.Owner.Navigate(node.FullPath);
    }

    private void FolderNode_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: FolderNodeViewModel node }) node.Expand();
    }

    private void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ViewModel.SelectedTab?.NavigateAddressCommand.Execute(null);
        e.Handled = true;
    }

    private void KnowledgeSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ViewModel.KnowledgeSearchCommand.Execute(null);
        e.Handled = true;
    }

    private void Files_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindParent<DataGridRow>(source) is null || FindParent<DataGridColumnHeader>(source) is not null) return;
        ViewModel.SelectedTab?.OpenCommand.Execute(null);
    }

    private void Catalog_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<DataGridRow>(source) is not null)
            ViewModel.OpenCatalogResultCommand.Execute(null);
    }

    private void AssistantThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var margin = AssistantHost.Margin;
        AssistantHost.Margin = new Thickness(0, 0,
            Math.Clamp(margin.Right - e.HorizontalChange, 8, Math.Max(8, ActualWidth - 400)),
            Math.Clamp(margin.Bottom - e.VerticalChange, 38, Math.Max(38, ActualHeight - 160)));
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    // === 内容面板 DataGrid 拖放 ===

    private Point _filesDragStart;
    private FileEntry? _filesDragSource;
    private bool _isFilesDragging;

    /// <summary>多选集合同步到当前 Tab VM（Copy/Cut/Delete 按多选批量执行）。</summary>
    private void FilesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedTab is { } tab)
            tab.SelectedFiles = FilesDataGrid.SelectedItems.OfType<FileEntry>().ToList();
    }

    private void FilesSelectAll_Click(object sender, RoutedEventArgs e) => FilesDataGrid.SelectAll();

    private void FilesDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _filesDragStart = e.GetPosition(FilesDataGrid);
        _filesDragSource = null;
        _isFilesDragging = false;
        if (e.OriginalSource is not DependencyObject source) return;
        var row = FindParent<DataGridRow>(source);
        if (row?.Item is FileEntry file) { _filesDragSource = file; return; }
        // 点在空白处（行外）：清除选中，与资源管理器行为一致
        if (FindParent<System.Windows.Controls.DataGridCell>(source) is null)
        {
            FilesDataGrid.SelectedItem = null;
            if (ViewModel.SelectedTab is { } tab) tab.SelectedFile = null;
        }
    }

    private void FilesDataGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isFilesDragging || e.LeftButton != MouseButtonState.Pressed || _filesDragSource is null) return;
        // 右键按住时（正要弹出/已弹出右键菜单）绝不启动 OLE 拖拽循环，
        // 否则菜单关闭后的鼠标释放会被 OLE 当作“放下”，触发一次意外的自复制 Drop
        if (e.RightButton == MouseButtonState.Pressed) return;
        var pos = e.GetPosition(FilesDataGrid);
        if (Math.Abs(pos.X - _filesDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _filesDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _isFilesDragging = true;
        var paths = new List<string> { _filesDragSource.FullPath };
        var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
        DragDrop.DoDragDrop(FilesDataGrid, data, DragDropEffects.Copy | DragDropEffects.Move);
        _isFilesDragging = false;
    }

    private void FilesDataGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        var targetTab = ViewModel.SelectedTab;
        if (targetTab is null || targetTab.IsComputer || !System.IO.Directory.Exists(targetTab.CurrentPath))
        {
            e.Effects = DragDropEffects.None;
        }
        else
        {
            e.Effects = e.KeyStates.HasFlag(DragDropKeyStates.LeftMouseButton) && e.KeyStates.HasFlag(DragDropKeyStates.ControlKey)
                ? DragDropEffects.Copy
                : e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey)
                    ? DragDropEffects.Move
                    : DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private void FilesDataGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Handled = true; return; }
        var targetTab = ViewModel.SelectedTab;
        if (targetTab is null) { e.Handled = true; return; }
        var dropped = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (dropped is null) { e.Handled = true; return; }
        targetTab.HandleFilesDropped(dropped);
        e.Handled = true;
    }

    // === 工作空间 TreeView 拖拽重排序 ===

    private Point _treeDragStart;
    private WorkspaceNodeViewModel? _treeDragNode;
    private TreeViewItem? _treeDragItem;
    private AdornerLayer? _treeAdornerLayer;
    private InsertionLineAdorner? _insertionAdorner;
    private HighlightAdorner? _highlightAdorner;
    private DropTargetZone _currentDropZone;
    private TreeViewItem? _currentDropTargetItem;

    private void WorkspaceTree_PreviewMouseLeftButtonDown_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        _treeDragItem = FindParent<TreeViewItem>(source);
        if (_treeDragItem is null) { _treeDragNode = null; return; }
        _treeDragNode = _treeDragItem.DataContext as WorkspaceNodeViewModel;
        _treeDragStart = e.GetPosition(WorkspaceTree);
    }

    private void WorkspaceTree_PreviewMouseMove_Drag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _treeDragNode is null) return;
        var pos = e.GetPosition(WorkspaceTree);
        if (Math.Abs(pos.X - _treeDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _treeDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var sourceNode = _treeDragNode;
        _treeDragNode = null;
        _treeDragItem = null;
        DragDrop.DoDragDrop(WorkspaceTree, sourceNode, DragDropEffects.Move);
        ClearAllAdorners();
    }

    private void WorkspaceTree_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(WorkspaceNodeViewModel)))
        {
            e.Effects = DragDropEffects.None;
            ClearAllAdorners();
            e.Handled = true;
            return;
        }

        var sourceNode = (WorkspaceNodeViewModel?)e.Data.GetData(typeof(WorkspaceNodeViewModel));

        if (e.OriginalSource is not DependencyObject source || FindParent<TreeViewItem>(source) is not { } targetItem)
        {
            // 拖到 TreeView 空白区域 → 脱离空间
            ClearAllAdorners();
            _currentDropZone = DropTargetZone.Into;
            _currentDropTargetItem = null;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        var targetNode = targetItem.DataContext as WorkspaceNodeViewModel;
        if (sourceNode is null || targetNode is null || ReferenceEquals(sourceNode, targetNode))
        {
            ClearAllAdorners();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (IsDescendant(sourceNode, targetNode))
        {
            ClearAllAdorners();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // 热区检测：鼠标在 TreeViewItem 上的 Y 相对位置
        var posInItem = e.GetPosition(targetItem);
        var itemHeight = targetItem.ActualHeight > 0 ? targetItem.ActualHeight : 24;
        var ratio = posInItem.Y / itemHeight;
        var zone = ratio switch
        {
            < 0.25 => DropTargetZone.Before,
            > 0.75 => DropTargetZone.After,
            _ => DropTargetZone.Into
        };

        // 只有 Directory 可以"Into"嵌套；Space 只有 直接 Children.Add 模式
        if (zone == DropTargetZone.Into && !CanAcceptChildren(targetNode))
        {
            zone = DropTargetZone.Before; // Space 目标不支持 Into，退化为 Before
        }

        _currentDropZone = zone;
        _currentDropTargetItem = targetItem;
        e.Effects = DragDropEffects.Move;

        // 选择视觉：Into → 高亮整个目标；Before/After → 插入线
        ClearAllAdorners();
        if (zone == DropTargetZone.Into)
            ShowHighlightAdorner(targetItem);
        else
            ShowInsertionAdorner(targetItem, sourceNode, zone);

        e.Handled = true;
    }

    private void WorkspaceTree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearAllAdorners();
        if (e.Data.GetData(typeof(WorkspaceNodeViewModel)) is not WorkspaceNodeViewModel sourceNode) return;

        if (_currentDropTargetItem?.DataContext is not WorkspaceNodeViewModel targetNode || _currentDropTargetItem is null)
        {
            // 拖到空白区域 → 脱离空间
            ViewModel.DetachWorkspaceNode(sourceNode);
            return;
        }

        if (ReferenceEquals(sourceNode, targetNode) || IsDescendant(sourceNode, targetNode)) return;

        ViewModel.ReorderWorkspaceNode(sourceNode, targetNode, _currentDropZone);
    }

    /// <summary>判断目标节点能否接受子节点（Directory 可以接受任何子，Space 只能接受 Directory 子）。</summary>
    private static bool CanAcceptChildren(WorkspaceNodeViewModel target)
    {
        return target.IsDirectory || target.IsSpace;
    }

    private static bool IsDescendant(WorkspaceNodeViewModel ancestor, WorkspaceNodeViewModel candidate)
    {
        foreach (var child in ancestor.Children)
        {
            if (ReferenceEquals(child, candidate)) return true;
            if (IsDescendant(child, candidate)) return true;
        }
        return false;
    }

    private void ShowInsertionAdorner(TreeViewItem targetItem, WorkspaceNodeViewModel sourceNode, DropTargetZone zone)
    {
        if (_treeAdornerLayer is null) _treeAdornerLayer = AdornerLayer.GetAdornerLayer(WorkspaceTree);
        if (_treeAdornerLayer is null) return;

        ClearAllAdorners();
        _insertionAdorner = new InsertionLineAdorner(targetItem, sourceNode, zone);
        _treeAdornerLayer.Add(_insertionAdorner);
    }

    private void ShowHighlightAdorner(TreeViewItem targetItem)
    {
        if (_treeAdornerLayer is null) _treeAdornerLayer = AdornerLayer.GetAdornerLayer(WorkspaceTree);
        if (_treeAdornerLayer is null) return;

        ClearAllAdorners();
        _highlightAdorner = new HighlightAdorner(targetItem);
        _treeAdornerLayer.Add(_highlightAdorner);
    }

    private void ClearAllAdorners()
    {
        if (_treeAdornerLayer is not null)
        {
            if (_insertionAdorner is not null) _treeAdornerLayer.Remove(_insertionAdorner);
            if (_highlightAdorner is not null) _treeAdornerLayer.Remove(_highlightAdorner);
        }
        _insertionAdorner = null;
        _highlightAdorner = null;
    }
}

/// <summary>TreeView 拖拽重排序的插入位置指示线（Adorner）。支持 Before（顶部）和 After（底部）。</summary>
internal sealed class InsertionLineAdorner : Adorner
{
    private readonly Pen _pen;
    private readonly DropTargetZone _zone;

    public InsertionLineAdorner(UIElement adornedElement, WorkspaceNodeViewModel sourceNode, DropTargetZone zone) : base(adornedElement)
    {
        _zone = zone;
        _pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0x79, 0x24)), 2);
        _pen.Freeze();
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (AdornedElement is not FrameworkElement adorned) return;
        var rect = new Rect(adorned.RenderSize);

        // Before = 顶部线, After = 底部线
        var y = _zone == DropTargetZone.After ? rect.Bottom - 1 : rect.Top + 1;
        dc.DrawLine(_pen, new Point(rect.Left, y), new Point(rect.Right, y));

        // 左右小三角
        var triangle = new StreamGeometry();
        using (var ctx = triangle.Open())
        {
            var tipY = y;
            var flapY = _zone == DropTargetZone.After ? y - 4 : y + 4;
            ctx.BeginFigure(new Point(rect.Left - 6, flapY), false, true);
            ctx.LineTo(new Point(rect.Left - 6, tipY), true, false);
            ctx.LineTo(new Point(rect.Left, tipY), true, false);
            ctx.Close();
        }
        dc.DrawGeometry(_pen.Brush, _pen, triangle);
    }

    protected override Size MeasureOverride(Size constraint) => AdornedElement.DesiredSize;
    protected override Size ArrangeOverride(Size finalSize) => finalSize;
}

/// <summary>TreeView 拖拽"放到目录内"的高亮指示（Adorner）。</summary>
internal sealed class HighlightAdorner : Adorner
{
    private static readonly Brush FillBrush = new SolidColorBrush(Color.FromArgb((byte)0x30, (byte)0xE8, (byte)0x79, (byte)0x24));
    private static readonly Pen BorderPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0x79, 0x24)), 1.5);

    static HighlightAdorner()
    {
        FillBrush.Freeze();
        BorderPen.Freeze();
    }

    public HighlightAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (AdornedElement is not FrameworkElement adorned) return;
        var rect = new Rect(adorned.RenderSize);
        // 圆角矩形高亮背景 + 边框
        var rounded = new RectangleGeometry(new Rect(rect.Left + 2, rect.Top + 1, rect.Width - 4, rect.Height - 2), 4, 4);
        dc.DrawGeometry(FillBrush, BorderPen, rounded);
    }

    protected override Size MeasureOverride(Size constraint) => AdornedElement.DesiredSize;
    protected override Size ArrangeOverride(Size finalSize) => finalSize;
}
