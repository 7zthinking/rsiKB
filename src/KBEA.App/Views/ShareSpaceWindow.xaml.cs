using System.Windows;
using KBEA.Core.Models;
using KBEA.Core.Team;

namespace KBEA.App.Views;

/// <summary>共享空间对话框：选空间 + 选级别。</summary>
public partial class ShareSpaceWindow : Window
{
    public ShareSpaceWindow(IReadOnlyList<SpaceDefinition> spaces)
    {
        InitializeComponent();
        SpaceCombo.ItemsSource = spaces;
        SpaceCombo.SelectedIndex = 0;
    }

    public SpaceDefinition? SelectedSpace => SpaceCombo.SelectedItem as SpaceDefinition;

    public ShareLevel SelectedLevel => LevelCombo.SelectedIndex switch
    {
        0 => ShareLevel.Search,
        1 => ShareLevel.Read,
        2 => ShareLevel.Write,
        _ => ShareLevel.Read
    };

    private void OnShare(object sender, RoutedEventArgs e)
    {
        if (SelectedSpace is null)
        {
            MessageBox.Show(this, "请选择空间", "共享空间", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
