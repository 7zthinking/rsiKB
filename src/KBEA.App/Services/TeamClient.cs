using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KBEA.Core.Team;

namespace KBEA.App.Services;

/// <summary>团队共享 HTTP 客户端：全部请求带机器签名。HTTPS 对端按成员表公钥钉扎（自签证书无 CA 链）。</summary>
public sealed class TeamClient
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
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    private readonly MachineIdentity _identity;

    public TeamClient(MachineIdentity identity)
    {
        _identity = identity;
    }

    public async Task<bool> PingAsync(PeerInfo peer, CancellationToken token = default)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, peer, "/t/ping", token: token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>向疑似 owner 的机器询问短码邀请；匹配则返回完整签名载荷（本方法无需成员签名——joiner 尚未入队）。</summary>
    public async Task<InvitePayload?> GetShortInviteAsync(PeerInfo peer, string shortCode, CancellationToken token = default)
    {
        var path = $"/t/teams/invite/{Uri.EscapeDataString(shortCode)}";
        try
        {
            using var response = await SendAsync(HttpMethod.Get, peer, path, token: token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<InvitePayload>(body, JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return null;
        }
    }

    public async Task<JoinResponse?> JoinTeamAsync(PeerInfo owner, InvitePayload invite, CancellationToken token = default)
    {
        var request = new JoinRequest
        {
            Invite = invite,
            MachineCode = _identity.MachineCode,
            Nickname = _identity.Nickname,
            PublicKey = _identity.PublicKey
        };
        try
        {
            var json = JsonSerializer.Serialize(request);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await SendAsync(HttpMethod.Post, owner, "/t/teams/join", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<JoinResponse>(body, JsonOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return null;
        }
    }

    public async Task<List<RemoteFileEntry>?> ListSpaceAsync(PeerInfo peer, Guid spaceId, CancellationToken token = default)
    {
        var path = $"/t/spaces/{spaceId}/list";
        try
        {
            using var response = await SendAsync(HttpMethod.Get, peer, path, token: token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SpaceListing>(body, JsonOptions)?.Files;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>下载共享空间文件（返回字节数组；供预览与缓存）。</summary>
    public async Task<byte[]?> GetFileBytesAsync(PeerInfo peer, Guid spaceId, string path, CancellationToken token = default)
    {
        try
        {
            var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            using var response = await SendAsync(HttpMethod.Get, peer,
                $"/t/spaces/{spaceId}/file?path={encoded}", token: token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>下载共享空间文件到目标路径（Range 由服务端支持，这里按整流下载）。</summary>
    public async Task<bool> DownloadFileAsync(PeerInfo peer, Guid spaceId, string path, string targetPath, CancellationToken token = default)
    {
        try
        {
            var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            using var response = await SendAsync(HttpMethod.Get, peer,
                $"/t/spaces/{spaceId}/file?path={encoded}", token: token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var tmp = targetPath + ".dl";
            await using (var stream = File.Create(tmp))
            {
                await response.Content.CopyToAsync(stream, token).ConfigureAwait(false);
            }
            File.Move(tmp, targetPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    /// <summary>写回共享空间文件（需要 write 级别）。</summary>
    public async Task<bool> PutFileAsync(PeerInfo peer, Guid spaceId, string path, string localPath, CancellationToken token = default)
    {
        try
        {
            var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            await using var content = File.OpenRead(localPath);
            using var streamContent = new StreamContent(content);
            using var response = await SendAsync(HttpMethod.Put, peer,
                $"/t/spaces/{spaceId}/file?path={encoded}", streamContent, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, PeerInfo peer, string path,
        HttpContent? content = null, CancellationToken token = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var request = new HttpRequestMessage(method, peer.BaseUrl + path);
        request.Headers.Add("X-KBEA-Mc", _identity.MachineCode);
        request.Headers.Add("X-KBEA-Ts", timestamp);
        request.Headers.Add("X-KBEA-Sig",
            _identity.Sign(MachineIdentity.SignaturePayload(_identity.MachineCode, timestamp, method.Method, path)));
        if (content is not null) request.Content = content;
        return await Http.SendAsync(request, token).ConfigureAwait(false);
    }
}
