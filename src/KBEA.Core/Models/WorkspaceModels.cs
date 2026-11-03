using System.Text.Json.Serialization;

namespace KBEA.Core.Models;

public sealed class AppConfiguration
{
    public string AuthorName { get; set; } = Environment.UserName;
    public string AuthorEmail { get; set; } = $"{Environment.UserName}@localhost";
    public string Theme { get; set; } = "Light";
    public string RsyncExecutable { get; set; } = "tools/rsync/rsync.exe";
    public string ArchiveRoot { get; set; } = string.Empty;
    public bool AutoDetect { get; set; } = true;
    public List<string> FavoriteFolders { get; set; } = [];
    public List<string> RecentLocalFolders { get; set; } = [];
    public List<RemoteLocationDefinition> RemoteLocations { get; set; } = [];
    public List<SpaceDefinition> Spaces { get; set; } = [];
    public List<DirectoryDefinition> Directories { get; set; } = [];

    /// <summary>旧结构（空间装目录），仅用于一次性迁移；迁移完成后会被清空。</summary>
    public List<WorkspaceDefinition> Workspaces { get; set; } = [];

    [JsonIgnore] public Dictionary<Guid, Guid> LegacyDirectoryIdMap { get; set; } = [];
}

/// <summary>空间：一个物理位置（本地文件夹、Git 仓库、rsync 目录或网络位置）。</summary>
public sealed class SpaceDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新空间";
    public string Path { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public RemoteLocationKind RemoteKind { get; set; } = RemoteLocationKind.Network;
    public Guid? DirectoryId { get; set; }
    public bool AutoSync { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public bool IsRemote => !string.IsNullOrWhiteSpace(RemoteUrl);
}

/// <summary>目录：空间的逻辑分组，可嵌套。空间归属某个目录，未归属的显示在根层。</summary>
public sealed class DirectoryDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新目录";
    public Guid? ParentId { get; set; }
}

/// <summary>旧结构，仅在迁移时读取。</summary>
public sealed class WorkspaceDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新工作空间";
    public List<MountDefinition> Mounts { get; set; } = [];
    public List<CategoryNode> Categories { get; set; } = [];
    public List<string> Favorites { get; set; } = [];
}

/// <summary>旧结构，仅在迁移时读取。</summary>
public sealed class MountDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public bool AutoSync { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
}

public enum RemoteLocationKind { Git, Rsync, Network }

public sealed class RemoteLocationDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public RemoteLocationKind Kind { get; set; }
    public string Address { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
}

/// <summary>旧结构，仅在迁移时读取。</summary>
public sealed class CategoryNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<CategoryNode> Children { get; set; } = [];
}

public sealed record CatalogLink(
    Guid Id,
    Guid SpaceId,
    Guid DirectoryId,
    string RelativePath,
    string FileHash,
    DateTimeOffset CreatedAt);

public sealed record CatalogResult(
    Guid SpaceId,
    string SpaceName,
    string SpacePath,
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
    public string Display => !IsRepository ? "当前目录不在 Git 仓库中" : ConflictCount > 0 ? $"{ConflictCount} 个冲突" : ChangeCount > 0 ? $"{ChangeCount} 项变更 · {Branch}" : $"已同步 · {Branch}";
}

public sealed record ArchiveRecord(
    string Id,
    string SpaceName,
    string NodeName,
    string Destination,
    string Note,
    DateTimeOffset CreatedAt,
    string Status);

public sealed record SyncLog(DateTimeOffset Time, string Operation, string Status, string Message);
