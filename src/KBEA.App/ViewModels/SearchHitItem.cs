using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using KBEA.Core.Knowledge;

namespace KBEA.App.ViewModels;

/// <summary>合并后的标签 chip：同类命名空间折叠为一个，Tooltip 展开全部值。</summary>
public sealed class TagChipViewModel
{
    public string Display { get; init; } = string.Empty;
    public string Tooltip { get; init; } = string.Empty;
}

/// <summary>检索命中项（UI 包装）：增加版本簇折叠状态、语义标签与显示文本。</summary>
public sealed partial class SearchHitItem : ObservableObject
{
    public SearchHitItem(KnowledgeSearchService.SearchHit hit)
    {
        Hit = hit;
        Siblings = hit.Siblings.Select(s => new SearchHitItem(s)).ToArray();
    }

    public KnowledgeSearchService.SearchHit Hit { get; }

    public SearchHitItem[] Siblings { get; }

    public string FileName => Path.GetFileName(Hit.RelativePath);

    public string RelativePath => Hit.RelativePath;

    public string FullPath => Hit.FullPath;

    public string Summary => Hit.Summary;

    public string Format => Hit.Format;

    public string Score => Hit.Score > 0 ? Hit.Score.ToString("F1") : string.Empty;

    /// <summary>人类可读的文件大小。</summary>
    public string DisplaySize
    {
        get
        {
            if (Hit.Size <= 0) return string.Empty;
            string[] units = ["B", "KB", "MB", "GB"];
            var value = (double)Hit.Size;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
        }
    }

    /// <summary>修改时间。</summary>
    public string DisplayTime => Hit.Mtime > DateTimeOffset.MinValue ? Hit.Mtime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>合并后的语义标签（同类命名空间折叠，鼠标悬浮显示全部值）。</summary>
    public ObservableCollection<TagChipViewModel> TagChips { get; } = [];

    /// <summary>文件名后是否有扩展名（无扩展名按目录处理，用于取 shell 图标）。</summary>
    public bool IsDirectory => string.IsNullOrEmpty(Path.GetExtension(Hit.RelativePath));

    /// <summary>取系统图标用的绝对路径；由 MainViewModel 注入解析结果（团队命中为空 → 通用图标）。</summary>
    public string IconPath { get; private set; } = string.Empty;

    /// <summary>注入图标路径解析（在填充结果后调用）。</summary>
    public void ResolveIcon(string iconPath) => IconPath = iconPath;

    /// <summary>初始化标签展示（在填充结果后调用一次）。</summary>
    public void BuildTags()
    {
        TagChips.Clear();
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var tag in Hit.Tags)
        {
            var separator = tag.IndexOf('/');
            if (separator <= 0) continue;
            var ns = tag[..separator];
            if (ns is "format" or "size" or "git") continue;
            if (!groups.TryGetValue(ns, out var list)) groups[ns] = list = [];
            list.Add(tag[(separator + 1)..]);
        }
        foreach (var (ns, values) in groups)
        {
            var distinct = values.Distinct().ToList();
            var display = distinct.Count == 1 ? distinct[0] : $"{distinct[0]} 等 {distinct.Count} 项";
            TagChips.Add(new TagChipViewModel
            {
                Display = display,
                Tooltip = string.Join("  ", distinct.Select(v => $"{ns}/{v}"))
            });
        }
    }

    /// <summary>版本角色（简化版/完整版/最新/归档…）。</summary>
    public string VersionRole => Hit.VersionRole;

    [ObservableProperty] private bool _isExpanded;

    public bool HasSiblings => Siblings.Length > 0;

    /// <summary>版本簇说明文案。</summary>
    public string SiblingSummary => HasSiblings
        ? $"另有 {Siblings.Length} 个相关版本"
        : string.Empty;
}
