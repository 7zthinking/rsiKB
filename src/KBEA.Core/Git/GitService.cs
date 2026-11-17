using KBEA.Core.Models;
using KBEA.Core.Storage;
using LibGit2Sharp;

namespace KBEA.Core.Git;

public sealed class GitService
{
    public bool IsRepository(string path) => DiscoverRepositoryRoot(path) is not null;

    public string? DiscoverRepositoryRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? string.Empty;
        if (!Directory.Exists(path)) return null;
        try
        {
            var discovered = Repository.Discover(path);
            if (string.IsNullOrWhiteSpace(discovered)) return null;
            using var repository = new Repository(discovered);
            var root = repository.Info.WorkingDirectory;
            return string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
        }
        catch (LibGit2SharpException)
        {
            return null;
        }
    }

    public string CloneRepository(string remoteUrl, string localPath)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) throw new ArgumentException("远程仓库地址不能为空。", nameof(remoteUrl));
        if (Directory.Exists(localPath) && Directory.EnumerateFileSystemEntries(localPath).Any())
            throw new InvalidOperationException("克隆目录必须为空目录。");
        var parent = Path.GetDirectoryName(Path.GetFullPath(localPath));
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        Repository.Clone(remoteUrl, localPath);
        return Path.GetFullPath(localPath);
    }

    public GitStatusInfo BindRepository(string path, string remoteUrl, IProgress<string>? progress = null)
    {
        progress?.Report($"▶ 关联 Git 仓库：{path}");
        var root = DiscoverRepositoryRoot(path);
        if (root is null)
        {
            progress?.Report("→ 当前目录不在 Git 仓库中，正在初始化…");
            Repository.Init(path);
            root = Path.GetFullPath(path);
        }
        else
        {
            progress?.Report($"→ 已发现现有仓库：{root}");
        }
        var metadata = new MetadataStore(root);
        metadata.Initialize();
        using (var repository = new Repository(root))
        {
            EnsureMainBranch(repository);
            progress?.Report($"→ 主分支：{repository.Head.FriendlyName}");
            if (!string.IsNullOrWhiteSpace(remoteUrl))
            {
                SetRemote(repository, remoteUrl);
                progress?.Report($"→ 远程 origin 已配置：{remoteUrl}");
            }
            else
            {
                progress?.Report("→ 未提供远程地址，跳过 origin 配置。");
            }
        }
        metadata.SetConfigValue("git.remote", remoteUrl);
        var status = GetStatus(root);
        progress?.Report($"✓ 仓库就绪：{status.Display}");
        return status;
    }

    public GitStatusInfo GetStatus(string path)
    {
        var discovered = Directory.Exists(path) ? Repository.Discover(path) : null;
        if (string.IsNullOrWhiteSpace(discovered)) return new GitStatusInfo(false, 0, string.Empty, string.Empty, 0);
        using var repository = new Repository(discovered);
        var changes = repository.RetrieveStatus().Count(x => x.State != FileStatus.Unaltered && x.State != FileStatus.Ignored);
        return new GitStatusInfo(true, changes, repository.Head.FriendlyName,
            repository.Network.Remotes["origin"]?.Url ?? repository.Network.Remotes.FirstOrDefault()?.Url ?? string.Empty, repository.Index.Conflicts.Count());
    }

    /// <summary>
    /// 一次性取回仓库状态与文件状态。
    /// RetrieveStatus 会完整扫描索引与工作区，大仓库耗时数秒；
    /// 原先 GetStatus + GetFileStates 会各扫一遍，这里合并为一次扫描。
    /// </summary>
    public GitSnapshot GetSnapshot(string path)
    {
        var discovered = Directory.Exists(path) ? Repository.Discover(path) : null;
        if (string.IsNullOrWhiteSpace(discovered)) return GitSnapshot.Empty;
        using var repository = new Repository(discovered);
        var entries = repository.RetrieveStatus();
        var changes = entries.Count(x => x.State != FileStatus.Unaltered && x.State != FileStatus.Ignored);
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.State == FileStatus.Unaltered || entry.State == FileStatus.Ignored) continue;
            states[entry.FilePath.Replace('/', Path.DirectorySeparatorChar)] = FriendlyState(entry.State);
        }
        var status = new GitStatusInfo(true, changes, repository.Head.FriendlyName,
            repository.Network.Remotes["origin"]?.Url ?? repository.Network.Remotes.FirstOrDefault()?.Url ?? string.Empty, repository.Index.Conflicts.Count());
        return new GitSnapshot(status, states);
    }

    public IReadOnlyList<GitCommitInfo> GetHistory(string path, int limit = 50)
    {
        var discovered = Directory.Exists(path) ? Repository.Discover(path) : null;
        if (string.IsNullOrWhiteSpace(discovered)) return [];
        using var repository = new Repository(discovered);
        return repository.Commits.Take(limit).Select(x => new GitCommitInfo(x.Id.Sha[..Math.Min(8, x.Id.Sha.Length)], x.MessageShort, x.Author.Name, x.Author.When)).ToArray();
    }

    /// <summary>首选远程：origin，否则第一个远程。与 GetStatus 的远程查找保持一致。</summary>
    private static Remote? GetPrimaryRemote(Repository repository)
        => repository.Network.Remotes["origin"] ?? repository.Network.Remotes.FirstOrDefault();

    public string SaveAndPush(string path, string message, string authorName, string authorEmail, IProgress<string>? progress = null)
    {
        using var repository = OpenRepository(path);
        EnsureMainBranch(repository);
        Commands.Stage(repository, "*");
        var hasChanges = repository.RetrieveStatus().Any(x => x.State != FileStatus.Unaltered && x.State != FileStatus.Ignored);
        var signature = new Signature(authorName, authorEmail, DateTimeOffset.Now);
        var committed = false;
        if (hasChanges)
        {
            repository.Commit(string.IsNullOrWhiteSpace(message) ? $"保存资料 {DateTime.Now:yyyy-MM-dd HH:mm}" : message, signature, signature);
            committed = true;
            progress?.Report($"✓ 已提交本地变更：{message}");
        }
        else
        {
            progress?.Report("→ 没有需要保存的变更，跳过提交。");
        }
        var remote = GetPrimaryRemote(repository);
        if (remote is null) return committed ? "已保存到本地仓库；配置远程后可上传。" : "没有需要保存的变更。";
        if (!repository.Commits.Any()) return "尚无提交，未执行上传。";
        var branch = repository.Head.FriendlyName;

        // 上传走命令行 git：系统里的 url 重写规则与凭据助手才会生效。
        progress?.Report($"$ git push {remote.Name} {branch}:{branch}");
        var pushResult = GitCli.RunOrThrow(path, "保存并上传", progress,
            "-c", $"user.name={authorName}", "-c", $"user.email={authorEmail}",
            "push", remote.Name, $"{branch}:{branch}");

        repository.Branches.Update(repository.Head, updater => { updater.Remote = remote.Name; updater.UpstreamBranch = branch; });
        var pushText = pushResult.StandardOutput + "\n" + pushResult.StandardError;
        if (pushText.Contains("Everything up-to-date", StringComparison.OrdinalIgnoreCase))
            return committed ? "变更已保存；远程已是最新。" : "无需上传（远程已是最新）。";
        return committed ? "变更已保存并上传。" : "本地提交已上传。";
    }

    public string Pull(string path, string authorName, string authorEmail, IProgress<string>? progress = null)
    {
        using var repository = OpenRepository(path);
        var remote = GetPrimaryRemote(repository) ?? throw new InvalidOperationException("当前仓库尚未配置任何远程地址。");
        if (!repository.Commits.Any()) throw new InvalidOperationException("请先保存一个本地提交，再拉取更新。");
        var branch = repository.Head.FriendlyName;

        // 拉取走命令行 git，复用系统 url 重写与凭据助手。
        progress?.Report($"$ git pull --ff-only {remote.Name} {branch}");
        var result = GitCli.RunOrThrow(path, "拉取更新", progress,
            "pull", "--ff-only", remote.Name, branch);
        repository.Branches.Update(repository.Head, updater => { updater.Remote = remote.Name; updater.UpstreamBranch = branch; });
        var text = result.StandardOutput + "\n" + result.StandardError;
        if (text.Contains("Already up to date", StringComparison.OrdinalIgnoreCase)) return "无需更新（已是最新版本）。";
        if (text.Contains("Fast-forward", StringComparison.OrdinalIgnoreCase)) return "更新已拉取。";
        if (text.Contains("conflict", StringComparison.OrdinalIgnoreCase)) return "拉取产生冲突，请解决后再上传。";
        return "更新已合并。";
    }

    public IReadOnlyList<string> GetConflicts(string path)
    {
        var discovered = Directory.Exists(path) ? Repository.Discover(path) : null;
        if (string.IsNullOrWhiteSpace(discovered)) return [];
        using var repository = new Repository(discovered);
        return repository.Index.Conflicts.Select(x => x.Ancestor?.Path ?? x.Ours?.Path ?? x.Theirs?.Path ?? string.Empty).Where(x => x.Length > 0).Distinct().ToArray();
    }

    /// <summary>
    /// 读取 HEAD 中某文件的基线内容，用于冲突/变更版本比对。
    /// Found=false 表示 HEAD 中不存在（新文件）；IsBinary=true 表示二进制文件（含 NUL 字节），不返回文本。
    /// </summary>
    public HeadFileContent GetHeadFileText(string path, string relativePath)
    {
        var discovered = Directory.Exists(path) ? Repository.Discover(path) : null;
        if (string.IsNullOrWhiteSpace(discovered)) return HeadFileContent.Missing;
        using var repository = new Repository(discovered);
        var commit = repository.Head.Tip;
        if (commit is null) return HeadFileContent.Missing;
        var entry = commit[relativePath.Replace('\\', '/')];
        if (entry?.TargetType != TreeEntryTargetType.Blob) return HeadFileContent.Missing;
        var blob = (Blob)entry.Target;
        using var stream = blob.GetContentStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (Array.IndexOf(bytes, (byte)0) >= 0) return new HeadFileContent(true, true, string.Empty);
        return new HeadFileContent(true, false, System.Text.Encoding.UTF8.GetString(bytes));
    }

    /// <summary>
    /// 标记冲突已解决并提交（作者取自仓库 git config 的 user.name / user.email）。
    /// keepWorktree=true 直接以工作区内容为解决结果（二进制文件唯一可用分支）；
    /// keepWorktree=false 时先把 resolvedText 写回文件（"放弃工作区改动"传 HEAD 基线文本）。
    /// </summary>
    public void ResolveConflict(string path, string relativePath, string? resolvedText, bool keepWorktree)
    {
        var root = DiscoverRepositoryRoot(path) ?? throw new InvalidOperationException("当前目录不在 Git 仓库中。");
        var fullPath = Path.Combine(root, relativePath);
        if (!keepWorktree)
        {
            if (resolvedText is null) throw new InvalidOperationException("没有可写回的解决内容。");
            File.WriteAllText(fullPath, resolvedText);
        }
        using var repository = OpenRepository(root);
        Commands.Stage(repository, relativePath);
        var config = repository.Config;
        var signature = new Signature(
            config.Get<string>("user.name")?.Value ?? "KBEA",
            config.Get<string>("user.email")?.Value ?? "kbea@local",
            DateTimeOffset.Now);
        repository.Commit($"解决冲突：{relativePath}", signature, signature);
    }

    private static Repository OpenRepository(string path)
    {
        var discovered = Directory.Exists(path) ? Repository.Discover(path) : null;
        return !string.IsNullOrWhiteSpace(discovered)
            ? new Repository(discovered)
            : throw new InvalidOperationException("当前目录不在 Git 仓库中。");
    }

    private static void EnsureMainBranch(Repository repository)
    {
        if (repository.Head.FriendlyName.Equals("main", StringComparison.OrdinalIgnoreCase)) return;
        if (repository.Commits.Any()) repository.Branches.Rename(repository.Head, "main");
        else repository.Refs.UpdateTarget("HEAD", "refs/heads/main");
    }

    private static void SetRemote(Repository repository, string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return;
        var remote = repository.Network.Remotes["origin"];
        if (remote is null) repository.Network.Remotes.Add("origin", remoteUrl);
        else repository.Network.Remotes.Update(remote.Name, updater => updater.Url = remoteUrl);
    }

    public IReadOnlyList<GitStatusEntry> GetDetailedStatus(string path)
    {
        var discovered = Directory.Exists(path) ? Repository.Discover(path) : null;
        if (string.IsNullOrWhiteSpace(discovered)) return [];
        using var repository = new Repository(discovered);
        var list = new List<GitStatusEntry>();
        foreach (var entry in repository.RetrieveStatus())
        {
            // 跳过未变更和忽略的
            if (entry.State == FileStatus.Unaltered || entry.State == FileStatus.Ignored) continue;
            var relative = entry.FilePath.Replace('/', Path.DirectorySeparatorChar);
            var stagedStates = new HashSet<FileStatus>
            {
                FileStatus.NewInIndex, FileStatus.ModifiedInIndex, FileStatus.DeletedFromIndex,
                FileStatus.RenamedInIndex, FileStatus.TypeChangeInIndex
            };
            var unstagedStates = new HashSet<FileStatus>
            {
                FileStatus.NewInWorkdir, FileStatus.ModifiedInWorkdir, FileStatus.DeletedFromWorkdir,
                FileStatus.RenamedInWorkdir, FileStatus.TypeChangeInWorkdir
            };
            // 暂存区
            if (stagedStates.Contains(entry.State) || entry.State.HasFlag(FileStatus.Conflicted))
                list.Add(new GitStatusEntry(relative, FriendlyState(entry.State), true));
            // 工作区（可能同时存在暂存和未暂存的变更）
            if (unstagedStates.Contains(entry.State))
                list.Add(new GitStatusEntry(relative, FriendlyState(entry.State), false));
        }
        // 冲突单独加一次（IsStaged=true 表示"需要提交解决"）
        foreach (var entry in repository.RetrieveStatus())
        {
            if (entry.State.HasFlag(FileStatus.Conflicted))
            {
                var relative = entry.FilePath.Replace('/', Path.DirectorySeparatorChar);
                if (!list.Any(x => x.RelativePath == relative && x.State == "冲突"))
                    list.Add(new GitStatusEntry(relative, "冲突", true));
            }
        }
        return list.OrderBy(x => x.RelativePath).ToArray();
    }

    private static string FriendlyState(FileStatus state) => state switch
    {
        FileStatus.NewInIndex or FileStatus.NewInWorkdir => "新增",
        FileStatus.ModifiedInIndex or FileStatus.ModifiedInWorkdir => "已修改",
        FileStatus.DeletedFromIndex or FileStatus.DeletedFromWorkdir => "已删除",
        FileStatus.RenamedInIndex or FileStatus.RenamedInWorkdir => "重命名",
        FileStatus.TypeChangeInIndex or FileStatus.TypeChangeInWorkdir => "类型变更",
        FileStatus.Conflicted => "冲突",
        _ => state.ToString()
    };
}
