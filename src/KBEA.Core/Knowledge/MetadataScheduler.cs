using KBEA.Core.Plugins;

namespace KBEA.Core.Knowledge;

/// <summary>
/// 元数据调度器：管理任务队列的领取、执行与定时计划触发。
/// 供独立 Worker 进程使用，也可在测试中直接驱动。
/// </summary>
public sealed class MetadataScheduler
{
    private readonly MetadataJobStore _jobs;
    private readonly Func<IReadOnlyList<SpaceDefinitionLite>> _spaceProvider;

    /// <summary>轮询间隔：空闲时 2 秒，有任务时立即连续处理。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>定时检查间隔。</summary>
    public TimeSpan ScheduleCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    public MetadataScheduler(MetadataJobStore jobs, Func<IReadOnlyList<SpaceDefinitionLite>>? spaceProvider = null)
    {
        _jobs = jobs;
        _spaceProvider = spaceProvider ?? (() => []);
        _jobs.Initialize();
    }

    /// <summary>Worker 启动时恢复上次未完成的任务（进程崩溃后遗留的 running 任务重新排队）。</summary>
    public int RecoverStaleJobs()
    {
        var recovered = 0;
        foreach (var job in _jobs.List(limit: 500))
        {
            if (job.State != MetadataJobState.Running) continue;
            _jobs.Complete(job.Id, MetadataJobState.Queued, "上次执行被中断，已重新排队");
            recovered++;
        }
        return recovered;
    }

    /// <summary>执行一个任务；返回是否成功。</summary>
    public bool RunJob(MetadataJob job, Action<string>? log = null)
    {
        try
        {
            var service = new KnowledgeIngestService();
            var progress = new Progress<KnowledgeIngestService.LayerProgress>(p =>
                _jobs.UpdateProgress(job.Id, p.Stage, p.Processed, p.Total));
            var pluginProgress = new Progress<PluginProgress>(p =>
                _jobs.UpdatePluginProgress(job.Id, p));

            var report = service.IngestLayer(
                job.SpacePath,
                job.Kind,
                new Progress<string>(message => log?.Invoke(message)),
                progress,
                pluginProgress,
                CancellationToken.None);

            _jobs.Complete(job.Id, MetadataJobState.Succeeded, report.Summary);
            UpdatePlanState(job, report.Summary, true);
            return true;
        }
        catch (Exception ex)
        {
            _jobs.Complete(job.Id, MetadataJobState.Failed, ex.Message);
            UpdatePlanState(job, ex.Message, false);
            log?.Invoke($"任务失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>轮询并执行任务，直到收到停止信号。返回执行的任务数。</summary>
    public int RunLoop(CancellationToken cancellationToken, Action<string>? log = null)
    {
        var executed = 0;
        var lastScheduleCheck = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.Now - lastScheduleCheck >= ScheduleCheckInterval)
                {
                    TriggerDueSchedules();
                    lastScheduleCheck = DateTimeOffset.Now;
                }

                var job = _jobs.ClaimNext();
                if (job is null)
                {
                    if (cancellationToken.WaitHandle.WaitOne(PollInterval))
                        break;
                    continue;
                }

                log?.Invoke($"开始执行任务：{MetadataLevelMapping.DisplayName(job.Kind)} @ {job.SpaceName}");
                RunJob(job, log);
                executed++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log?.Invoke($"调度循环异常：{ex.Message}");
                try { if (cancellationToken.WaitHandle.WaitOne(PollInterval)) break; }
                catch (OperationCanceledException) { break; }
            }
        }

        return executed;
    }

    /// <summary>检查所有空间的定时计划，把到期的计划转成任务入队。</summary>
    public int TriggerDueSchedules()
    {
        var triggered = 0;
        foreach (var space in _spaceProvider())
        {
            var planStore = new MetadataPlanStore(space.Path);
            var plan = planStore.Load();
            if (plan.Schedules.Count == 0) continue;

            var changed = false;
            foreach (var schedule in plan.Schedules)
            {
                if (!schedule.Enabled) continue;
                if (schedule.IntervalHours <= 0) continue;

                var level = plan.Find(schedule.Level);
                if (level is null || !level.Enabled) continue;

                var due = schedule.LastRunAt is null
                    ? true
                    : DateTimeOffset.Now - schedule.LastRunAt.Value >= TimeSpan.FromHours(schedule.IntervalHours);

                if (!due) continue;

                // 已有同层任务在跑则跳过，避免堆积
                if (_jobs.HasActiveJob(space.Path)) continue;

                schedule.LastRunAt = DateTimeOffset.Now;
                schedule.NextRunAt = schedule.LastRunAt.Value.AddHours(schedule.IntervalHours);
                changed = true;
                triggered++;

                _jobs.Enqueue(new MetadataJob
                {
                    SpacePath = space.Path,
                    SpaceName = space.Name,
                    Kind = MetadataLevelMapping.ToJobKind(schedule.Level),
                    Trigger = "schedule",
                    ScheduleId = schedule.Id,
                    Stage = "等待 Worker 领取"
                });
            }

            if (changed) planStore.Save(plan);
        }
        return triggered;
    }

    /// <summary>把执行结果写回空间的分层计划，供前端展示每层最近一次状态。</summary>
    private void UpdatePlanState(MetadataJob job, string message, bool succeeded)
    {
        try
        {
            var planStore = new MetadataPlanStore(job.SpacePath);
            var plan = planStore.Load();
            var level = plan.GetOrAdd(MetadataLevelMapping.ToLevel(job.Kind));
            level.LastRunAt = DateTimeOffset.Now;
            level.LastResult = succeeded ? message : $"失败：{message}";

            foreach (var schedule in plan.Schedules.Where(s => s.Id == job.ScheduleId))
                schedule.LastRunAt = DateTimeOffset.Now;

            planStore.Save(plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 计划回写失败不影响任务本身
        }
    }
}

/// <summary>调度器所需的最小空间信息（避免 Core 依赖完整配置模型）。</summary>
public sealed record SpaceDefinitionLite(string Name, string Path);
