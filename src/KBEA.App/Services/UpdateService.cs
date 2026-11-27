using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace KBEA.App.Services;

/// <summary>更新流程阶段（持久化保存，应用重启后仍可恢复）。</summary>
public enum UpdatePhase
{
    /// <summary>无更新流程。</summary>
    None,

    /// <summary>远程有新版本，尚未开始下载。</summary>
    Available,

    /// <summary>下载中断，可从断点继续。</summary>
    Paused,

    /// <summary>安装包已下载完成，等待启动更新。</summary>
    Downloaded,

    /// <summary>更新脚本已启动，应用即将退出。</summary>
    Updating
}

/// <summary>更新流程状态快照（持久化到 %LOCALAPPDATA%\KBEA\Update\state.json）。</summary>
public sealed class UpdateState
{
    public UpdatePhase Phase { get; set; } = UpdatePhase.None;

    /// <summary>目标版本 tag（如 v0.5.0）。</summary>
    public string? TargetVersion { get; set; }

    public bool IsPrerelease { get; set; }

    /// <summary>安装包下载地址（GitHub release asset）。</summary>
    public string? AssetUrl { get; set; }

    /// <summary>GitHub 发布页地址（找不到自动更新包时引导手动下载）。</summary>
    public string? ReleaseUrl { get; set; }

    /// <summary>本地安装包路径。</summary>
    public string? ZipPath { get; set; }

    public long DownloadedBytes { get; set; }

    public long TotalBytes { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 应用内更新：下载安装包（支持断点续传）、持久化流程状态、生成并启动更新脚本。
/// 更新脚本（update.ps1）负责：等待应用退出 → 解压覆盖安装目录（保留 branding/ 本机自定义）
/// → 清理临时文件 → 重新启动应用。
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "rsiKB-update" } }
    };

    /// <summary>更新工作目录：下载包、状态文件、更新脚本都放在这里。</summary>
    public static string UpdateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KBEA", "Update");

    private static string StatePath => Path.Combine(UpdateDir, "state.json");

    private static string ScriptPath => Path.Combine(UpdateDir, "update.ps1");

    /// <summary>某版本安装包的本地存放路径。</summary>
    public static string GetZipPath(string tag) => Path.Combine(UpdateDir, $"KBEA-{tag}-win-x64.zip");

    // ==================== 状态持久化 ====================

    public static UpdateState? LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StatePath));
        }
        catch (Exception ex)
        {
            DiagLog.Exception("读取更新状态失败", ex);
            return null;
        }
    }

    public static void SaveState(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(UpdateDir);
            state.UpdatedAt = DateTimeOffset.Now;
            File.WriteAllText(StatePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            DiagLog.Exception("保存更新状态失败", ex);
        }
    }

    public static void ClearState()
    {
        try { File.Delete(StatePath); }
        catch { /* 清理失败无碍 */ }
    }

    // ==================== 下载（断点续传） ====================

    /// <summary>
    /// 下载安装包。<paramref name="resumeFrom"/> &gt; 0 时发送 Range 头从断点续传；
    /// 服务端不支持 Range（返回 200 全量）时自动从头重写。
    /// onProgress(current, total)：total 为 -1 表示大小未知。
    /// </summary>
    public static async Task DownloadAsync(string url, string zipPath, long resumeFrom,
        Action<long, long> onProgress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // 服务端确认续传（206 Partial Content）才追加写入；否则当作全量从头重写
        var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        long total;
        if (resumed && response.Content.Headers.ContentRange?.Length is { } rangeLength)
        {
            total = rangeLength;
        }
        else
        {
            resumed = false;
            resumeFrom = 0;
            total = response.Content.Headers.ContentLength ?? -1;
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(zipPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write);
        var buffer = new byte[81920];
        var downloaded = target.Length;
        onProgress(downloaded, total);
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            onProgress(downloaded, total);
        }
    }

    // ==================== 更新脚本 ====================

    /// <summary>生成 update.ps1：等待应用退出 → 解压覆盖安装目录 → 清理 → 重启应用。</summary>
    public static void WriteUpdaterScript(string zipPath)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // PS 单引号字符串转义（路径可能含撇号）
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";

        // 注意：$$ 内插原始字符串——{{ }} 是内插语法，PS 大括号用单个字面量 { }
        var script = $$"""
            # KBEA 自动更新脚本（由应用生成）：解压覆盖安装目录后重启应用。
            $ErrorActionPreference = 'Stop'
            $installDir = {{Q(installDir)}}
            $zipPath = {{Q(zipPath)}}
            $statePath = {{Q(StatePath)}}

            # 1) 等待主程序与后台服务退出（最多 30 秒，超时强制结束）
            foreach ($name in @('KBEA', 'KBEA.Worker')) {
                $deadline = (Get-Date).AddSeconds(30)
                while (Get-Process -Name $name -ErrorAction SilentlyContinue) {
                    if ((Get-Date) -gt $deadline) { Stop-Process -Name $name -Force -ErrorAction SilentlyContinue; break }
                    Start-Sleep -Milliseconds 300
                }
            }

            # 2) 解压更新包（CI 产物以安装内容为根；兼容带一层目录的包）
            $temp = Join-Path $env:TEMP ('kbea-update-' + [guid]::NewGuid().ToString('N'))
            Expand-Archive -Path $zipPath -DestinationPath $temp -Force
            $source = Join-Path $temp 'KBEA-Portable'
            if (-not (Test-Path $source)) { $source = $temp }

            # 3) 覆盖程序文件；branding/ 是本机固化配置（logo/标题/主题色），保留不动
            Get-ChildItem -LiteralPath $source -Force | ForEach-Object {
                if ($_.Name -eq 'branding') { return }
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $installDir $_.Name) -Recurse -Force
            }

            # 4) 清理临时文件与更新状态，重启应用
            Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
            Start-Process -FilePath (Join-Path $installDir 'KBEA.exe') -WorkingDirectory $installDir
            """;

        Directory.CreateDirectory(UpdateDir);
        File.WriteAllText(ScriptPath, script);
    }

    /// <summary>启动更新脚本（独立进程，应用随即可退出）；失败返回 false。</summary>
    public static bool LaunchUpdater(string zipPath)
    {
        try
        {
            WriteUpdaterScript(zipPath);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ScriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            Process.Start(psi);
            DiagLog.Write("更新脚本已启动，应用即将退出。");
            return true;
        }
        catch (Exception ex)
        {
            DiagLog.Exception("启动更新脚本失败", ex);
            return false;
        }
    }
}
