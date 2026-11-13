using System.Diagnostics;
using System.Text;

namespace KBEA.Core.Git;

/// <summary>命令行 git 调用结果。</summary>
public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
    public string Output => string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
}

/// <summary>命令行 git 执行失败时抛出，消息已翻译为中文。</summary>
public sealed class GitCommandException(string message, GitCommandResult result) : Exception(message)
{
    public GitCommandResult Result { get; } = result;
}

/// <summary>
/// 通过命令行执行 git。系统里的 url 重写规则（insteadOf / pushInsteadOf）、
/// 凭据助手（GitHub CLI / 凭据管理器）和代理设置都会被 git 自动继承。
/// </summary>
public static class GitCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>无 progress 回调的重载：一次性读完输出（与旧调用兼容）。</summary>
    public static GitCommandResult Run(string workingDirectory, params string[] arguments)
        => Run(workingDirectory, null, arguments);

    /// <summary>带可选 progress 回调：stdout / stderr 按行实时推送，最后返回完整输出。</summary>
    public static GitCommandResult Run(string workingDirectory, IProgress<string>? progress, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) throw new GitCommandException("无法启动 git 命令行工具。", new GitCommandResult(-1, string.Empty, string.Empty));

            var stdoutSb = new StringBuilder();
            var stderrSb = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stdoutSb.AppendLine(e.Data);
                progress?.Report(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stderrSb.AppendLine(e.Data);
                progress?.Report(e.Data); // stderr 也实时显示，git 把大量常规信息打到 stderr
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new GitCommandException("git 命令执行超时。", new GitCommandResult(-1, stdoutSb.ToString(), stderrSb.ToString()));
            }
            // 让异步读取 flush 完
            process.WaitForExit();

            return new GitCommandResult(process.ExitCode, stdoutSb.ToString().TrimEnd('\r', '\n'), stderrSb.ToString().TrimEnd('\r', '\n'));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new GitCommandException("未找到 git 命令行工具，请确认 git 已安装并加入 PATH。", new GitCommandResult(-1, string.Empty, ex.Message));
        }
    }

    public static GitCommandResult RunOrThrow(string workingDirectory, string action, IProgress<string>? progress, params string[] arguments)
    {
        var result = Run(workingDirectory, progress, arguments);
        if (result.Succeeded) return result;
        throw new GitCommandException(DescribeFailure(action, result), result);
    }

    private static string DescribeFailure(string action, GitCommandResult result)
    {
        var detail = result.Output.Trim();
        if (detail.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return $"{action}失败：远程仓库身份验证未通过。\n\n"
                 + "请先在命令行对该仓库执行一次 git push 完成登录（凭据会保存到 Git 凭据管理器），再回到这里重试。";
        if (detail.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("unable to access", StringComparison.OrdinalIgnoreCase))
            return $"{action}失败：无法连接远程仓库。\n\n{detail}";
        if (detail.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("rejected", StringComparison.OrdinalIgnoreCase))
            return $"{action}失败：远程仓库有本地没有的提交，请先执行\"拉取更新\"再上传。\n\n{detail}";
        return $"{action}失败：{detail}";
    }
}
