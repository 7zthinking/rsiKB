using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using KBEA.Core.Team;

namespace KBEA.App.ViewModels;

/// <summary>侧栏「团队共享」面板：团队成员行。</summary>
public sealed class TeamMemberItem : ObservableObject
{
    public TeamMemberItem(MemberRecord member, bool online)
    {
        Member = member;
        Online = online;
    }

    public MemberRecord Member { get; }
    public bool Online { get; }
    public string Nickname => Member.Nickname;
    public string MachineDisplay => Member.MachineCode.Length >= 8 ? Member.MachineCode[..8].ToUpperInvariant() : Member.MachineCode;
    public string RoleText => Member.IsOwner ? "owner" : "成员";
    public string StatusDot => Online ? "●" : "○";
    public string StatusText => Online ? "在线" : "离线";
    public override string ToString() => $"{Nickname}（{RoleText}）";
}

/// <summary>侧栏/团队管理窗口：团队卡片。</summary>
public sealed class TeamCardItem : ObservableObject
{
    public TeamCardItem(TeamFolder team, bool isOwner, string ownerNickname, IReadOnlyList<TeamMemberItem> members)
    {
        Team = team;
        IsOwner = isOwner;
        OwnerNickname = ownerNickname;
        Members = new ObservableCollection<TeamMemberItem>(members);
    }

    public TeamFolder Team { get; }
    public string TeamId => Team.Definition.TeamId;
    public string Name => Team.Definition.Name;
    public bool IsOwner { get; }
    public string OwnerBadge => IsOwner ? "我是 owner" : string.Empty;
    /// <summary>底部操作按钮文案：owner 解散、成员退出。</summary>
    public string LeaveText => IsOwner ? "解散团队" : "退出团队";
    public string OwnerNickname { get; }
    public string OwnerText => $"owner：{OwnerNickname}{(IsOwner ? "（我）" : string.Empty)}";
    public string MemberSummary => $"{Members.Count} 名成员";
    public ObservableCollection<TeamMemberItem> Members { get; }
    public string ShareSummary => Team.MyShares.Count == 0 ? "尚未共享空间" : $"已共享 {Team.MyShares.Count} 个空间";
    public override string ToString() => Name;
}

/// <summary>侧栏「来自团队的共享」列表项。</summary>
public sealed class SharedSpaceItem : ObservableObject
{
    public SharedSpaceItem(SharedEntry entry)
    {
        Entry = entry;
    }

    public SharedEntry Entry { get; }
    /// <summary>true = 我创建的团队（点击打开本地空间）；false = 加入的团队（点击打开对端 WebDAV UNC 目录）。</summary>
    public bool FromMyTeam { get; set; }

    private bool _isSelected;
    /// <summary>侧栏行选中态（点击行时由 VM 统一置位）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(RowWeight));
                OnPropertyChanged(nameof(RowOpacity));
            }
        }
    }

    /// <summary>选中行加粗提示。</summary>
    public string RowWeight => IsSelected ? "SemiBold" : "Normal";
    /// <summary>未选中行略淡，选中行全黑。</summary>
    public string RowOpacity => IsSelected ? "1" : "0.82";

    public string Display => $"{Entry.OwnerNickname} · {Entry.SpaceName}";
    public string TeamName => Entry.TeamName;
    public string LevelText => Entry.LevelText;
    public bool Online => Entry.Online;
    public string StatusDot => Online ? "●" : "○";
    public string ToolTip => $"{Entry.TeamName} · {Entry.OwnerNickname} 的「{Entry.SpaceName}」 · {Entry.LevelText}{(Online ? string.Empty : " · owner 离线")}";
    public override string ToString() => Display;
}

/// <summary>侧栏团队共享树：团队 → 有共享的成员 → 该成员共享的空间。按我创建/我加入分组展示。</summary>
public sealed class TeamShareTeamNode
{
    public string TeamId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string MemberSummary { get; init; } = string.Empty;
    public List<TeamShareMemberNode> Members { get; init; } = [];
}

/// <summary>树第二层：有共享空间的成员机器。</summary>
public sealed class TeamShareMemberNode
{
    public string MachineCode { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public bool Online { get; init; }
    public string StatusDot => Online ? "●" : "○";
    public string StatusText => Online ? "在线" : "离线";
    public List<SharedSpaceItem> Shares { get; init; } = [];
}

/// <summary>团队搜索命中的来源引用（编码进 SearchHit.FullPath）。</summary>
public sealed class TeamHitRef
{
    public string OwnerMachineCode { get; set; } = string.Empty;
    public string OwnerNickname { get; set; } = string.Empty;
    public Guid SpaceId { get; set; }
    public string SpaceName { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public long Mtime { get; set; }
    public ShareLevel Level { get; set; }

    public const string Scheme = "kbea-team://";

    public string Encode() => Scheme + Uri.EscapeDataString(JsonSerializer.Serialize(this));

    public static TeamHitRef? Decode(string fullPath)
    {
        if (!fullPath.StartsWith(Scheme, StringComparison.Ordinal)) return null;
        try
        {
            return JsonSerializer.Deserialize<TeamHitRef>(Uri.UnescapeDataString(fullPath[Scheme.Length..]));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
