using System.Collections.Concurrent;
using System.IO;

namespace KBEA.Core.Files;

public sealed class ChangeMonitor : IDisposable
{
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, Timer> _debounce = new();
    private bool _disposed;

    public event EventHandler<string>? Changed;

    public void Start(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watchers.ContainsKey(path) || !Directory.Exists(path)) return;
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 65536,
            EnableRaisingEvents = true
        };
        FileSystemEventHandler handler = (_, e) => Signal(path, e.FullPath);
        RenamedEventHandler renamed = (_, e) => Signal(path, e.FullPath);
        watcher.Changed += handler;
        watcher.Created += handler;
        watcher.Deleted += handler;
        watcher.Renamed += renamed;
        _watchers[path] = watcher;
    }

    public void Stop(string path)
    {
        if (_watchers.TryRemove(path, out var watcher)) watcher.Dispose();
        if (_debounce.TryRemove(path, out var timer)) timer.Dispose();
    }

    private void Signal(string root, string changedPath)
    {
        if (changedPath.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            changedPath.Contains($"{Path.DirectorySeparatorChar}.7z{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) return;
        var timer = _debounce.GetOrAdd(root, _ => new Timer(_ => RaiseChanged(root), null, Timeout.Infinite, Timeout.Infinite));
        timer.Change(TimeSpan.FromMilliseconds(1500), Timeout.InfiniteTimeSpan);
    }

    private void RaiseChanged(string root)
    {
        if (!_disposed) Changed?.Invoke(this, root);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        foreach (var timer in _debounce.Values) timer.Dispose();
        _watchers.Clear();
        _debounce.Clear();
    }
}
