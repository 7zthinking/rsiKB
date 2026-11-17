using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using KBEA.Core.Models;

namespace KBEA.App.ViewModels;

public enum WorkspaceNodeKind { Directory, Space }

public sealed class WorkspaceNodeViewModel : ObservableObject
{
    public WorkspaceNodeViewModel(WorkspaceNodeKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    public WorkspaceNodeKind Kind { get; }
    public bool IsDirectory => Kind == WorkspaceNodeKind.Directory;
    public bool IsSpace => Kind == WorkspaceNodeKind.Space;
    public string Name { get; set; }
    public DirectoryDefinition? Directory { get; init; }
    public SpaceDefinition? Space { get; init; }
    public ObservableCollection<WorkspaceNodeViewModel> Children { get; } = [];
    public bool IsExpanded { get; set; } = true;
    public bool IsTeamLink => Kind == WorkspaceNodeKind.Space && Space?.IsTeamLink == true;
    public string Icon => Kind == WorkspaceNodeKind.Directory ? "◫" : IsTeamLink ? "⇄" : "▣";
    public string Badge => Kind == WorkspaceNodeKind.Space && Space is not null && !System.IO.Directory.Exists(Space.Path) ? "离线"
        : IsTeamLink ? "团队" : string.Empty;

    /// <summary>UIA/读屏等场景用节点名作为元素名（默认 ToString 会暴露类名）。</summary>
    public override string ToString() => Name;
}
