using System.Diagnostics;
using KBEA.Core.Models;
using KBEA.Core.Storage;

namespace KBEA.Core.Archive;

public sealed class ArchiveService
{
    public async Task<ArchiveRecord> CreateAsync(MountDefinition mount, AppConfiguration configuration, string note, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutable(configuration.RsyncExecutable);
        if (!File.Exists(executable)) throw new FileNotFoundException("未找到 rsync.exe。请在设置中指定便携版 rsync 路径。", executable);
        if (string.IsNullOrWhiteSpace(configuration.ArchiveRoot)) throw new InvalidOperationException("请先在设置中配置归档根目录。");
        if (!Directory.Exists(mount.Path)) throw new DirectoryNotFoundException("挂载文件夹当前离线，无法归档。");

        var node = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var project = string.Concat(mount.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var current = CombineArchive(configuration.ArchiveRoot, project, "current");
        var backup = CombineArchive(configuration.ArchiveRoot, project, node);
        var source = Path.GetFullPath(mount.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-avz", "--partial", $"--backup-dir={backup}", "--exclude=.git/", "--exclude=.7z/index/", "--exclude=*.tmp", "--exclude=*.temp", source, current.TrimEnd('/', '\\') + "/" })
            startInfo.ArgumentList.Add(argument);

        progress?.Report("正在启动 rsync 归档…");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 rsync。");
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("rsync 归档已取消或超过 2 小时时限。");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"rsync 归档失败，退出代码 {process.ExitCode}。");
        var record = new ArchiveRecord(Guid.NewGuid().ToString("N"), mount.Name, node, current, note, DateTimeOffset.Now, "完成");
        new MetadataStore(mount.Path).RecordArchive(record);
        progress?.Report("关键节点归档完成。");
        return record;
    }

    public IReadOnlyList<ArchiveRecord> GetHistory(string mountPath) => new MetadataStore(mountPath).GetArchives();

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
