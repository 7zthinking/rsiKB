namespace KBEA.Core.Models;

public sealed class AppConfiguration
{
    public string AuthorName { get; set; } = Environment.UserName;
    public string AuthorEmail { get; set; } = $"{Environment.UserName}@localhost";
    public string Theme { get; set; } = "Light";
    public string RsyncExecutable { get; set; } = "tools/rsync/rsync.exe";
    public string ArchiveRoot { get; set; } = string.Empty;
    public bool AutoDetect { get; set; } = true;
    public List<WorkspaceDefinition> Workspaces { get; set; } = [];
}

public sealed class WorkspaceDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新工作空间";
    public List<MountDefinition> Mounts { get; set; } = [];
    public List<CategoryNode> Categories { get; set; } = [];
    public List<string> Favorites { get; set; } = [];
}

public sealed class MountDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public bool AutoSync { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
}

public sealed class CategoryNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<CategoryNode> Children { get; set; } = [];
}

public sealed record CatalogLink(
    Guid Id,
    Guid WorkspaceId,
    Guid CategoryId,
    string RelativePath,
    string FileHash,
    DateTimeOffset CreatedAt);

public sealed record CatalogResult(
    Guid MountId,
    string MountName,
    string MountPath,
    string RelativePath,
    string FullPath,
    string FileHash);

public sealed record FileEntry(
    string Name,
    string FullPath,
    string RelativePath,
    bool IsDirectory,
    long Size,
    DateTime LastWriteTime,
    string GitState = "")
{
    public string Kind => IsDirectory ? "文件夹" : string.IsNullOrWhiteSpace(Path.GetExtension(Name)) ? "文件" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
    public string DisplaySize => IsDirectory ? string.Empty : Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024d:F1} KB",
        < 1024 * 1024 * 1024 => $"{Size / 1024d / 1024d:F1} MB",
        _ => $"{Size / 1024d / 1024d / 1024d:F1} GB"
    };
}

public sealed record GitCommitInfo(string Id, string Message, string Author, DateTimeOffset Time);

public sealed record GitStatusInfo(bool IsRepository, int ChangeCount, string Branch, string RemoteUrl, int ConflictCount)
{
    public string Display => !IsRepository ? "未绑定 Git" : ConflictCount > 0 ? $"{ConflictCount} 个冲突" : ChangeCount > 0 ? $"{ChangeCount} 项变更 · {Branch}" : $"已同步 · {Branch}";
}

public sealed record ArchiveRecord(
    string Id,
    string MountName,
    string NodeName,
    string Destination,
    string Note,
    DateTimeOffset CreatedAt,
    string Status);

public sealed record SyncLog(DateTimeOffset Time, string Operation, string Status, string Message);
