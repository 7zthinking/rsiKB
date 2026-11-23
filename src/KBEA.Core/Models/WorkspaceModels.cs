using System.Text.Json.Serialization;

namespace KBEA.Core.Models;

public sealed class AppConfiguration
{
    public string MachineId { get; set; } = Guid.NewGuid().ToString();
    public string MachineName { get; set; } = Environment.UserName;
    public string AuthorName { get; set; } = Environment.UserName;
    public string AuthorEmail { get; set; } = $"{Environment.UserName}@localhost";
    public string Theme { get; set; } = "Light";
    public bool AutoDetect { get; set; } = true;
    public List<LlmEndpointConfig> LlmEndpoints { get; set; } = [];
    public List<string> FavoriteFolders { get; set; } = [];
    public List<string> RecentLocalFolders { get; set; } = [];
    public List<RemoteLocationDefinition> RemoteLocations { get; set; } = [];
    public List<SpaceDefinition> Spaces { get; set; } = [];
    public List<DirectoryDefinition> Directories { get; set; } = [];
    public List<Organization> Organizations { get; set; } = [];
    public List<OrganizationShare> OrganizationShares { get; set; } = [];

    // ---- 团队共享（共享设置页；改动重启后生效） ----
    /// <summary>团队文件内容缓存限额（GB，LRU 淘汰）。</summary>
    public double TeamCacheLimitGb { get; set; } = 2;
    /// <summary>团队 manifest 同步间隔（分钟）。</summary>
    public int TeamSyncIntervalMinutes { get; set; } = 5;
    /// <summary>团队共享服务起始端口（被占用时自动向后尝试）。</summary>
    public int TeamServicePort { get; set; } = 52235;

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

    /// <summary>空间类型：本地空间 / 团队引用空间（来自团队共享的目录）。</summary>
    public SpaceKind Kind { get; set; } = SpaceKind.Local;

    // ---- 团队引用空间（Kind = TeamLink）----
    public string TeamId { get; set; } = string.Empty;
    public string OwnerMachineCode { get; set; } = string.Empty;
    public Guid RemoteSpaceId { get; set; }

    /// <summary>团队引用空间：镜像在本地，但实体归团队 owner；禁止再共享给其他团队。</summary>
    public bool IsTeamLink => Kind == SpaceKind.TeamLink;

    /// <summary>是否允许共享给团队（团队引用空间不允许再共享）。</summary>
    public bool IsShareable => Kind == SpaceKind.Local;
}

public enum SpaceKind { Local, TeamLink }

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

/// <summary>LLM 端点配置。</summary>
public sealed class LlmEndpointConfig
{
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

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
    string DisplaySize,
    string LastWriteText);

public sealed record FileEntry(
    string Name,
    string FullPath,
    string RelativePath,
    bool IsDirectory,
    long Size,
    DateTime LastWriteTime,
    DateTime CreationTime = default,
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

/// <summary>单个文件的 Git 状态，用于"Git 状态及历史"面板上部。</summary>
public sealed record GitStatusEntry(string RelativePath, string State, bool IsStaged)
{
    public string StagingText => IsStaged ? "暂存区" : "工作区";
}

/// <summary>一次 Git 扫描的合并结果：仓库概览 + 各文件状态。</summary>
public sealed record GitSnapshot(GitStatusInfo Status, IReadOnlyDictionary<string, string> FileStates)
{
    public static GitSnapshot Empty { get; } = new(
        new GitStatusInfo(false, 0, string.Empty, string.Empty, 0),
        new Dictionary<string, string>());
}

/// <summary>HEAD 基线文件内容：用于版本比对。Found=false 表示 HEAD 中不存在（新文件）。</summary>
public sealed record HeadFileContent(bool Found, bool IsBinary, string Text)
{
    public static HeadFileContent Missing { get; } = new(false, false, string.Empty);
}

public sealed record SyncLog(DateTimeOffset Time, string Operation, string Status, string Message);

/// <summary>组织：LAN 上的一组共享工作空间的 KBEA 实例。</summary>
public sealed record Organization(
    Guid Id,
    string Name,
    string CreatorId,
    DateTime Created,
    List<string> MemberIds,
    bool IsPublic);

/// <summary>组织与空间之间的共享关系。</summary>
public sealed record OrganizationShare(
    Guid OrgId,
    Guid WorkspaceId,
    bool AllowShare);

/// <summary>局域网中另一台 KBEA 实例的发现信息。</summary>
public sealed record MachineInfo(
    string MachineId,
    string MachineName,
    string Endpoint,
    int Port);
