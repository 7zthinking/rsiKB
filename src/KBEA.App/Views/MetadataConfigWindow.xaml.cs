using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KBEA.App.Services;
using KBEA.Core.Knowledge;
using KBEA.Core.Models;
using KBEA.Core.Plugins;

namespace KBEA.App.Views;

/// <summary>
/// 知识库元数据面板：展示整体构建情况、各库各层级的执行状态，
/// 以及每个插件附加的元数据构建情况（执行中带进度条，完成后显示状态与更新时间）。
/// </summary>
public partial class MetadataConfigWindow : Window
{
    private readonly WorkerClient _worker;
    private readonly List<SpaceDefinition> _spaces;
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    /// <summary>状态提示回传，由主窗口写入状态栏。</summary>
    public event EventHandler<string>? StatusChanged;

    public MetadataConfigWindow(WorkerClient worker, List<SpaceDefinition> spaces)
    {
        _worker = worker;
        _spaces = spaces;
        InitializeComponent();

        ScheduleLevelBox.ItemsSource = new[] { "基础层", "类型层", "专题层" };
        ScheduleLevelBox.SelectedIndex = 0;
        ScheduleSpaceBox.ItemsSource = _spaces.Select(s => s.Name).ToList();
        if (_spaces.Count > 0) ScheduleSpaceBox.SelectedIndex = 0;

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshPanels();
        _timer.Start();

        RefreshPanels();
    }

    /// <summary>窗口关闭时停止刷新计时器。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPanels();

    private void StartWorker_Click(object sender, RoutedEventArgs e)
    {
        var started = _worker.EnsureWorkerRunning();
        RefreshPanels();
        StatusChanged?.Invoke(this, started ? "后台元数据服务已启动。" : "后台服务启动失败，请检查 KBEA.Worker.exe 是否存在。");
    }

    /// <summary>重建整个面板内容。</summary>
    private void RefreshPanels()
    {
        WorkerStatusText.Text = _worker.IsWorkerRunning
            ? "后台服务运行中 · 任务在独立进程中执行，不影响界面操作"
            : "后台服务未运行 · 任务将在服务启动后自动执行";

        SpacePanel.Items.Clear();
        EmptyHint.Visibility = _spaces.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var latest = _worker.LatestPerSpace();
        RefreshOverview(latest);

        foreach (var space in _spaces)
        {
            SpacePanel.Items.Add(BuildSpaceCard(space, latest));
        }
    }

    /// <summary>刷新整体情况：各库各层级的完成情况汇总。</summary>
    private void RefreshOverview(IReadOnlyDictionary<string, MetadataJob> latest)
    {
        var total = 0;
        var done = 0;
        var running = 0;
        var failed = 0;
        var pending = 0;

        // 语义层没有可用插件时永远无法完成，不计入总任务量，否则总进度到不了 100%
        var semanticAvailable = PluginManager.Instance.GetByLevel(MetadataLevel.Semantic).Count > 0;
        foreach (var space in _spaces)
        {
            foreach (var level in new[] { MetadataLevel.Basic, MetadataLevel.Type, MetadataLevel.Semantic })
            {
                if (level == MetadataLevel.Semantic && !semanticAvailable) continue;
                total++;
                if (!latest.TryGetValue($"{space.Path}|{(int)level}", out var job)) { pending++; continue; }
                switch (job.State)
                {
                    case MetadataJobState.Succeeded: done++; break;
                    case MetadataJobState.Running: running++; break;
                    case MetadataJobState.Failed: failed++; break;
                    default: pending++; break;
                }
            }
        }

        OverviewText.Text = total == 0
            ? "尚未登记任何知识库。"
            : semanticAvailable
                ? $"共 {total} 项（{_spaces.Count} 个库 × 3 个层级）：已完成 {done} · 进行中 {running} · 排队/未开始 {pending} · 失败 {failed}"
                : $"共 {total} 项（{_spaces.Count} 个库 × 2 个层级，未发现语义插件）：已完成 {done} · 进行中 {running} · 排队/未开始 {pending} · 失败 {failed}";

        var percent = total == 0 ? 0 : (int)Math.Min(100, done * 100L / total);
        OverviewBar.Value = percent;
    }

