using KBEA.Core.Archive;
using KBEA.Plugin.Rsync;

namespace KBEA.Tests;

/// <summary>
/// rsync 归档插件的独立存储测试（插件自治数据库 .7z/archive.db，不依赖宿主 meta.db）。
/// </summary>
public class ArchiveStoreTests
{
    [Fact]
    public void RecordAndHistory_RoundTripInPluginOwnedDatabase()
    {
        using var directory = new TestDirectory();
        var mount = directory.SubDirectory("mount");

        var store = new ArchiveStore(mount);
        var record = new ArchiveRecord("1", "项目A", "20261030-180000", "D:/archive", "验收", DateTimeOffset.Now, "完成");
        store.Record(record);

        var history = store.GetHistory();
        Assert.Equal("验收", Assert.Single(history).Note);
        Assert.Equal("20261030-180000", history[0].NodeName);

        // 插件自有数据库独立于宿主 meta.db
        Assert.True(File.Exists(Path.Combine(mount, ".7z", "archive.db")));
    }

    [Fact]
    public void History_IsOrderedByCreationTimeDesc()
    {
        using var directory = new TestDirectory();
        var mount = directory.SubDirectory("mount");
        var store = new ArchiveStore(mount);

        store.Record(new ArchiveRecord("1", "空间", "20260101-000000", "D:/a", "旧", DateTimeOffset.UtcNow.AddDays(-1), "完成"));
        store.Record(new ArchiveRecord("2", "空间", "20260102-000000", "D:/b", "新", DateTimeOffset.UtcNow, "完成"));

        var history = store.GetHistory();
        Assert.Equal(2, history.Count);
        Assert.Equal("新", history[0].Note);
        Assert.Equal("旧", history[1].Note);
    }
}
