using KBEA.Core.Git;
using LibGit2Sharp;

namespace KBEA.Tests;

public sealed class GitServiceTests
{
    [Fact]
    public void LocalRepository_CommitAndHistoryWork()
    {
        using var directory = new TestDirectory();
        var mount = directory.SubDirectory("mount");
        var service = new GitService();
        var status = service.BindRepository(mount, string.Empty);
        Assert.True(status.IsRepository);
        Assert.Equal("main", status.Branch);
        File.WriteAllText(Path.Combine(mount, "README.md"), "hello");

        var message = service.SaveAndPush(mount, "initial", "Tester", "tester@example.com");
        Assert.Contains("本地仓库", message);
        Assert.Equal(0, service.GetStatus(mount).ChangeCount);
        Assert.Equal("initial", Assert.Single(service.GetHistory(mount)).Message);
    }

    [Fact]
    public void RemoteRepository_PushAndPullRoundTripWorks()
    {
        using var directory = new TestDirectory();
        var remote = Path.Combine(directory.Path, "remote.git");
        Repository.Init(remote, isBare: true);
        using (var bare = new Repository(remote)) bare.Refs.UpdateTarget("HEAD", "refs/heads/main");
        var first = directory.SubDirectory("first");
        var service = new GitService();
        service.BindRepository(first, remote);
        File.WriteAllText(Path.Combine(first, "shared.txt"), "version 1");
        service.SaveAndPush(first, "v1", "Tester", "tester@example.com");

        var second = Path.Combine(directory.Path, "second");
        Repository.Clone(remote, second);
        service.BindRepository(second, remote);
        File.WriteAllText(Path.Combine(second, "shared.txt"), "version 2");
        service.SaveAndPush(second, "v2", "Tester", "tester@example.com");

        service.Pull(first, "Tester", "tester@example.com");
        Assert.Equal("version 2", File.ReadAllText(Path.Combine(first, "shared.txt")));
        Assert.Equal(2, service.GetHistory(first).Count);
    }
}
