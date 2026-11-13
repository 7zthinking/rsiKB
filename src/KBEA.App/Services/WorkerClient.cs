using System.Diagnostics;
using KBEA.Core.Knowledge;

namespace KBEA.App.Services;

/// <summary>
/// 前端与后台 Worker 的桥梁。
/// 前端只负责投递任务与查询进度，绝不自己跑元数据计算，保证界面不被拖住。
/// </summary>
public sealed class WorkerClient : IDisposable
{
    private readonly MetadataJobStore _jobs;
    private Process? _worker;

    public WorkerClient()
    {
        _jobs = new MetadataJobStore();
        _jobs.Initialize();
    }

    /// <summary>Worker 进程是否在运行。</summary>
    public bool IsWorkerRunning => _worker is { HasExited: false };

    /// <summary>Worker 可执行文件路径（发布目录或开发目录）。</summary>
    public static string? ResolveWorkerPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "KBEA.Worker.exe"),
            Path.Combine(AppContext.BaseDirectory, "worker", "KBEA.Worker.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "KBEA.Worker", "bin", "Release", "net8.0", "win-x64", "publish", "KBEA.Worker.exe")
        };
        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>
    /// 投递一次元数据任务，并确保 Worker 在跑。
    /// 若 Worker 不可用，任务仍会入队，等 Worker 启动后自动执行。
    /// </summary>
    public MetadataJob EnqueueJob(string spacePath, string spaceName, MetadataJobKind kind, string trigger = "manual")
    {
        var job = _jobs.Enqueue(new MetadataJob
        {
            SpacePath = spacePath,
            SpaceName = spaceName,
            Kind = kind,
            Trigger = trigger,
            Stage = "等待 Worker 领取"
        });
        EnsureWorkerRunning();
        return job;
    }

    /// <summary>确保后台 Worker 进程存活；已退出则重新拉起。</summary>
    public bool EnsureWorkerRunning()
    {
        if (IsWorkerRunning) return true;

        var path = ResolveWorkerPath();
        if (path is null) return false;

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(path)!
            };
            info.ArgumentList.Add("--db");
            info.ArgumentList.Add(_jobs.DatabasePath);

            _worker = Process.Start(info);
            return _worker is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>列出某空间的任务历史。</summary>
    public IReadOnlyList<MetadataJob> ListJobs(string? spacePath = null) => _jobs.List(spacePath);

    /// <summary>取某空间各层级最近一次任务状态。</summary>
    public IReadOnlyDictionary<string, MetadataJob> LatestPerSpace() => _jobs.LatestPerSpace();

    /// <summary>某空间是否有排队或执行中的任务。</summary>
    public bool HasActiveJob(string spacePath) => _jobs.HasActiveJob(spacePath);

    /// <summary>取某任务的各插件进度。</summary>
    public IReadOnlyList<PluginProgress> GetPluginProgress(string jobId) => _jobs.GetPluginProgress(jobId);

    /// <summary>取消某空间所有排队中的任务（执行中的不受影响）。</summary>
    public int CancelQueued(string spacePath)
    {
        var canceled = 0;
        foreach (var job in _jobs.List(spacePath))
        {
            if (job.State != MetadataJobState.Queued) continue;
            _jobs.Complete(job.Id, MetadataJobState.Canceled, "用户取消");
            canceled++;
        }
        return canceled;
    }

    /// <summary>清理历史任务记录。</summary>
    public void Cleanup() => _jobs.Cleanup();

    public void Dispose()
    {
        // Worker 是常驻后台进程，不随前端退出而终止，避免任务中断
        _worker?.Dispose();
        _worker = null;
    }
}
