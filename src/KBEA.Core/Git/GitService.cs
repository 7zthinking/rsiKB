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

    public GitStatusInfo BindRepository(string path, string remoteUrl)
    {
        var root = DiscoverRepositoryRoot(path);
        if (root is null)
        {
            Repository.Init(path);
            root = Path.GetFullPath(path);
        }
        var metadata = new MetadataStore(root);
        metadata.Initialize();
        using (var repository = new Repository(root))
        {
            EnsureMainBranch(repository);
            SetRemote(repository, remoteUrl);
        }
        metadata.SetConfigValue("git.remote", remoteUrl);
        return GetStatus(root);
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

    public IReadOnlyDictionary<string, string> GetFileStates(string path)
    {
        var discovered = Directory.Exists(path) ? Repository.Discover(path) : null;
        if (string.IsNullOrWhiteSpace(discovered)) return new Dictionary<string, string>();
        using var repository = new Repository(discovered);
        return repository.RetrieveStatus()
            .Where(x => x.State != FileStatus.Unaltered && x.State != FileStatus.Ignored)
            .ToDictionary(x => x.FilePath.Replace('/', Path.DirectorySeparatorChar), x => FriendlyState(x.State), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<GitCommitInfo> GetHistory(string path, int limit = 50)
    {
        var discovered = Directory.Exists(path) ? Repository.Discover(path) : null;
        if (string.IsNullOrWhiteSpace(discovered)) return [];
        using var repository = new Repository(discovered);
        return repository.Commits.Take(limit).Select(x => new GitCommitInfo(x.Id.Sha[..Math.Min(8, x.Id.Sha.Length)], x.MessageShort, x.Author.Name, x.Author.When)).ToArray();
    }

    public string SaveAndPush(string path, string message, string authorName, string authorEmail)
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
        }
        var remote = repository.Network.Remotes["origin"];
        if (remote is null) return committed ? "已保存到本地仓库；配置远程后可上传。" : "没有需要保存的变更。";
        if (!repository.Commits.Any()) return "尚无提交，未执行上传。";
        var branch = repository.Head;
        repository.Network.Push(remote, $"{branch.CanonicalName}:{branch.CanonicalName}");
        repository.Branches.Update(branch, updater => { updater.Remote = remote.Name; updater.UpstreamBranch = branch.CanonicalName; });
        return committed ? "变更已保存并上传。" : "本地提交已上传。";
    }

    public string Pull(string path, string authorName, string authorEmail)
    {
        using var repository = OpenRepository(path);
        var remote = repository.Network.Remotes["origin"] ?? throw new InvalidOperationException("当前仓库尚未配置 origin 远程地址。");
        if (!repository.Commits.Any()) throw new InvalidOperationException("请先保存一个本地提交，再拉取更新。");
        var branch = repository.Head;
        repository.Branches.Update(branch, updater => { updater.Remote = remote.Name; updater.UpstreamBranch = branch.CanonicalName; });
        var signature = new Signature(authorName, authorEmail, DateTimeOffset.Now);
        var result = Commands.Pull(repository, signature, new PullOptions());
        return result.Status == MergeStatus.UpToDate ? "已是最新版本。" : result.Status == MergeStatus.FastForward ? "更新已拉取。" : result.Status == MergeStatus.Conflicts ? "拉取产生冲突，请解决后再上传。" : "更新已合并。";
    }

    public IReadOnlyList<string> GetConflicts(string path)
    {
        var discovered = Directory.Exists(path) ? Repository.Discover(path) : null;
        if (string.IsNullOrWhiteSpace(discovered)) return [];
        using var repository = new Repository(discovered);
        return repository.Index.Conflicts.Select(x => x.Ancestor?.Path ?? x.Ours?.Path ?? x.Theirs?.Path ?? string.Empty).Where(x => x.Length > 0).Distinct().ToArray();
    }

    public void ResolveConflict(string path, string relativePath, string resolvedText, string authorName, string authorEmail)
    {
        var root = DiscoverRepositoryRoot(path) ?? throw new InvalidOperationException("当前目录不在 Git 仓库中。");
        File.WriteAllText(Path.Combine(root, relativePath), resolvedText);
        using var repository = OpenRepository(root);
        Commands.Stage(repository, relativePath);
        var signature = new Signature(authorName, authorEmail, DateTimeOffset.Now);
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
