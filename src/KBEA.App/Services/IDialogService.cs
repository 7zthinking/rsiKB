using KBEA.Core.Models;

namespace KBEA.App.Services;

public interface IDialogService
{
    string? Prompt(string title, string label, string defaultValue = "");
    string? SelectFolder(string title);
    void Info(string message, string title = "知识库自进化助手");
    bool Confirm(string message, string title = "请确认");
    bool ShowSettings(AppConfiguration configuration);
    void ShowHistory(IEnumerable<GitCommitInfo> commits, string title);
    void ShowArchives(IEnumerable<ArchiveRecord> records, string title);
}
