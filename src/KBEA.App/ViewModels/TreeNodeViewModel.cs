using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using KBEA.Core.Models;

namespace KBEA.App.ViewModels;

public enum WorkspaceNodeKind { Workspace, Category, Mount }

public sealed class WorkspaceNodeViewModel : ObservableObject
{
    public WorkspaceNodeViewModel(WorkspaceDefinition workspace, WorkspaceNodeKind kind, string name)
    {
        Workspace = workspace;
        Kind = kind;
        Name = name;
    }

    public WorkspaceDefinition Workspace { get; }
    public WorkspaceNodeKind Kind { get; }
    public string Name { get; }
    public CategoryNode? Category { get; init; }
    public MountDefinition? Mount { get; init; }
    public ObservableCollection<WorkspaceNodeViewModel> Children { get; } = [];
    public bool IsExpanded { get; set; } = true;
    public string Icon => Kind switch { WorkspaceNodeKind.Workspace => "▣", WorkspaceNodeKind.Category => "◫", _ => "▤" };
    public string Badge => Kind == WorkspaceNodeKind.Mount && Mount is not null && !Directory.Exists(Mount.Path) ? "离线" : string.Empty;
}
