using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KBEA.App.Services;

/// <summary>
/// 为自绘边框窗口补齐系统级效果：
/// 把最大化尺寸约束到显示器工作区，避免窗口底边被任务栏遮挡。
/// 阴影由 WindowChrome.GlassFrameThickness 负责（见 MainWindow.xaml）。
/// </summary>
internal sealed class WindowChromeService
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    private readonly Window _window;

    private WindowChromeService(Window window) => _window = window;

    public static void Attach(Window window)
    {
        var service = new WindowChromeService(window);
        window.SourceInitialized += service.OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(_window)!;
        source.AddHook(OnWindowMessage);
    }

    /// <summary>
    /// 重写 WM_GETMINMAXINFO，把最大尺寸设为工作区而不是整块屏幕。
    /// WindowChrome 在最大化时会把窗口按边框厚度向外扩张，底边因此压到任务栏下面。
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;
        ApplyWorkAreaBounds(hwnd, lParam);
        handled = true;
        return IntPtr.Zero;
    }

    private static void ApplyWorkAreaBounds(IntPtr handle, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var maxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        maxInfo.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        maxInfo.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        maxInfo.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        maxInfo.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        Marshal.StructureToPtr(maxInfo, lParam, fDeleteOld: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinSize;
        public Point CurrentSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect MonitorArea;
        public Rect WorkArea;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

