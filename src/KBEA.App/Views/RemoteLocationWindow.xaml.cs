using System.IO;
using System.Windows;
using KBEA.Core.Models;
using Microsoft.Win32;

namespace KBEA.App.Views;

public partial class RemoteLocationWindow : Window
{
    public RemoteLocationWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    public RemoteLocationDefinition? Value { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择已有工作副本或缓存目录", Multiselect = false };
        if (dialog.ShowDialog(this) == true) LocalPathBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var address = AddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(address))
        {
            MessageBox.Show(this, "请填写显示名称和远程地址。", "添加远程目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var kind = KindBox.SelectedIndex switch
        {
            1 => RemoteLocationKind.Rsync,
            2 => RemoteLocationKind.Network,
            _ => RemoteLocationKind.Git
        };
        var localPath = LocalPathBox.Text.Trim();
        if (kind != RemoteLocationKind.Network && string.IsNullOrWhiteSpace(localPath))
            localPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KBEA", "RemoteCache", Guid.NewGuid().ToString("N"));
        Value = new RemoteLocationDefinition { Name = name, Kind = kind, Address = address, LocalPath = localPath };
        DialogResult = true;
    }
}
