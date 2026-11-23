using KBEA.Core.Models;

namespace KBEA.Core.Git;

/// <summary>
/// Git 能力契约（P2 插件化）：接口留在 Core，实现搬进 kbea-plugin-git 插件。
/// 宿主（App/Worker）一律通过 <see cref="GitOperationsProvider"/> 解析实现；
/// 插件未加载时返回 null，Git 相关功能优雅降级（状态显示"不在仓库中"、按钮不注册）。
/// </summary>
public interface IGitOperations
{
    bool IsRepository(string path);

    string? DiscoverRepositoryRoot(string path);

    string CloneRepository(string remoteUrl, string localPath);

    GitStatusInfo BindRepository(string path, string remoteUrl, IProgress<string>? progress = null);

    GitStatusInfo GetStatus(string path);

    GitSnapshot GetSnapshot(string path);

    IReadOnlyList<GitCommitInfo> GetHistory(string path, int limit = 50);

    string SaveAndPush(string path, string message, string authorName, string authorEmail, IProgress<string>? progress = null);

    string Pull(string path, string authorName, string authorEmail, IProgress<string>? progress = null);

    IReadOnlyList<string> GetConflicts(string path);

    HeadFileContent GetHeadFileText(string path, string relativePath);

    void ResolveConflict(string path, string relativePath, string? resolvedText, bool keepWorktree);

    IReadOnlyList<GitStatusEntry> GetDetailedStatus(string path);
}

/// <summary>
/// Git 实现注册点：插件 Init 时调用 Register 注入实现，宿主各消费点经 Instance 取用。
/// 进程内单实现；重复 Register 以最后一次为准（重启加载，不会热切换）。
/// </summary>
public static class GitOperationsProvider
{
    public static IGitOperations? Instance { get; private set; }

    public static void Register(IGitOperations implementation) => Instance = implementation;
}

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
