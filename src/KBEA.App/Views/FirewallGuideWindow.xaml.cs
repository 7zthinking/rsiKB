using System.Windows;
using KBEA.App.Services;

namespace KBEA.App.Views;

/// <summary>
/// 防火墙首跑引导：新建团队时若放行规则缺失则弹出。
/// DialogResult=true 表示规则已添加成功。
/// </summary>
public partial class FirewallGuideWindow : Window
{
    private readonly int _servicePort;

    public FirewallGuideWindow(int servicePort)
    {
        InitializeComponent();
        _servicePort = servicePort;
        PortRun.Text = $" {servicePort}";
        HintText.Text = HintText.Text
            .Replace("52235", servicePort.ToString());
    }

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        AllowButton.IsEnabled = false;
        AllowButton.Content = "等待管理员确认…";
        var ok = FirewallGuide.TryAddRules(_servicePort);
        if (ok)
        {
            MessageBox.Show(this, "防火墙规则已添加。其他设备现在可以发现本机了。", "防火墙放行",
                MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        else
        {
            MessageBox.Show(this, "添加规则未完成（可能拒绝了管理员确认）。你可以稍后在「团队管理」里重试，或手动添加放行规则。",
                "防火墙放行", MessageBoxButton.OK, MessageBoxImage.Warning);
            AllowButton.IsEnabled = true;
            AllowButton.Content = "一键放行";
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => FirewallGuide.OpenFirewallSettings();

    private void OnLater(object sender, RoutedEventArgs e) => DialogResult = false;
}
