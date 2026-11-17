using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web;
using KBEA.Core.Models;
using KBEA.Core.Team;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KBEA.App.Services;

/// <summary>
/// 团队共享 Kestrel 服务（承载于 App 进程）：
/// HTTP 端点（rsiKB 客户端，带签名）：ping / 成员表 / 加入团队 / manifest / 元数据 / 空间列表 / 文件（Range）
/// HTTPS 端点（同一组 /t/ 端点，自签身份证书）：App 内客户端按成员表公钥钉扎（TeamTrustStore）。
/// WebDAV 端点（外部程序走 UNC，映射在根路径）：PROPFIND / GET / PUT / LOCK / UNLOCK，
/// Basic 认证：user=请求者机器码、pass=成员签名令牌（24h），按成员表公钥验签后执行共享级别（search/read/write）。
/// </summary>
public sealed class TeamShareServer : IDisposable
{
    private const string DavUserItemKey = "KBEA-DavUser";

    private readonly TeamStore _store;
    private readonly MachineIdentity _identity;
    private readonly Func<List<TeamFolder>> _teamsProvider;
    private readonly Func<Guid, SpaceDefinition?> _spaceResolver;
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _locks = new(StringComparer.OrdinalIgnoreCase);
    private WebApplication? _app;

    public TeamShareServer(TeamStore store, MachineIdentity identity,
        Func<List<TeamFolder>> teamsProvider, Func<Guid, SpaceDefinition?> spaceResolver)
    {
        _store = store;
        _identity = identity;
        _teamsProvider = teamsProvider;
        _spaceResolver = spaceResolver;
    }

    public int Port { get; private set; }

    /// <summary>HTTPS 端口（0 = 未启用：HTTP 端口后 5 个均被占用时降级）。</summary>
    public int HttpsPort { get; private set; }

