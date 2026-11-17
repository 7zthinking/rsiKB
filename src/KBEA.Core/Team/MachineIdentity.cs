using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace KBEA.Core.Team;

/// <summary>
/// 机器身份：机器码 + 签名密钥对。
/// 设计文档约定 ed25519；.NET 8 无内建 ed25519，为守住「除 mDNS 外 0 新依赖」原则，
/// 改用内建 ECDsa P-256（同等安全强度），签名语义不变。
/// 机器码 = SHA-256(机器 GUID + 主板序列号 + 首网卡 MAC) 前 16 字节。
/// profile.json 存于 %APPDATA%\KBEA\profile.json。
/// </summary>
public sealed class MachineIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string MachineCode { get; private set; } = string.Empty;
    public string Nickname { get; private set; } = string.Empty;
    public string PublicKey { get; private set; } = string.Empty;
    public string ProfilePath { get; }

    private ECDsa? _key;

    private MachineIdentity(string profilePath)
    {
        ProfilePath = profilePath;
    }

    /// <summary>展示用短码：RSI-xxxxxxxx。</summary>
    public string DisplayCode => MachineCode.Length >= 8 ? $"RSI-{MachineCode[..8].ToUpperInvariant()}" : MachineCode;

    /// <summary>测试隔离：设置 KBEA_DATA_DIR 后 profile 存到该目录（verify 脚本用）。</summary>
    public static string? DataDirOverride => Environment.GetEnvironmentVariable("KBEA_DATA_DIR");

    /// <summary>加载或首次生成身份。</summary>
    public static MachineIdentity LoadOrCreate(string? profilePath = null, string? nickname = null)
    {
        profilePath ??= Path.Combine(DataDirOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KBEA", "profile.json");
        var identity = new MachineIdentity(profilePath);
        identity.LoadOrGenerate(nickname);
        return identity;
    }

    private void LoadOrGenerate(string? nickname)
    {
        try
        {
            if (File.Exists(ProfilePath))
            {
                var json = File.ReadAllText(ProfilePath);
                var profile = JsonSerializer.Deserialize<ProfileData>(json, JsonOptions);
                if (profile is not null && !string.IsNullOrEmpty(profile.MachineCode)
                    && !string.IsNullOrEmpty(profile.PublicKey))
                {
                    MachineCode = profile.MachineCode;
                    Nickname = string.IsNullOrWhiteSpace(nickname) ? profile.Nickname : nickname;
                    PublicKey = profile.PublicKey;
                    _key = ECDsa.Create();
                    _key.ImportPkcs8PrivateKey(Convert.FromBase64String(profile.PrivateKey), out _);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // 损坏的 profile 重新生成
        }

        MachineCode = ComputeMachineCode();
        var key = ECDsa.Create();
        _key = key;
        // 公钥/私钥以 DER（SPKI / PKCS#8）base64 存储，跨平台无 XML 依赖
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        Nickname = string.IsNullOrWhiteSpace(nickname) ? Environment.UserName : nickname;
        Save();
    }

    public void SetNickname(string nickname)
    {
        Nickname = nickname;
        Save();
    }

    private void Save()
    {
        var data = new ProfileData
        {
            MachineCode = MachineCode,
            Nickname = Nickname,
            PublicKey = PublicKey,
            PrivateKey = Convert.ToBase64String(_key!.ExportPkcs8PrivateKey())
        };
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        File.WriteAllText(ProfilePath, JsonSerializer.Serialize(data, JsonOptions));
    }

    /// <summary>对数据签名，返回 base64。</summary>
    public string Sign(byte[] data)
    {
        var bytes = _key!.SignData(data, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(bytes);
    }

    public string Sign(string text) => Sign(Encoding.UTF8.GetBytes(text));

    /// <summary>用指定公钥（XML）验证签名。</summary>
    public static bool Verify(string publicKeyXml, byte[] data, string signatureBase64)
    {
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyXml), out _);
            return verifier.VerifyData(data, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static bool Verify(string publicKeyXml, string text, string signatureBase64)
        => Verify(publicKeyXml, Encoding.UTF8.GetBytes(text), signatureBase64);

    /// <summary>构造标准签名串：machineCode|timestamp|method|path。</summary>
    public static string SignaturePayload(string machineCode, string timestamp, string method, string path)
        => $"{machineCode}|{timestamp}|{method}|{path}";

    // ---------- WebDAV 访问令牌（Basic 认证密码用） ----------

    /// <summary>
    /// 签发 WebDAV 访问令牌：{机器码}:{过期Unix秒}:{签名}。
    /// 外部程序（UNC/WebDAV）无法带自定义签名头，用此令牌作为 Basic 认证密码，
    /// 服务端用成员表公钥验签 → 堵住「绕过团队授权直接 WebDAV 访问」。
    /// </summary>
    public string CreateWebDavToken(int hours = 24)
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(hours).ToUnixTimeSeconds();
        var payload = $"webdav|{MachineCode}|{expiry}";
        var signature = Convert.ToBase64String(_key!.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));
        return $"{MachineCode}:{expiry}:{signature}";
    }

    /// <summary>校验 WebDAV 令牌：机器码匹配 + 未过期 + 成员公钥验签。与 CreateWebDavToken 配对。</summary>
    public static bool VerifyWebDavToken(string token, string memberPublicKey, string expectedMachineCode)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(memberPublicKey)) return false;
        var parts = token.Split(':');
        if (parts.Length != 3) return false;
        var (machineCode, expiry, signature) = (parts[0], parts[1], parts[2]);
        if (!string.Equals(machineCode, expectedMachineCode, StringComparison.Ordinal)) return false;
        if (!long.TryParse(expiry, out var seconds) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= seconds) return false;
        return Verify(memberPublicKey, $"webdav|{machineCode}|{expiry}", signature);
    }

    // ---------- HTTPS 自签证书 ----------

    /// <summary>
    /// 导出身份自签证书（CN=RSI-xxxxxxxx，NotAfter 10 年）。
    /// 证书公钥 == 身份公钥 → 客户端可按成员表公钥钉扎（TeamTrustStore），无需 CA 链。
    /// CreateSelfSigned 在 Windows 上产生 ephemeral 私钥，导出 PFX 重建以兼容 Kestrel 绑定。
    /// </summary>
    public X509Certificate2 ExportIdentityCertificate()
    {
        var request = new CertificateRequest($"CN={DisplayCode}", _key!, HashAlgorithmName.SHA256);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string ComputeMachineCode()
    {
        var machineGuid = ReadMachineGuid();
        var boardSerial = ReadBoardSerial();
        var mac = ReadFirstMac();
        var raw = $"{machineGuid}|{boardSerial}|{mac}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static string ReadMachineGuid()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string ?? string.Empty;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return string.Empty;
        }
    }

    private static string ReadBoardSerial()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = "baseboard get SerialNumber",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return string.Empty;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length >= 2 ? lines[^1] : string.Empty;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string ReadFirstMac()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211);
            return nic?.GetPhysicalAddress().ToString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return string.Empty;
        }
    }

    private sealed class ProfileData
    {
        public string MachineCode { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public string PrivateKey { get; set; } = string.Empty;
    }
}
