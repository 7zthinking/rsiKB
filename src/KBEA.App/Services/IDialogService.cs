using KBEA.Core.Models;

namespace KBEA.App.Services;

public sealed record SpaceDialogResult(string Name, string Path = "", string RemoteUrl = "", RemoteLocationKind RemoteKind = RemoteLocationKind.Network, Guid? DirectoryId = null, bool DeleteRequested = false);
public sealed record GitDialogResult(string RepositoryPath, string RemoteUrl);

public interface IDialogService
{
    void SetDirectories(List<DirectoryDefinition> directories);
    string? Prompt(string title, string label, string defaultValue = "");
    string? SelectFolder(string title);
    void Info(string message, string title = "知识库自进化助手");
    bool Confirm(string message, string title = "请确认");
    bool ShowSettings(AppConfiguration configuration);
    SpaceDialogResult? ShowNewSpace();
    SpaceDialogResult? ShowEditSpace(string name);
    RemoteLocationDefinition? ShowRemoteLocation();
    GitDialogResult? ShowGitRepository(string currentPath);
    void ShowHistory(IEnumerable<GitCommitInfo> commits, string title);
    void ShowArchives(IEnumerable<ArchiveRecord> records, string title);
}
