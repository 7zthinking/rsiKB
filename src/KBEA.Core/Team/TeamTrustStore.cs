using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KBEA.Core.Team;

/// <summary>
/// 信任公钥库：HTTPS 客户端按「对端身份公钥」钉扎（自签证书无 CA 链，不能走系统校验）。
/// 服务端证书 CN=RSI-xxxxxxxx、公钥 == 该机器身份公钥；
/// ReloadTeams 时把全部成员公钥（含 owner 与本机）灌入，叶子证书公钥 ∈ 库即信任。
/// </summary>
public static class TeamTrustStore
{
    private static readonly ConcurrentDictionary<string, byte> PublicKeys = new(StringComparer.Ordinal);

    /// <summary>灌入一个成员公钥（SPKI DER base64）。</summary>
    public static void Add(string publicKeyBase64)
    {
        if (!string.IsNullOrWhiteSpace(publicKeyBase64)) PublicKeys.TryAdd(publicKeyBase64, 0);
    }

    /// <summary>重建前清空（团队退出/成员变更后失效公钥不残留）。</summary>
    public static void Clear() => PublicKeys.Clear();

    /// <summary>叶子证书公钥 ∈ 信任库 → 信任；否则拒绝（含系统信任但不在库内的证书）。</summary>
    public static bool IsTrusted(X509Certificate2? certificate)
    {
        if (certificate is null) return false;
        try
        {
            var key = certificate.GetECDsaPublicKey();
            if (key is null) return false;
            var spki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            return PublicKeys.ContainsKey(spki);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            return false;
        }
    }
}
