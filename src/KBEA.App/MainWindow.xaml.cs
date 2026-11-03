using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KBEA.App.Services;
using KBEA.App.ViewModels;

namespace KBEA.App;

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
        SizeChanged += OnWindowSizeChanged;
        MainTabs.SizeChanged += (_, _) => UpdateTabWidths();
        ViewModel.Tabs.CollectionChanged += (_, _) => UpdateTabWidths();
        Loaded += (_, _) => UpdateTabWidths();
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
        var available = MainTabs.ActualWidth;
        if (available <= 0) return;
        // 占位标签固定 36，其余标签均分剩余宽度，至少保留一个图标宽度。
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
        if (e.NewValue is WorkspaceNodeViewModel node) ViewModel.SelectedNode = node;
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
        if (sender is ListBox { SelectedItem: KBEA.Core.Models.RemoteLocationDefinition location })
            ViewModel.OpenRemoteLocationCommand.Execute(location);
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
}
