namespace KBEA.Core.Archive;

/// <summary>
/// 关键节点留档记录（rsync 归档插件产出）。
/// </summary>
public sealed record ArchiveRecord(
    string Id,
    string SpaceName,
    string NodeName,
    string Destination,
    string Note,
    DateTimeOffset CreatedAt,
    string Status);

/// <summary>
/// 归档操作契约：由 rsync 归档插件实现并经 <see cref="ArchiveOperationsProvider"/> 注册。
/// 宿主侧（关键节点命令、留档历史窗口）经 Provider 解析，插件缺失时为 null → 优雅降级。
/// </summary>
public interface IArchiveOperations
{
    /// <summary>
    /// 执行一次关键节点归档。进度文本通过 <paramref name="progress"/> 回调。
    /// </summary>
    Task<ArchiveRecord> CreateAsync(
        string spaceName,
        string spacePath,
        string note,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>取该空间的留档历史（按时间倒序）。</summary>
    IReadOnlyList<ArchiveRecord> GetHistory(string spacePath);
}

/// <summary>归档操作注册表：插件在 Init 时注册实现，宿主各消费点经 Instance 解析。</summary>
public static class ArchiveOperationsProvider
{
    public static IArchiveOperations? Instance { get; private set; }

    public static void Register(IArchiveOperations implementation) => Instance = implementation;
}
