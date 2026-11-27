using System.Windows;
using System.Windows.Controls;
using KBEA.App.Services;

namespace KBEA.App.Views;

/// <summary>
/// 更新包下载窗口：进度条 + 暂停按钮。暂停/中断保留已下载内容（断点续传），
/// 断点位置由调用方写入 UpdateState 持久化，下次「检查更新」时提示继续。
/// </summary>
public sealed class UpdateDownloadWindow : Window
{
    private readonly TextBlock _statusText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10)
    };

    private readonly ProgressBar _progress = new() { Height = 6, Minimum = 0, Maximum = 100 };

    private readonly CancellationTokenSource _cts = new();

    /// <summary>下载是否完成（false = 用户暂停/中断或出错，保留断点）。</summary>
    public bool Completed { get; private set; }

    /// <param name="resumeFrom">断点偏移（字节）；0 表示全新下载。</param>
    public UpdateDownloadWindow(string versionLabel, string url, string zipPath, long resumeFrom)
    {
        Title = $"下载更新 {versionLabel}";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var pauseButton = new Button
        {
            Content = "暂停",
            Width = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        pauseButton.Click += (_, _) => _cts.Cancel();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { _statusText, _progress, pauseButton }
        };

        Loaded += async (_, _) => await RunAsync(url, zipPath, resumeFrom);
    }

    private async Task RunAsync(string url, string zipPath, long resumeFrom)
    {
        try
        {
            await UpdateService.DownloadAsync(url, zipPath, resumeFrom,
                (current, total) => Dispatcher.Invoke(() =>
                {
                    if (total > 0)
                    {
                        _progress.IsIndeterminate = false;
                        _progress.Value = current * 100.0 / total;
                        _statusText.Text = $"已下载 {FormatBytes(current)} / {FormatBytes(total)}（{current * 100 / total:0}%）。";
                    }
                    else
                    {
                        _progress.IsIndeterminate = true;
                        _statusText.Text = $"已下载 {FormatBytes(current)}…";
                    }
                }),
                _cts.Token);

            Completed = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            // 用户暂停：保留断点，断点位置由调用方持久化
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"下载失败：{ex.Message}", "下载更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1048576 => $"{bytes / 1048576.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B"
    };
}
