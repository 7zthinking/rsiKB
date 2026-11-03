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
    public string Icon => Kind == WorkspaceNodeKind.Directory ? "◫" : "▣";
    public string Badge => Kind == WorkspaceNodeKind.Space && Space is not null && !System.IO.Directory.Exists(Space.Path) ? "离线" : string.Empty;
}
