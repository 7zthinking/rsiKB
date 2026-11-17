using System.IO;
using KBEA.Core.Models;
using KBEA.Core.Team;

namespace KBEA.App.Services;

/// <summary>
/// 团队引用空间镜像：把团队成员共享的空间同步到本地镜像目录
/// （%LOCALAPPDATA%\KBEA\TeamSpaces\&lt;spaceId&gt;），浏览 = 本地枚举（即时），
/// 小文件随同步落盘，大文件点击时按需取回。owner 离线时沿用旧镜像（离线暂存）。
/// </summary>
public sealed class TeamSpaceMirror
{
    /// <summary>随同步自动落盘的文件大小上限；更大的文件点击时按需下载。</summary>
    public const long AutoSyncSizeLimit = 64 * 1024 * 1024;

    private readonly TeamClient _client;
    private readonly Func<string, PeerInfo?> _peerLookup;
    private readonly object _sync = new();

    public TeamSpaceMirror(TeamClient client, Func<string, PeerInfo?> peerLookup)
    {
        _client = client;
        _peerLookup = peerLookup;
    }

    public string MirrorRoot(Guid spaceId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KBEA", "TeamSpaces", $"{spaceId:N}");

    /// <summary>与远端同步一次镜像；返回 (同步的文件数, 远端是否在线)。</summary>
    public async Task<(int Synced, bool Online)> SyncAsync(SpaceDefinition space, CancellationToken token = default)
    {
        if (!space.IsTeamLink) return (0, false);
        var peer = _peerLookup(space.OwnerMachineCode);
        if (peer is not { Online: true }) return (0, false);
        var listing = await _client.ListSpaceAsync(peer, space.RemoteSpaceId, token).ConfigureAwait(false);
        if (listing is null) return (0, true);

        var root = MirrorRoot(space.Id);
        Directory.CreateDirectory(root);
        var synced = 0;
        foreach (var entry in listing.Where(f => !f.IsDirectory && f.Size <= AutoSyncSizeLimit))
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target))
            {
                var info = new FileInfo(target);
                var remoteMtime = DateTimeOffset.FromUnixTimeSeconds(entry.Mtime).UtcDateTime;
                if (info.Length == entry.Size && Math.Abs((info.LastWriteTimeUtc - remoteMtime).TotalSeconds) < 2) continue;
            }
            if (await _client.DownloadFileAsync(peer, space.RemoteSpaceId, entry.Path, target, token).ConfigureAwait(false))
            {
                try
                {
                    File.SetLastWriteTimeUtc(target, DateTimeOffset.FromUnixTimeSeconds(entry.Mtime).UtcDateTime);
                }
                catch (IOException) { }
                synced++;
            }
        }
        return (synced, true);
    }

    /// <summary>确保某个文件在镜像中（点击打开/预览大文件时按需下载）；返回本地路径，null = 失败。</summary>
    public async Task<string?> EnsureFileAsync(SpaceDefinition space, string relativePath, CancellationToken token = default)
    {
        if (!space.IsTeamLink) return null;
        var root = MirrorRoot(space.Id);
        var target = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!target.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal)) return null;
        if (File.Exists(target)) return target;

        var peer = _peerLookup(space.OwnerMachineCode);
        if (peer is not { Online: true }) return null;
        var ok = await _client.DownloadFileAsync(peer, space.RemoteSpaceId, relativePath, target, token).ConfigureAwait(false);
        return ok ? target : null;
    }

    /// <summary>把本地镜像里的文件写回 owner（write 级别才能成功）。</summary>
    public async Task<bool> PushFileAsync(SpaceDefinition space, string relativePath, CancellationToken token = default)
    {
        if (!space.IsTeamLink) return false;
        var root = MirrorRoot(space.Id);
        var target = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(target)) return false;
        var peer = _peerLookup(space.OwnerMachineCode);
        if (peer is not { Online: true }) return false;
        return await _client.PutFileAsync(peer, space.RemoteSpaceId, relativePath, target, token).ConfigureAwait(false);
    }

    /// <summary>清理镜像目录（删除团队引用空间时调用）。</summary>
    public void Cleanup(Guid spaceId)
    {
        var root = MirrorRoot(spaceId);
        lock (_sync)
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        }
    }
}
