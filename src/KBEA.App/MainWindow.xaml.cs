using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KBEA.App.ViewModels;

namespace KBEA.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        AssistantHost.MouseLeftButtonUp += (_, _) => viewModel.IsAssistantOpen = !viewModel.IsAssistantOpen;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void WorkspaceTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is WorkspaceNodeViewModel node) ViewModel.SelectedNode = node;
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel node && ViewModel.SelectedTab == node.Owner) node.Owner.Navigate(node.FullPath);
    }

    private void FolderNode_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: FolderNodeViewModel node }) node.Expand();
    }

    private void Files_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<DataGridColumnHeader>(source) is not null) return;
        ViewModel.SelectedTab?.OpenCommand.Execute(null);
    }

    private void Catalog_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ViewModel.OpenCatalogResultCommand.Execute(null);

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
