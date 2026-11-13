namespace KBEA.App.Services;

/// <summary>前端诊断日志：统一写入 %TEMP%\kbea-crash.log。</summary>
public static class DiagLog
{
    private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kbea-crash.log");
    private static readonly object Gate = new();

    /// <summary>写一条带时间戳的日志；失败不影响业务。</summary>
    public static void Write(string message)
    {
        try
        {
            lock (Gate) File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>写异常日志（含完整堆栈）。</summary>
    public static void Exception(string tag, Exception ex)
    {
        try { Write($"{tag}: {ex}"); }
        catch { }
    }
}
