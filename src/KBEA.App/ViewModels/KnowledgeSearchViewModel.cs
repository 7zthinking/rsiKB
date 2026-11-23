using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KBEA.Core.Knowledge;

namespace KBEA.App.ViewModels;

/// <summary>语义检索结果视图模型：承载分面解释、命中列表、选中联动预览与版本簇折叠。</summary>
public sealed partial class KnowledgeSearchViewModel : ObservableObject
{
    public KnowledgeSearchViewModel()
    {
        ClearCommand = new RelayCommand(() =>
        {
            Query = string.Empty;
            Reset();
        });
        ToggleSiblingsCommand = new RelayCommand<SearchHitItem>(hit =>
        {
            if (hit is null) return;
            hit.IsExpanded = !hit.IsExpanded;
        });
        OpenHitCommand = new RelayCommand<SearchHitItem>(hit =>
        {
            if (hit is null) return;
            OpenRequested?.Invoke(hit.Hit);
        });
        OpenFolderCommand = new RelayCommand<SearchHitItem>(hit =>
        {
            if (hit is null) return;
            OpenFolderRequested?.Invoke(hit.Hit);
        });
    }

    /// <summary>请求打开命中文件（由 MainViewModel 接管）。</summary>
    public Action<KnowledgeSearchService.SearchHit>? OpenRequested { get; set; }

    /// <summary>请求在浏览视图打开命中文件所在文件夹（由 MainViewModel 接管；团队共享命中忽略）。</summary>
    public Action<KnowledgeSearchService.SearchHit>? OpenFolderRequested { get; set; }

    /// <summary>图标路径解析：命中项 → (绝对路径, 是否目录)。由 MainViewModel 注入（空间根 + 相对路径）。</summary>
    public Func<KnowledgeSearchService.SearchHit, (string Path, bool IsDirectory)>? IconResolver { get; set; }

    /// <summary>预览加载委托：传入完整路径，返回 (图片, 文本)。由 MainViewModel 注入。</summary>
    public Func<string, (BitmapImage? Image, string Text)>? PreviewLoader { get; set; }

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private string _resultTitle = "知识检索";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private SearchHitItem? _selectedHit;
    [ObservableProperty] private string _previewText = "选择左侧命中文件可在此预览。";
    [ObservableProperty] private BitmapImage? _previewImage;

    public ObservableCollection<SearchHitItem> Hits { get; } = [];
    public ObservableCollection<string> FacetChips { get; } = [];
    public ObservableCollection<string> Understanding { get; } = [];

    public RelayCommand ClearCommand { get; }
    public RelayCommand<SearchHitItem> ToggleSiblingsCommand { get; }
    public RelayCommand<SearchHitItem> OpenHitCommand { get; }
    public RelayCommand<SearchHitItem> OpenFolderCommand { get; }

    public bool HasResult => Hits.Count > 0;
    public bool HasImagePreview => PreviewImage is not null;
    public bool HasTextPreview => PreviewImage is null;

    /// <summary>选中命中项的信息行（类型 / 大小 / 修改时间 / 版本角色）。</summary>
    public string SelectedInfo => SelectedHit is null
        ? string.Empty
        : string.Join(" · ", new[]
            {
                string.IsNullOrEmpty(SelectedHit.Format) ? null : $"格式 {SelectedHit.Format}",
                string.IsNullOrEmpty(SelectedHit.DisplaySize) ? null : SelectedHit.DisplaySize,
                string.IsNullOrEmpty(SelectedHit.DisplayTime) ? null : $"修改 {SelectedHit.DisplayTime}",
                string.IsNullOrEmpty(SelectedHit.VersionRole) ? null : $"版本 {SelectedHit.VersionRole}"
            }.Where(part => part is not null));

    partial void OnSelectedHitChanged(SearchHitItem? value)
    {
        OnPropertyChanged(nameof(SelectedInfo));
        if (value is null)
        {
            PreviewImage = null;
            PreviewText = "选择左侧命中文件可在此预览。";
            OnPropertyChanged(nameof(HasImagePreview));
            OnPropertyChanged(nameof(HasTextPreview));
            return;
        }
        var (image, text) = PreviewLoader?.Invoke(value.FullPath) ?? (null, "预览不可用。");
        PreviewImage = image;
        PreviewText = text;
        OnPropertyChanged(nameof(HasImagePreview));
        OnPropertyChanged(nameof(HasTextPreview));
    }

    /// <summary>填充检索结果。</summary>
    public void Apply(KnowledgeSearchService.SearchOutcome outcome)
    {
        Hits.Clear();
        FacetChips.Clear();
        Understanding.Clear();

        foreach (var note in outcome.Query.Notes) Understanding.Add(note);
        if (outcome.Query.RemainingText.Length > 0) FacetChips.Add($"关键词：{outcome.Query.RemainingText}");

        foreach (var hit in outcome.Hits)
        {
            var item = new SearchHitItem(hit);
            item.BuildTags();
            var icon = IconResolver?.Invoke(hit);
            if (icon is { } value) item.ResolveIcon(value.Path);
            Hits.Add(item);
        }

        ResultTitle = outcome.Hits.Count == 0
            ? "未找到匹配文件"
            : $"找到 {outcome.Hits.Count} 项（候选 {outcome.TotalCandidates} 项）";
        SelectedHit = Hits.Count > 0 ? Hits[0] : null;
        OnPropertyChanged(nameof(HasResult));
    }

    /// <summary>清空结果并退出检索视图（返回浏览页面）。</summary>
    public void Reset()
    {
        Hits.Clear();
        FacetChips.Clear();
        Understanding.Clear();
        ResultTitle = "知识检索";
        IsSearching = false;
        SelectedHit = null;
        OnPropertyChanged(nameof(HasResult));
    }

    partial void OnQueryChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) Reset();
    }
}