    /// <summary>在指定端口启动；占用则自动向后尝试。返回实际端口（0 = 失败）。</summary>
    public int Start(int preferredPort)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var port = preferredPort + attempt;
            // HTTPS 端口独立探测（TcpListener 预检可用性）：绑定失败会让整个 StartAsync 抛出，
            // 不能和 HTTP 重试混在一个 catch 里
            var httpsPort = FindFreePort(port + 1, port + 5);
            try
            {
                StartOn(port, httpsPort);
                Port = port;
                HttpsPort = httpsPort;
                return port;
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                // 端口被占，试下一个
            }
        }
        return 0;
    }

    private static int FindFreePort(int from, int to)
    {
        for (var candidate = from; candidate <= to; candidate++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, candidate);
                listener.Start();
                listener.Stop();
                return candidate;
            }
            catch (Exception ex) when (ex is SocketException or IOException) { }
        }
        return 0;
    }

    private void StartOn(int port, int httpsPort)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var identityCertificate = _identity.ExportIdentityCertificate();
        builder.WebHost.UseUrls(httpsPort > 0
            ? $"http://*:{port};https://*:{httpsPort}"
            : $"http://*:{port}");
        if (httpsPort > 0)
        {
            builder.WebHost.ConfigureKestrel(options =>
                options.ConfigureHttpsDefaults(https => https.ServerCertificate = identityCertificate));
        }
        var app = builder.Build();

        // ---- WebDAV Basic 认证中间件 ----
        // 仅作用于 WebDAV 路径（非 /t/ 前缀）；OPTIONS 放行（WebDAV 客户端协商阶段不带凭据）。
        // 凭据：Basic base64(机器码:签名令牌)，令牌由本机身份签发、成员表公钥验签 → 未授权机器无法列举/读取文件。
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/t") || HttpMethods.IsOptions(ctx.Request.Method))
            {
                await next();
                return;
            }
            var user = AuthenticateDav(ctx);
            if (user is null)
            {
                ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"KBEA\"";
                ctx.Response.StatusCode = 401;
                return;
            }
            ctx.Items[DavUserItemKey] = user;
            await next();
        });

        // ---- HTTP 端点 ----
        app.MapGet("/t/ping", () => Results.Json(new { mc = _identity.MachineCode, nm = _identity.Nickname, port = Port }));

        app.MapGet("/t/teams/{teamId}/members", (string teamId, HttpContext ctx) =>
        {
            if (!VerifyMemberSignature(ctx, teamId)) return Results.StatusCode(401);
            return Results.Json(_store.ReadMembers(teamId));
        });

        app.MapGet("/t/teams/invite/{code}", (string code) =>
        {
            // joiner 尚非成员（无签名头），凭短码换取完整签名邀请载荷；短码匹配 + 未过期 + 验签由 ResolveShortInvite 完成
            var payload = _store.ResolveShortInvite(code);
            return payload is null ? Results.NotFound(new { message = "邀请码无效或已过期" }) : Results.Json(payload);
        });

        app.MapPost("/t/teams/join", async (HttpContext ctx) =>
        {
            JoinRequest? request;
            try
            {
                request = await ctx.Request.ReadFromJsonAsync<JoinRequest>();
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                request = null;
            }
            if (request?.Invite is null) return Results.Json(new JoinResponse { Success = false, Message = "请求无效" });
            var team = _store.LoadTeam(request.Invite.TeamId, _identity.MachineCode);
            if (team is null) return Results.Json(new JoinResponse { Success = false, Message = "团队不存在" });
            if (!team.IsOwner(_identity.MachineCode)) return Results.Json(new JoinResponse { Success = false, Message = "本机不是该团队的 owner" });
            if (string.IsNullOrWhiteSpace(request.MachineCode) || string.IsNullOrWhiteSpace(request.PublicKey))
                return Results.Json(new JoinResponse { Success = false, Message = "缺少机器码或公钥" });

            var members = _store.ReadMembers(team.Definition.TeamId);
            var existing = members.FirstOrDefault(m => m.MachineCode == request.MachineCode);
            if (existing is not null)
            {
                existing.Nickname = request.Nickname;
                existing.PublicKey = request.PublicKey;
            }
            else
            {
                members.Add(new MemberRecord
                {
                    MachineCode = request.MachineCode,
                    Nickname = request.Nickname,
                    PublicKey = request.PublicKey,
                    Role = "member",
                    JoinedAt = DateTimeOffset.UtcNow
                });
            }
            _store.WriteMembers(team.Definition.TeamId, members);
            return Results.Json(new JoinResponse { Success = true, Team = team.Definition, Members = members });
        });

        app.MapGet("/t/teams/{teamId}/sync/{machineCode}/{**path}", (string teamId, string machineCode, string path, HttpContext ctx) =>
        {
            if (!VerifyMemberSignature(ctx, teamId)) return Results.StatusCode(401);
            var root = _store.SyncDir(teamId, machineCode);
            var fullPath = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal)) return Results.StatusCode(403);
            if (!File.Exists(fullPath)) return Results.NotFound();
            return Results.File(fullPath, "application/octet-stream");
        });

        app.MapGet("/t/spaces/{spaceId}/list", (string spaceId, HttpContext ctx) =>
        {
            if (!Guid.TryParse(spaceId, out var id)) return Results.StatusCode(400);
            var level = ResolveLevel(id, ctx);
            if (level is null) return Results.StatusCode(401);
            if (level.Value < ShareLevel.Search) return Results.StatusCode(403);
            var space = _spaceResolver(id);
            if (space is null || space.IsTeamLink || !Directory.Exists(space.Path)) return Results.NotFound();
            return Results.Json(new SpaceListing(ListFiles(space.Path)));
        });

        app.MapGet("/t/spaces/{spaceId}/file", (string spaceId, HttpContext ctx) =>
        {
            if (!Guid.TryParse(spaceId, out var id)) return Results.StatusCode(400);
            var level = ResolveLevel(id, ctx);
            if (level is null) return Results.StatusCode(401);
            if (level.Value < ShareLevel.Read) return Results.StatusCode(403);
            var space = _spaceResolver(id);
            var relative = ctx.Request.Query["path"].ToString();
            if (space is null || relative.Length == 0) return Results.NotFound();
            var fullPath = ResolveSpacePath(space.Path, relative);
            if (fullPath is null || !File.Exists(fullPath)) return Results.NotFound();
            return Results.File(fullPath, "application/octet-stream", enableRangeProcessing: true);
        });

        app.MapPut("/t/spaces/{spaceId}/file", async (string spaceId, HttpContext ctx) =>
        {
            if (!Guid.TryParse(spaceId, out var id)) return Results.StatusCode(400);
            var level = ResolveLevel(id, ctx);
            if (level is null) return Results.StatusCode(401);
            if (level.Value < ShareLevel.Write) return Results.StatusCode(403);
            var space = _spaceResolver(id);
            var relative = ctx.Request.Query["path"].ToString();
            if (space is null || space.IsTeamLink || relative.Length == 0) return Results.StatusCode(403);
            var fullPath = ResolveSpacePath(space.Path, relative);
            if (fullPath is null) return Results.StatusCode(403);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using (var target = File.Create(fullPath))
            {
                await ctx.Request.Body.CopyToAsync(target);
            }
            return Results.Ok();
        });

        // ---- WebDAV：映射在根路径（UNC \\机器@端口\空间名\文件 的首段 = 空间名）----
        app.MapMethods("/", new[] { "OPTIONS", "PROPFIND" }, (HttpContext ctx) => HandleDavRoot(ctx));
        app.MapMethods("/{share}", new[] { "OPTIONS", "PROPFIND", "LOCK", "UNLOCK", "GET", "PUT" },
            (string share, HttpContext ctx) => HandleDav(share, string.Empty, ctx));
        app.MapMethods("/{share}/{**path}", new[] { "OPTIONS", "PROPFIND", "GET", "PUT", "LOCK", "UNLOCK" },
            (string share, string path, HttpContext ctx) => HandleDav(share, path, ctx));

        _app = app;
        // 同步等待绑定完成：让 Start() 的端口重试逻辑能真正感知绑定失败。
        // 之前用 fire-and-forget RunAsync，绑定异常（端口占用）落在未观察 Task 里，
        // Start 误报成功、异常经 finalizer 线程炸进程
        app.StartAsync().GetAwaiter().GetResult();
        _ = app.RunAsync().ContinueWith(
            t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Dispose() => _app?.StopAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(3));

    // ---------- 权限 ----------

    /// <summary>校验请求签名 ∈ 团队成员表（防机器码伪造）。WebDAV 不走此校验（外部程序无法带自定义头）。</summary>
    private bool VerifyMemberSignature(HttpContext ctx, string teamId)
    {
        var machineCode = ctx.Request.Headers["X-KBEA-Mc"].ToString();
        var timestamp = ctx.Request.Headers["X-KBEA-Ts"].ToString();
        var signature = ctx.Request.Headers["X-KBEA-Sig"].ToString();
        if (machineCode.Length == 0 || timestamp.Length == 0 || signature.Length == 0) return false;
        if (!long.TryParse(timestamp, out var seconds) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds) > 300) return false;

        var member = _store.ReadMembers(teamId).FirstOrDefault(m => m.MachineCode == machineCode);
        if (member is null) return false;
        var payload = MachineIdentity.SignaturePayload(machineCode, timestamp, ctx.Request.Method, ctx.Request.Path.Value ?? "/");
        return MachineIdentity.Verify(member.PublicKey, payload, signature);
    }

    /// <summary>解析请求者对某空间的共享级别（跨团队取最高）；null = 无权限。
    /// 必须是团队成员且签名验证通过。</summary>
    private ShareLevel? ResolveLevel(Guid spaceId, HttpContext ctx)
    {
        var machineCode = ctx.Request.Headers["X-KBEA-Mc"].ToString();
        if (machineCode.Length == 0) return null;
        ShareLevel? best = null;
        foreach (var team in _teamsProvider())
        {
            if (team.Members.All(m => m.MachineCode != machineCode)) continue;
            if (!team.MyShares.Any(s => s.SpaceId == spaceId)) continue;
            if (!VerifyMemberSignature(ctx, team.Definition.TeamId)) continue;
            var share = team.MyShares.First(s => s.SpaceId == spaceId);
            best = best is null ? share.Level : (ShareLevel)Math.Max((int)best, (int)share.Level);
        }
        return best;
    }

    private static string? ResolveSpacePath(string spacePath, string relative)
    {
        var root = Path.GetFullPath(spacePath);
        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return fullPath.StartsWith(root, StringComparison.Ordinal) ? fullPath : null;
    }

    private static List<RemoteFileEntry> ListFiles(string root)
    {
        var result = new List<RemoteFileEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && result.Count < 20000)
        {
            var current = pending.Pop();
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(directory);
                    if (name is ".git" or ".7z") continue;
                    result.Add(new RemoteFileEntry(
                        Path.GetRelativePath(root, directory).Replace('\\', '/'), 0, 0, true));
                    pending.Push(directory);
                }
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    var info = new FileInfo(file);
                    result.Add(new RemoteFileEntry(
                        Path.GetRelativePath(root, file).Replace('\\', '/'), info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(), false));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    // ---------- WebDAV ----------
    // UNC 映射：\\机器@端口\空间名\文件 → PROPFIND /空间名/文件…
    // 根路径 PROPFIND 列出请求者所在团队已声明共享的空间（作为「共享根目录」下的文件夹）。

    /// <summary>当前请求者机器码（中间件已认证并写入 Items；OPTIONS 放行时可能为 null）。</summary>
    private static string? DavRequester(HttpContext ctx) => ctx.Items[DavUserItemKey] as string;

    /// <summary>
    /// Basic 认证：user=请求者机器码，pass=成员签名令牌。遍历本机全部团队的成员表找请求者公钥验签。
    /// 返回请求者机器码（null = 认证失败）。
    /// </summary>
    private string? AuthenticateDav(HttpContext ctx)
    {
        var header = ctx.Request.Headers["Authorization"].ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
        var separator = decoded.IndexOf(':');
        if (separator <= 0) return null;
        var machineCode = decoded[..separator];
        var token = decoded[(separator + 1)..];
        foreach (var team in _teamsProvider())
        {
            var member = team.Members.FirstOrDefault(m => m.MachineCode == machineCode);
            if (member is not null)
                return MachineIdentity.VerifyWebDavToken(token, member.PublicKey, machineCode) ? machineCode : null;
        }
        return null;
    }

    /// <summary>请求者对某空间的 WebDAV 级别：owner 自己 = write；成员 = 所在团队对该空间的声明级别（跨团队取最高）。</summary>
    private ShareLevel DavLevelFor(SpaceDefinition space, string? requester)
    {
        if (string.IsNullOrEmpty(requester)) return ShareLevel.Search;
        var best = ShareLevel.Search;
        foreach (var team in _teamsProvider())
        {
            if (team.Members.All(m => m.MachineCode != requester)) continue;
            var share = team.MyShares.FirstOrDefault(s => s.SpaceId == space.Id);
            if (share is null) continue;
            var level = team.IsOwner(requester) ? ShareLevel.Write : share.Level;
            best = (ShareLevel)Math.Max((int)best, (int)level);
        }
        return best;
    }

    private IResult HandleDavRoot(HttpContext ctx)
    {
        if (ctx.Request.Method == "OPTIONS") return DavOptions(ctx);
        if (ctx.Request.Method == "PROPFIND")
        {
            var requester = DavRequester(ctx) ?? string.Empty;
            var spaces = _teamsProvider()
                .Where(t => t.Members.Any(m => m.MachineCode == requester))
                .SelectMany(t => t.MyShares)
                .Where(s => s.Level >= ShareLevel.Search)
                .GroupBy(s => s.SpaceName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .ToList();
            var sb = new StringBuilder();
            sb.Append(DavHeader());
            sb.Append(DavCollection("/", string.Empty));
            foreach (var name in spaces)
                sb.Append(DavCollection($"/{Uri.EscapeDataString(name)}/", name));
            sb.Append("</d:multistatus>");
            return Results.Text(sb.ToString(), "text/xml; charset=utf-8", statusCode: 207);
        }
        return Results.StatusCode(405);
    }

    private async Task<IResult> HandleDav(string share, string path, HttpContext ctx)
    {
        var space = FindDavSpace(share);
        if (space is null) return Results.NotFound();
        var method = ctx.Request.Method;
        if (method == "OPTIONS") return DavOptions(ctx);
        var requester = DavRequester(ctx) ?? string.Empty;
        if (method == "PROPFIND")
        {
            if (DavLevelFor(space, requester) < ShareLevel.Search) return Results.StatusCode(403);
            return DavPropfind(ctx, space, path);
        }
        if (method == "GET")
        {
            if (DavLevelFor(space, requester) < ShareLevel.Read) return Results.StatusCode(403);
            var fullPath = ResolveSpacePath(space.Path, path);
            if (fullPath is null || !File.Exists(fullPath)) return Results.NotFound();
            return Results.File(fullPath, "application/octet-stream", enableRangeProcessing: true);
        }
        if (method == "PUT")
        {
            if (space.IsTeamLink || DavLevelFor(space, requester) < ShareLevel.Write) return Results.StatusCode(403);
            var fullPath = ResolveSpacePath(space.Path, path);
            if (fullPath is null) return Results.StatusCode(403);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using (var target = File.Create(fullPath))
            {
                await ctx.Request.Body.CopyToAsync(target);
            }
            return Results.Ok();
        }
        if (method == "LOCK")
        {
            if (DavLevelFor(space, requester) < ShareLevel.Write) return Results.StatusCode(403);
            return DavLock(ctx, space, path);
        }
        if (method == "UNLOCK") return DavUnlock(ctx, path);
        return Results.StatusCode(405);
    }

    private SpaceDefinition? FindDavSpace(string share)
    {
        var decoded = HttpUtility.UrlDecode(share);
        foreach (var team in _teamsProvider())
        {
            foreach (var declaration in team.MyShares)
            {
                if (!string.Equals(declaration.SpaceName, decoded, StringComparison.OrdinalIgnoreCase)) continue;
                var space = _spaceResolver(declaration.SpaceId);
                if (space is not null) return space;
            }
        }
        return null;
    }

    private static int DepthOf(HttpContext ctx) =>
        int.TryParse(ctx.Request.Headers["Depth"].ToString(), out var depth) ? Math.Clamp(depth, 0, 1) : 1;

    private IResult DavPropfind(HttpContext ctx, SpaceDefinition space, string path)
    {
        var depth = DepthOf(ctx);
        var root = Path.GetFullPath(space.Path);
        var fullPath = ResolveSpacePath(root, path);
        if (fullPath is null) return Results.NotFound();
        var sb = new StringBuilder();
        sb.Append(DavHeader());
        var baseHref = $"/{Uri.EscapeDataString(space.Name)}/";
        if (string.IsNullOrEmpty(path))
        {
            sb.Append(DavCollection(baseHref, space.Name));
            if (depth == 1) sb.Append(DavChildren(baseHref, root));
        }
        else
        {
            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            var href = baseHref + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
            if (File.Exists(fullPath))
            {
                sb.Append(DavFile(href, new FileInfo(fullPath)));
            }
            else
            {
                sb.Append(DavCollection(href + "/", Path.GetFileName(fullPath)));
                if (depth == 1) sb.Append(DavChildren(href + "/", fullPath));
            }
        }
        sb.Append("</d:multistatus>");
        return Results.Text(sb.ToString(), "text/xml; charset=utf-8", statusCode: 207);
    }

    private static string DavChildren(string baseHref, string directory)
    {
        var sb = new StringBuilder();
        try
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name is ".git" or ".7z") continue;
                sb.Append(DavCollection(baseHref + Uri.EscapeDataString(name) + "/", name));
            }
            foreach (var child in Directory.EnumerateFiles(directory))
                sb.Append(DavFile(baseHref + Uri.EscapeDataString(Path.GetFileName(child)), new FileInfo(child)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return sb.ToString();
    }

    private static IResult DavOptions(HttpContext ctx)
    {
        ctx.Response.Headers["Allow"] = "OPTIONS, GET, PUT, PROPFIND, LOCK, UNLOCK";
        ctx.Response.Headers["DAV"] = "1,2";
        return Results.Text(string.Empty, statusCode: 200);
    }

    private IResult DavLock(HttpContext ctx, SpaceDefinition space, string path)
    {
        var seconds = ParseTimeout(ctx.Request.Headers["Timeout"].ToString());
        var token = $"opaquelocktoken:{Guid.NewGuid()}";
        _locks[$"{space.Id}:{path.ToLowerInvariant()}"] = (token, DateTimeOffset.UtcNow.AddSeconds(seconds));
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:prop xmlns:d="DAV:"><d:lockdiscovery><d:activelock>
            <d:locktype><d:write/></d:locktype><d:lockscope><d:exclusive/></d:lockscope>
            <d:depth>0</d:depth><d:timeout>Second-{seconds}</d:timeout>
            <d:locktoken><d:href>{token}</d:href></d:locktoken>
            </d:activelock></d:lockdiscovery></d:prop>
            """;
        ctx.Response.Headers["Lock-Token"] = $"<{token}>";
        return Results.Text(xml, "text/xml; charset=utf-8", statusCode: 200);
    }

    private IResult DavUnlock(HttpContext ctx, string path)
    {
        var token = ctx.Request.Headers["Lock-Token"].ToString().Trim('<', '>');
        var keys = _locks.Where(pair => pair.Value.Token == token).Select(pair => pair.Key).ToList();
        if (keys.Count == 0 && path.Length > 0)
        {
            keys = _locks.Where(pair => pair.Key.EndsWith(path.ToLowerInvariant(), StringComparison.Ordinal)).Select(pair => pair.Key).ToList();
        }
        foreach (var key in keys) _locks.Remove(key);
        return Results.StatusCode(204);
    }

    private static int ParseTimeout(string header)
    {
        var index = header.IndexOf("Second-", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return 600;
        return int.TryParse(header[(index + 7)..].Trim(), out var value) ? Math.Clamp(value, 60, 7200) : 600;
    }

    private static string DavHeader() =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\">";

    private static string XmlEscape(string text) => HttpUtility.HtmlEncode(text);

    private static string DavCollection(string href, string displayName) => $"""
        <d:response><d:href>{href}</d:href><d:propstat><d:prop>
        <d:displayname>{XmlEscape(displayName)}</d:displayname>
        <d:resourcetype><d:collection/></d:resourcetype>
        <d:getlastmodified>{DateTime.UtcNow:r}</d:getlastmodified>
        </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        """;

    private static string DavFile(string href, FileInfo info) => $"""
        <d:response><d:href>{href}</d:href><d:propstat><d:prop>
        <d:displayname>{XmlEscape(info.Name)}</d:displayname>
        <d:resourcetype/><d:getcontentlength>{info.Length}</d:getcontentlength>
        <d:getlastmodified>{info.LastWriteTimeUtc:r}</d:getlastmodified>
        </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        """;
}

/// <summary>远端共享空间的文件条目。</summary>
public sealed record RemoteFileEntry(string Path, long Size, long Mtime, bool IsDirectory);

public sealed record SpaceListing(List<RemoteFileEntry> Files);