    /// <summary>构造单个知识库的卡片：名称 + 三个层级的进度行（含插件明细）。</summary>
    private FrameworkElement BuildSpaceCard(SpaceDefinition space, IReadOnlyDictionary<string, MetadataJob> latest)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("CardBackground"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var stack = new StackPanel();
        card.Child = stack;

        // 标题行：库名 + 路径 + 全部重跑
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stack.Children.Add(header);

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(titlePanel);
        titlePanel.Children.Add(new TextBlock
        {
            Text = space.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = space.Path,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 12,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 380
        });

        var rerunAll = new Button
        {
            Content = "▶ 全部重跑",
            Style = (Style)FindResource("ToolbarButton"),
            Tag = space
        };
        rerunAll.Click += RerunAll_Click;
        Grid.SetColumn(rerunAll, 1);
        header.Children.Add(rerunAll);

        // 各层级进度行
        foreach (var level in new[] { MetadataLevel.Basic, MetadataLevel.Type, MetadataLevel.Semantic })
        {
            latest.TryGetValue($"{space.Path}|{(int)level}", out var job);
            stack.Children.Add(BuildLevelRow(space, level, job));
        }

        // 该库的定时计划
        var planStore = new MetadataPlanStore(space.Path);
        var plan = planStore.Load();
        if (plan.Schedules.Count > 0)
        {
            var schedulePanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            schedulePanel.Children.Add(new TextBlock
            {
                Text = "定时计划",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4)
            });
            foreach (var schedule in plan.Schedules)
            {
                schedulePanel.Children.Add(new TextBlock
                {
                    Text = $"· {MetadataLevelMapping.DisplayName(schedule.Level)} · 每 {schedule.IntervalHours} 小时 · " +
                           $"{(schedule.LastRunAt is null ? "尚未执行" : $"上次 {schedule.LastRunAt.Value:MM-dd HH:mm}")}" +
                           $"{(schedule.Enabled ? "" : "（已停用）")}",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("SecondaryTextBrush"),
                    Margin = new Thickness(10, 0, 0, 2)
                });
            }
            stack.Children.Add(schedulePanel);
        }

