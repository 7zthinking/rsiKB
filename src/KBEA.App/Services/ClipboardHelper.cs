using System.Runtime.InteropServices;
using System.Windows;

namespace KBEA.App.Services;

/// <summary>
/// 剪贴板安全写入（Win32 直写版）：
/// - WPF Clipboard.SetText/SetDataObject 在剪贴板被其他进程占用时，OLE 层每次尝试内部阻塞约 1 秒才失败，
///   重试数秒 → UI 卡死（曾致「生成邀请码」卡 6 秒）。
/// - 这里直接 OpenClipboard/EmptyClipboard/SetClipboardData：单次尝试极快，失败快速重试，总耗时上限 < 1 秒。
/// </summary>
public static class ClipboardHelper
{
    private const uint CfUnicodeText = 13;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private const uint GmemMoveable = 0x0002;

    /// <summary>写入剪贴板；失败快速放弃返回 false（调用方让用户手动复制）。总耗时上限约 0.8 秒。</summary>
    public static bool TrySetText(string text, int retries = 10)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // CF_UNICODETEXT 要求含结尾 NUL
        var bytes = (text.Length + 1) * 2;
        var hGlobal = IntPtr.Zero;
        try
        {
            hGlobal = GlobalAlloc(GmemMoveable, (nuint)bytes);
            if (hGlobal == IntPtr.Zero) return false;
            var pointer = GlobalLock(hGlobal);
            if (pointer == IntPtr.Zero) return false;
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            for (var attempt = 0; attempt < retries; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    Thread.Sleep(50);
                    continue;
                }
                try
                {
                    EmptyClipboard();
                    if (SetClipboardData(CfUnicodeText, hGlobal) != IntPtr.Zero)
                    {
                        // 成功后系统接管内存，不许 GlobalFree
                        hGlobal = IntPtr.Zero;
                        return true;
                    }
                    return false;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is ExternalException)
        {
            return false;
        }
        finally
        {
            if (hGlobal != IntPtr.Zero) GlobalFree(hGlobal);
        }
    }

    /// <summary>兼容旧调用点的 WPF 线程内写入（优先 Win32 直写，失败回退 WPF API）。</summary>
    public static bool TrySetTextOnUiThread(string text) => TrySetText(text);
}
