using System.Text.Json.Serialization;
using KBEA.Core.Plugins;

namespace KBEA.Core.Knowledge;

/// <summary>元数据任务类型：一次任务对应某个知识库某个层级的一次执行。</summary>
public enum MetadataJobKind
{
    /// <summary>基础层：规则标签 + 事实提取 + 版本聚类 + describe 骨架。</summary>
    Basic = 0,

    /// <summary>类型层：按格式专用解析插件补充结构化摘要。</summary>
    Type = 1,

    /// <summary>专题层：语义摘要与标签（依赖模型）。</summary>
    Semantic = 2
}

/// <summary>任务状态机。</summary>
public enum MetadataJobState
{
    /// <summary>排队中，等待 Worker 领取。</summary>
    Queued = 0,

    /// <summary>执行中。</summary>
    Running = 1,

    /// <summary>已完成。</summary>
    Succeeded = 2,

    /// <summary>执行失败。</summary>
    Failed = 3,

    /// <summary>已取消。</summary>
    Canceled = 4
}

/// <summary>一次元数据任务的描述。</summary>
public sealed class MetadataJob
{
    /// <summary>任务唯一 id。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>所属知识库（空间）路径。</summary>
    public string SpacePath { get; set; } = string.Empty;

    /// <summary>知识库名称（展示用）。</summary>
    public string SpaceName { get; set; } = string.Empty;

    /// <summary>任务层级。</summary>
    public MetadataJobKind Kind { get; set; } = MetadataJobKind.Basic;

    public MetadataJobState State { get; set; } = MetadataJobState.Queued;

    /// <summary>触发方式：手动、空间创建、定时。</summary>
    public string Trigger { get; set; } = "manual";

    /// <summary>已处理文件数。</summary>
    public int Processed { get; set; }

    /// <summary>总文件数（扫描完成后才确定）。</summary>
    public int Total { get; set; }

    /// <summary>当前阶段说明，如"正在提取 PPT 大纲"。</summary>
    public string Stage { get; set; } = "等待开始";

    /// <summary>结果摘要。</summary>
    public string Message { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>开始执行时间。</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>结束时间。</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>触发该任务的计划 id；手动任务为空。</summary>
    public string? ScheduleId { get; set; }

    /// <summary>进度百分比（0-100），由 Processed/Total 推导，Total 为 0 时为 0。</summary>
    [JsonIgnore]
    public int Percent => Total <= 0 ? 0 : (int)Math.Min(100, Processed * 100L / Total);

    /// <summary>耗时摘要。</summary>
    [JsonIgnore]
    public string Duration
    {
        get
        {
            var end = FinishedAt ?? DateTimeOffset.Now;
            var start = StartedAt ?? CreatedAt;
            var span = end - start;
            return span.TotalSeconds < 60
                ? $"{span.TotalSeconds:0.0} 秒"
                : $"{span.TotalMinutes:0.0} 分钟";
        }
    }
}

/// <summary>
/// 某个知识库的元数据分层计划：声明该库需要跑哪些层级、各自的插件与调度策略。
/// 存放在 .7z/metadata-plan.json，随空间走并进 git。
/// </summary>
public sealed class MetadataPlan
{
    /// <summary>计划版本号。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>知识库路径。</summary>
    public string SpacePath { get; set; } = string.Empty;

    /// <summary>知识库名称。</summary>
    public string SpaceName { get; set; } = string.Empty;

    /// <summary>各层级的启用状态与插件白名单。</summary>
    public List<MetadataLevelPlan> Levels { get; set; } = [];

    /// <summary>定时调度计划。</summary>
    public List<MetadataSchedule> Schedules { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>取某层级的计划项；不存在则返回 null。</summary>
    public MetadataLevelPlan? Find(MetadataLevel level) =>
        Levels.FirstOrDefault(l => l.Level == level);

    /// <summary>取某层级的计划项，不存在则新建一个默认项。</summary>
    public MetadataLevelPlan GetOrAdd(MetadataLevel level)
    {
        var plan = Find(level);
        if (plan is not null) return plan;
        plan = new MetadataLevelPlan { Level = level };
        Levels.Add(plan);
        return plan;
    }
}

/// <summary>单个层级的计划项。</summary>
public sealed class MetadataLevelPlan
{
    public MetadataLevel Level { get; set; }

    /// <summary>该层是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>参与该层的插件 id 白名单；为空表示按层级取全部已启用插件。</summary>
    public List<string> PluginIds { get; set; } = [];

    /// <summary>上次执行完成时间。</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>上次执行结果摘要。</summary>
    public string LastResult { get; set; } = string.Empty;

    /// <summary>该层涉及的文件总数（用于展示覆盖进度）。</summary>
    public int FileCount { get; set; }
}

/// <summary>定时调度计划。</summary>
public sealed class MetadataSchedule
{
    /// <summary>计划 id。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>所属层级。</summary>
    public MetadataLevel Level { get; set; } = MetadataLevel.Basic;

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 触发间隔（小时）。Worker 按"距上次执行时间 ≥ 间隔"来判断到期。
    /// 24 表示每天一次，168 表示每周一次。
    /// </summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>上次执行时间。</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>下次到期时间。</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>计划说明。</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>单个插件在一次任务中的执行进度。</summary>
public sealed class PluginProgress
{
    /// <summary>插件 id。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>插件显示名。</summary>
    public string PluginName { get; set; } = string.Empty;

    /// <summary>插件所属层级。</summary>
    public MetadataLevel Level { get; set; } = MetadataLevel.Basic;

    /// <summary>当前阶段说明。</summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>已处理文件数。</summary>
    public int Processed { get; set; }

    /// <summary>总文件数。</summary>
    public int Total { get; set; }

    /// <summary>最后更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>进度百分比。</summary>
    public int Percent => Total <= 0 ? 0 : (int)Math.Min(100, Processed * 100L / Total);

    /// <summary>覆盖的文件类型描述。</summary>
    public string Extensions { get; set; } = string.Empty;
}

/// <summary>元数据层级与任务类型的映射。</summary>
public static class MetadataLevelMapping
{
    public static MetadataJobKind ToJobKind(MetadataLevel level) => level switch
    {
        MetadataLevel.Type => MetadataJobKind.Type,
        MetadataLevel.Semantic => MetadataJobKind.Semantic,
        _ => MetadataJobKind.Basic
    };

    public static MetadataLevel ToLevel(MetadataJobKind kind) => kind switch
    {
        MetadataJobKind.Type => MetadataLevel.Type,
        MetadataJobKind.Semantic => MetadataLevel.Semantic,
        _ => MetadataLevel.Basic
    };

    /// <summary>层级的中文名。</summary>
    public static string DisplayName(MetadataLevel level) => level switch
    {
        MetadataLevel.Type => "类型层",
        MetadataLevel.Semantic => "专题层",
        _ => "基础层"
    };

    /// <summary>任务类型的中文名。</summary>
    public static string DisplayName(MetadataJobKind kind) => DisplayName(ToLevel(kind));
}
