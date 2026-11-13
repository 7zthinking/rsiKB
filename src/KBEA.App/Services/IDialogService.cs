using KBEA.Core.Models;

namespace KBEA.App.Services;

public sealed record SpaceDialogResult(string Name, string Path = "", string RemoteUrl = "", RemoteLocationKind RemoteKind = RemoteLocationKind.Network, Guid? DirectoryId = null, bool DeleteRequested = false);
public sealed record GitDialogResult(string RepositoryPath, string RemoteUrl);

/// <summary>Git 操作进度面板：Begin 返回一个 IGitProgress，后台 Report 实时追加到面板，结束时 Done 设最终状态。</summary>
public interface IGitProgress : IProgress<string>, IDisposable
{
    void Done(bool succeeded, string finalMessage);
}

public interface IDialogService
{
    void SetDirectories(List<DirectoryDefinition> directories);
    string? Prompt(string title, string label, string defaultValue = "");
    string? SelectFolder(string title);
    void Info(string message, string title = "知识库自进化助手");
    bool Confirm(string message, string title = "请确认");
    bool ShowSettings(AppConfiguration configuration);
    SpaceDialogResult? ShowNewSpace(Guid? preselectDirectoryId = null);
    SpaceDialogResult? ShowEditSpace(string name);
    RemoteLocationDefinition? ShowRemoteLocation();
    GitDialogResult? ShowGitRepository(string currentPath);
    void ShowHistory(string title, string repositoryPath);
    void ShowArchives(IEnumerable<ArchiveRecord> records, string title);
    IGitProgress BeginGitOperation(string title, string subtitle);
}
