using System.Diagnostics;
using KBEA.Core.Archive;

namespace KBEA.Plugin.Rsync;

/// <summary>
/// rsync 关键节点归档实现。
/// 进程调用与归档历史存储都在插件内，宿主只负责触发命令与显示结果。
/// </summary>
public sealed class RsyncArchiveService : IArchiveOperations
{
    public async Task<ArchiveRecord> CreateAsync(
        string spaceName,
        string spacePath,
        string note,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = ArchiveSettings.Load();
        var executable = ResolveExecutable(settings.RsyncExecutable);
        if (!File.Exists(executable))
            throw new FileNotFoundException("未找到 rsync.exe。请在插件设置中指定便携版 rsync 路径。", executable);
        if (string.IsNullOrWhiteSpace(settings.ArchiveRoot))
            throw new InvalidOperationException("请先在插件设置中配置归档根目录。");
        if (!Directory.Exists(spacePath))
            throw new DirectoryNotFoundException("空间当前离线，无法归档。");

        var node = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var project = string.Concat(spaceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var current = CombineArchive(settings.ArchiveRoot, project, "current");
        var backup = CombineArchive(settings.ArchiveRoot, project, node);
        var source = Path.GetFullPath(spacePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-avz", "--partial", $"--backup-dir={backup}",
                     "--exclude=.git/", "--exclude=.7z/index/", "--exclude=*.tmp", "--exclude=*.temp",
                     source, current.TrimEnd('/', '\\') + "/"
                 })
            startInfo.ArgumentList.Add(argument);

        progress?.Report("正在启动 rsync 归档…");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 rsync。");
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(Math.Max(1, settings.TimeoutHours)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException($"rsync 归档已取消或超过 {settings.TimeoutHours} 小时时限。");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"rsync 归档失败，退出代码 {process.ExitCode}。");

        var record = new ArchiveRecord(Guid.NewGuid().ToString("N"), spaceName, node, current, note, DateTimeOffset.Now, "完成");
        new ArchiveStore(spacePath).Record(record);
        progress?.Report("关键节点归档完成。");
        return record;
    }

    public IReadOnlyList<ArchiveRecord> GetHistory(string spacePath) => new ArchiveStore(spacePath).GetHistory();

    private static string ResolveExecutable(string configured) => Path.IsPathRooted(configured)
        ? configured
        : Path.Combine(AppContext.BaseDirectory, configured);

    private static string CombineArchive(params string[] parts)
    {
        if (parts.Length == 0) return string.Empty;
        var isRemote = parts[0].Contains("://", StringComparison.Ordinal) || (parts[0].Contains(':') && !Path.IsPathRooted(parts[0]));
        return isRemote ? string.Join('/', parts.Select(x => x.Trim('/', '\\'))) : Path.Combine(parts);
    }
}