        return card;
    }

    /// <summary>构造单个层级的进度行，下方挂各插件的明细。</summary>
    private FrameworkElement BuildLevelRow(SpaceDefinition space, MetadataLevel level, MetadataJob? job)
    {
        var container = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = MetadataLevelMapping.DisplayName(level),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });

        string status;
        bool needsRun;
        bool isRunning = job?.State == MetadataJobState.Running;
        if (job is null)
        {
            status = "尚未执行";
            needsRun = true;
        }
        else if (isRunning)
        {
            status = $"执行中 {job.Percent}% · {job.Stage}";
            needsRun = false;
        }
        else if (job.State == MetadataJobState.Queued)
        {
            status = "排队中，等待后台服务领取";
            needsRun = false;
        }
        else if (job.State == MetadataJobState.Succeeded)
        {
            status = $"已完成 · {job.FinishedAt:MM-dd HH:mm} · {job.Message}";
            needsRun = false;
        }
        else
        {
            status = $"未完成（{job.State}） · {job.Message}";
            needsRun = true;
        }

        var statusText = new TextBlock
        {
            Text = status,
            FontSize = 12,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(statusText, 1);
        row.Children.Add(statusText);

        var rerun = new Button
        {
            Content = needsRun ? "▶ 运行" : "↻ 重跑",
            Style = (Style)FindResource("ToolbarButton"),
            Tag = (space, level)
        };
        rerun.Click += RerunLevel_Click;
        Grid.SetColumn(rerun, 2);
        row.Children.Add(rerun);

        container.Children.Add(row);

        // 执行中的层级显示总进度条
        if (isRunning)
        {
            container.Children.Add(new ProgressBar
            {
                Height = 4,
                Minimum = 0,
                Maximum = 100,
                Value = job!.Percent,
                Margin = new Thickness(72, 5, 0, 0)
            });
        }

        // 各插件附加的元数据构建情况
        container.Children.Add(BuildPluginDetails(space, level, job));
        return container;
    }

    /// <summary>构造插件明细：每个插件一行，含状态、覆盖文件数、进度条与更新时间。</summary>
    private FrameworkElement BuildPluginDetails(SpaceDefinition space, MetadataLevel level, MetadataJob? job)
    {
        var panel = new StackPanel { Margin = new Thickness(72, 6, 0, 0) };

        // 该层级下注册的所有插件（用于展示"未参与本次构建"的插件）
        var registered = PluginManager.Instance.GetByLevel(level);
        var pluginProgress = job is null ? [] : _worker.GetPluginProgress(job.Id);
        var progressById = pluginProgress.ToDictionary(p => p.PluginId, StringComparer.Ordinal);

        var rows = new List<(IMetadataPlugin Plugin, PluginProgress? Progress)>();
        foreach (var plugin in registered)
        {
            rows.Add((plugin, progressById.TryGetValue(plugin.PluginId, out var p) ? p : null));
        }
        // 有进度但插件已注销的也要显示
        foreach (var progress in pluginProgress)
        {
            if (registered.All(p => p.PluginId == progress.PluginId)) continue;
            rows.Add((null!, progress));
        }

        if (rows.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = level == MetadataLevel.Semantic ? "该层级暂无插件（需要语义模型支持）" : "该层级暂无插件",
                FontSize = 11,
                Foreground = (Brush)FindResource("SecondaryTextBrush")
            });
            return panel;
        }

        foreach (var (plugin, progress) in rows)
        {
            panel.Children.Add(BuildPluginRow(plugin, progress, job));
        }

        return panel;
    }

    /// <summary>构造单个插件的状态行。</summary>
    private FrameworkElement BuildPluginRow(IMetadataPlugin? plugin, PluginProgress? progress, MetadataJob? job)
    {
        var row = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(header);

        string name = plugin?.PluginName ?? progress?.PluginName ?? "未知插件";
        string extensions = plugin is not null
            ? string.Join(" ", plugin.SupportedExtensions)
            : progress?.Extensions ?? string.Empty;

        string text;
        bool showBar = false;
        double percent = 0;

        if (job?.State == MetadataJobState.Running && progress is not null)
        {
            text = $"{name} · {progress.Stage} · 已处理 {progress.Processed}/{progress.Total} 个文件" +
                   (extensions.Length > 0 ? $" · {extensions}" : "");
            showBar = true;
            percent = progress.Percent;
        }
        else if (job?.State == MetadataJobState.Succeeded && progress is not null)
        {
            text = $"{name} · 已完成 · 覆盖 {progress.Processed} 个文件" +
                   $"{(extensions.Length > 0 ? $" · {extensions}" : "")} · {progress.UpdatedAt:MM-dd HH:mm:ss}";
        }
        else if (job?.State == MetadataJobState.Succeeded)
        {
            text = $"{name} · 未参与本次构建" + (extensions.Length > 0 ? $" · {extensions}" : "");
        }
        else if (job?.State == MetadataJobState.Queued)
        {
            text = $"{name} · 等待中" + (extensions.Length > 0 ? $" · {extensions}" : "");
        }
        else if (job is null)
        {
            text = $"{name} · 尚未执行" + (extensions.Length > 0 ? $" · {extensions}" : "");
        }
        else
        {
            text = $"{name} · 未完成" + (extensions.Length > 0 ? $" · {extensions}" : "");
        }

        header.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (showBar)
        {
            row.Children.Add(new ProgressBar
            {
                Height = 3,
                Minimum = 0,
                Maximum = 100,
                Value = percent,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        return row;
    }

    private void RerunLevel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<SpaceDefinition, MetadataLevel> tag }) return;
        var (space, level) = tag;
        _worker.EnqueueJob(space.Path, space.Name, MetadataLevelMapping.ToJobKind(level), "manual");
        RefreshPanels();
    }

    private void RerunAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SpaceDefinition space }) return;
        foreach (var kind in new[] { MetadataJobKind.Basic, MetadataJobKind.Type })
        {
            _worker.EnqueueJob(space.Path, space.Name, kind, "manual");
        }
        RefreshPanels();
    }

    private void ScheduleSpaceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 切换知识库时刷新计划显示
        RefreshPanels();
    }

    private void AddSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleSpaceBox.SelectedIndex < 0 || _spaces.Count == 0) return;
        var space = _spaces[ScheduleSpaceBox.SelectedIndex];

        if (!int.TryParse(IntervalBox.Text, out var hours) || hours <= 0)
        {
            MessageBox.Show(this, "间隔必须是大于 0 的整数小时。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var level = ScheduleLevelBox.SelectedIndex switch
        {
            1 => MetadataLevel.Type,
            2 => MetadataLevel.Semantic,
            _ => MetadataLevel.Basic
        };

        var planStore = new MetadataPlanStore(space.Path);
        var plan = planStore.Load();
        plan.GetOrAdd(level).Enabled = true;
        plan.Schedules.Add(new MetadataSchedule
        {
            Level = level,
            IntervalHours = hours,
            Enabled = true,
            Description = $"每 {hours} 小时执行{MetadataLevelMapping.DisplayName(level)}"
        });
        planStore.Save(plan);

        RefreshPanels();
        StatusChanged?.Invoke(this, $"已为“{space.Name}”添加{MetadataLevelMapping.DisplayName(level)}定时计划（每 {hours} 小时）。");
    }
}
