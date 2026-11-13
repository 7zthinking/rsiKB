using System.Windows;
using System.Windows.Media;

namespace KBEA.App.Views;

public partial class GitOutputWindow : Window
{
    public GitOutputWindow(string title, string subtitle)
    {
        InitializeComponent();
        HeadingText.Text = title;
        SubText.Text = subtitle;
    }

    /// <summary>追加一行到输出框，自动滚到底部。</summary>
    public void AppendLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLine(line));
            return;
        }
        OutputBox.AppendText(line + Environment.NewLine);
        OutputBox.ScrollToEnd();
    }

    /// <summary>设置最终结果：不动标题，旁边显示彩色状态；副标题改为最终消息；启用关闭按钮。</summary>
    public void SetResult(bool succeeded, string finalMessage)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetResult(succeeded, finalMessage));
            return;
        }
        ResultStatus.Text = succeeded ? "✓ 成功" : "✗ 失败";
        var brushKey = succeeded ? "SuccessBrush" : "WarningBrush";
        if (FindResource(brushKey) is Brush brush) ResultStatus.Foreground = brush;
        ResultStatus.Visibility = Visibility.Visible;
        SubText.Text = finalMessage;
        CloseButton.IsEnabled = true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(OutputBox.Text); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
