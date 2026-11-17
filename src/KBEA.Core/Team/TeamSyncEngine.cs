using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KBEA.Core.Team;

/// <summary>
/// 团队同步引擎（客户端）：manifest 版本戳拉取 + 增量下载 + 本机导出。
/// 每成员只写自己的 sync 子目录 → 结构性无合并冲突。
/// 拉取时机由上层决定：启动 / 定时 / mDNS 上线 / 手动刷新。
/// </summary>
public sealed class TeamSyncEngine
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    TeamTrustStore.IsTrusted(cert as System.Security.Cryptography.X509Certificates.X509Certificate2)
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private readonly TeamStore _store;
    private readonly MachineIdentity _identity;
    private readonly Func<string, PeerInfo?> _peerLookup;
    private readonly Action<string> _log;

    public TeamSyncEngine(TeamStore store, MachineIdentity identity, Func<string, PeerInfo?> peerLookup, Action<string>? log = null)
    {
        _store = store;
        _identity = identity;
        _peerLookup = peerLookup;
        _log = log ?? (_ => { });
    }

    // ---------- 本机导出（写自己子目录） ----------

    /// <summary>
    /// 导出本机对某团队共享的空间元数据并重建 manifest.json。
    /// spacePathResolver：SpaceId → 本机路径（null = 空间当前不可用，跳过）。
    /// </summary>
    public void ExportOwn(TeamFolder team, Func<Guid, string?> spacePathResolver)
    {
        var syncDir = _store.SyncDir(team.Definition.TeamId, _identity.MachineCode);
        var metadataDir = Path.Combine(syncDir, "metadata");
        Directory.CreateDirectory(metadataDir);
        var exporter = new MetadataExporter();
        var validFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var share in team.MyShares)
        {
            var spacePath = spacePathResolver(share.SpaceId);
            if (spacePath is null || !Directory.Exists(spacePath))
            {
                _log($"[导出] 空间不可用，跳过：{share.SpaceName}");
                continue;
            }
            var count = exporter.Export(share.SpaceId, share.SpaceName, spacePath, metadataDir, team.Definition.TeamId);
            validFiles.Add($"metadata/{share.SpaceId:N}.ndjson");
            validFiles.Add("spaces.jsonl");
            _log($"[导出] {share.SpaceName} → {count} 条");
        }

        WriteManifest(team.Definition.TeamId, validFiles);
    }

    /// <summary>重建并原子写本机 manifest.json；version 单调递增（时间戳兜底 +1）。</summary>
    private void WriteManifest(string teamId, HashSet<string> validFiles)
    {
        var syncDir = _store.SyncDir(teamId, _identity.MachineCode);
        var manifestPath = Path.Combine(syncDir, "manifest.json");
        var previous = ReadManifest(manifestPath);

        var files = new List<ManifestEntry>();
        foreach (var relative in EnumerateRelativeFiles(syncDir))
        {
            if (relative == "manifest.json" || !validFiles.Contains(relative)) continue;
            var fullPath = Path.Combine(syncDir, relative);
            try
            {
                using var stream = File.OpenRead(fullPath);
                files.Add(new ManifestEntry(relative, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), new FileInfo(fullPath).Length));
            }
            catch (IOException) { }
        }

        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (previous is not null && version <= previous.Version) version = previous.Version + 1;
        TeamStore.WriteJson(manifestPath, new TeamManifest { Version = version, MachineCode = _identity.MachineCode, Files = files });
    }

    private static TeamManifest? ReadManifest(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<TeamManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static List<string> EnumerateRelativeFiles(string root)
    {
        var result = new List<string>();
        if (!Directory.Exists(root)) return result;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
                result.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            foreach (var directory in Directory.EnumerateDirectories(current))
                pending.Push(directory);
        }
        return result;
    }

    // ---------- 拉取（读他人子目录） ----------

    /// <summary>对全部团队执行一轮拉取：刷新成员表副本 + 对每个在线成员比对 manifest 并增量下载。</summary>
    public async Task PullAllAsync(IReadOnlyList<TeamFolder> teams, CancellationToken token = default)
    {
        foreach (var team in teams)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await PullTeamAsync(team, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                _log($"[同步] {team.Definition.Name} 拉取失败（保持旧数据）：{ex.Message}");
            }
        }
    }

    private async Task PullTeamAsync(TeamFolder team, CancellationToken token)
    {
        var teamId = team.Definition.TeamId;

        // owner 在线 → 拉最新成员表副本
        var ownerPeer = _peerLookup(team.Definition.OwnerMachineCode);
        if (ownerPeer is { Online: true })
        {
            var members = await GetJsonAsync<List<MemberRecord>>(
                ownerPeer, $"/t/teams/{teamId}/members", token).ConfigureAwait(false);
            if (members is { Count: > 0 })
            {
                team.Members = members;
                TeamStore.WriteJsonl(_store.MembersPath(teamId), members);
            }
        }

        foreach (var member in team.Members)
        {
            if (member.MachineCode == _identity.MachineCode) continue;
            token.ThrowIfCancellationRequested();
            var peer = _peerLookup(member.MachineCode);
            if (peer is not { Online: true }) continue;
            await PullFromPeerAsync(teamId, peer, token).ConfigureAwait(false);
        }
    }

    /// <summary>从一个在线成员拉取：manifest 版本不同才继续，diff hash 只下变化文件。</summary>
    public async Task PullFromPeerAsync(string teamId, PeerInfo peer, CancellationToken token = default)
    {
        var localManifestPath = _store.ManifestPath(teamId, peer.MachineCode);
        var localManifest = ReadManifest(localManifestPath);
        var remote = await GetJsonAsync<TeamManifest>(peer,
            $"/t/teams/{teamId}/sync/{peer.MachineCode}/manifest.json", token).ConfigureAwait(false);
        if (remote is null || remote.Version == 0) return;
        if (localManifest is not null && remote.Version == localManifest.Version) return;

        // 版本不同即更新（防备份恢复回退），不假设大小
        var localDir = _store.SyncDir(teamId, peer.MachineCode);
        var localHashes = new HashSet<string>(localManifest?.Files.Select(f => f.Hash) ?? [], StringComparer.Ordinal);
        var remoteHashes = new HashSet<string>(remote.Files.Select(f => f.Hash), StringComparer.Ordinal);

        var downloaded = 0;
        foreach (var entry in remote.Files)
        {
            if (localHashes.Contains(entry.Hash)) continue;
            var target = Path.Combine(localDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var ok = await DownloadFileAsync(peer,
                $"/t/teams/{teamId}/sync/{peer.MachineCode}/{entry.Path}", target, token).ConfigureAwait(false);
            if (ok) downloaded++;
        }

        // 清理远端已删除、且哈希不在远端清单里的本地文件
        foreach (var relative in EnumerateRelativeFiles(localDir))
        {
            if (relative == "manifest.json") continue;
            var hash = HashFile(Path.Combine(localDir, relative));
            if (hash is not null && !remoteHashes.Contains(hash))
            {
                try { File.Delete(Path.Combine(localDir, relative.Replace('/', Path.DirectorySeparatorChar))); }
                catch (IOException) { }
            }
        }

        TeamStore.WriteJson(localManifestPath, remote);
        _log($"[同步] {peer.DisplayName} 更新 {downloaded} 个文件（version {remote.Version}）");
    }

    // ---------- HTTP ----------

    public static HttpRequestMessage SignedRequest(HttpMethod method, PeerInfo peer, string path, MachineIdentity identity)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-KBEA-Mc", identity.MachineCode);
        request.Headers.Add("X-KBEA-Ts", timestamp);
        request.Headers.Add("X-KBEA-Sig", identity.Sign(MachineIdentity.SignaturePayload(identity.MachineCode, timestamp, method.Method, path)));
        request.Headers.Host = null;
        return request;
    }

    private async Task<T?> GetJsonAsync<T>(PeerInfo peer, string path, CancellationToken token)
    {
        using var request = SignedRequest(HttpMethod.Get, peer, path, _identity);
        request.RequestUri = new Uri(peer.BaseUrl + path);
        using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return default;
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private async Task<bool> DownloadFileAsync(PeerInfo peer, string path, string targetPath, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var request = SignedRequest(HttpMethod.Get, peer, path, _identity);
            request.RequestUri = new Uri(peer.BaseUrl + path);
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var tmp = targetPath + ".dl";
            await using (var stream = File.Create(tmp))
            {
                await response.Content.CopyToAsync(stream, token).ConfigureAwait(false);
            }
            File.Move(tmp, targetPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return false;
        }
    }

    private static string? HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException)
        {
            return null;
        }
    }
}
