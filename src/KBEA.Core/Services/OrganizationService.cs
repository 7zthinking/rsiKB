using KBEA.Core.Models;

namespace KBEA.Core.Services;

/// <summary>
/// 组织与共享服务：管理局域网内 KBEA 实例之间的组织关系。
/// UDP 发现逻辑暂时 stub 掉，保留 TODO 标记以便后续实现。
/// </summary>
public sealed class OrganizationService
{
    private static readonly Lazy<OrganizationService> _instance = new(() => new OrganizationService());
    public static OrganizationService Instance => _instance.Value;

    private readonly List<MachineInfo> _discoveredMachines = [];

    private OrganizationService() { }

    // ==================== 公开 API ====================

    /// <summary>基于当前配置创建一个新组织，创建者自动成为首个成员。</summary>
    public Organization CreateOrganization(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("组织名称不能为空。", nameof(name));

        var org = new Organization(
            Id: Guid.NewGuid(),
            Name: name,
            CreatorId: AppConfig.MachineId,
            Created: DateTime.Now,
            MemberIds: new List<string> { AppConfig.MachineId },
            IsPublic: false);

        AppConfig.Organizations.Add(org);
        Console.WriteLine($"[OrganizationService] 创建组织：{org.Name} ({org.Id})");
        return org;
    }

    /// <summary>通过名称或 ID 加入已有组织。</summary>
    public Organization? JoinOrganization(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
            return null;

        var org = AppConfig.Organizations.FirstOrDefault(o =>
            o.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase) ||
            o.Id.ToString().Equals(nameOrId, StringComparison.OrdinalIgnoreCase));

        if (org is null)
        {
            Console.WriteLine($"[OrganizationService] 未找到组织：{nameOrId}");
            return null;
        }

        if (!org.MemberIds.Contains(AppConfig.MachineId))
        {
            org.MemberIds.Add(AppConfig.MachineId);
            Console.WriteLine($"[OrganizationService] 已加入组织：{org.Name}");
        }
        return org;
    }

    /// <summary>列出指定组织的成员。</summary>
    public IReadOnlyList<string> ListMembers(Guid orgId)
    {
        var org = AppConfig.Organizations.FirstOrDefault(o => o.Id == orgId);
        return org?.MemberIds.AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>退出某个组织（从成员列表中移除本机 ID）。</summary>
    public bool LeaveOrganization(Guid orgId)
    {
        var org = AppConfig.Organizations.FirstOrDefault(o => o.Id == orgId);
        if (org is null) return false;

        bool removed = org.MemberIds.Remove(AppConfig.MachineId);
        if (removed)
            Console.WriteLine($"[OrganizationService] 已退出组织：{org.Name}");
        return removed;
    }

    /// <summary>返回本机已加入的全部组织。</summary>
    public IReadOnlyList<Organization> GetMyOrganizations() =>
        AppConfig.Organizations
            .Where(o => o.MemberIds.Contains(AppConfig.MachineId))
            .ToList()
            .AsReadOnly();

    // ==================== 发现（Stub） ====================

    /// <summary>本机 UDP 广播端口。</summary>
    public const int DiscoveryPort = 4719;

    /// <summary>
    /// 启动 UDP 发现：在 LAN 上广播自身信息并监听其他 KBEA 实例的响应。
    /// 当前为 stub 实现，仅记录日志；真实 UDP Socket 逻辑后续补齐。
    /// </summary>
    public void StartDiscovery()
    {
        // TODO(networking): 实现 UdpClient 收发。
        // - 广播 payload JSON: { machineId, machineName, port, organizations[] }
        // - 收到其他实例广播时把 MachineInfo 加入 _discoveredMachines
        Console.WriteLine($"[OrganizationService] StartDiscovery() stub — UDP port {DiscoveryPort} not bound yet.");
    }

    /// <summary>停止 UDP 发现。</summary>
    public void StopDiscovery()
    {
        // TODO(networking): 关闭 UdpClient，释放资源。
        Console.WriteLine("[OrganizationService] StopDiscovery() stub.");
    }

    /// <summary>返回当前 LAN 上已发现的 KBEA 实例列表。</summary>
    public IReadOnlyList<MachineInfo> GetDiscoveredMachines() => _discoveredMachines.AsReadOnly();

    // ==================== 共享辅助 ====================

    /// <summary>切换某个空间在指定组织中的共享开关。</summary>
    public void SetWorkspaceShare(Guid orgId, Guid workspaceId, bool allowShare)
    {
        var existing = AppConfig.OrganizationShares
            .FirstOrDefault(s => s.OrgId == orgId && s.WorkspaceId == workspaceId);

        if (existing is not null)
        {
            AppConfig.OrganizationShares.Remove(existing);
        }

        if (allowShare)
        {
            AppConfig.OrganizationShares.Add(new OrganizationShare(orgId, workspaceId, true));
        }
    }

    // ==================== AppConfig 注入 ====================

    /// <summary>当前活跃的应用配置（服务端在构造/调用前由宿主赋值）。</summary>
    public AppConfiguration AppConfig { get; private set; } = null!;

    /// <summary>由宿主（MainWindow / App）在使用前注入当前配置实例。</summary>
    public void AttachConfiguration(AppConfiguration config)
    {
        AppConfig = config ?? throw new ArgumentNullException(nameof(config));
    }
}
