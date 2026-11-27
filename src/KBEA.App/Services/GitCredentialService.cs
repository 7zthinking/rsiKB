using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KBEA.App.Services;

/// <summary>一条 Git 凭证（Windows 凭据管理器 git:* 项；只读元信息，不读取密码）。</summary>
public sealed record GitCredentialEntry(
    /// <summary>凭据管理器目标名（如 git:https://github.com/），删除时使用。</summary>
    string Target,
    /// <summary>去掉 git: 前缀后的地址。</summary>
    string Url,
    /// <summary>主机名（github.com 等；无法解析时为空串）。</summary>
    string Host,
    /// <summary>用户名（可能未记录）。</summary>
    string? UserName,
    DateTimeOffset LastWritten);

/// <summary>一条 credential.helper 配置（来自 git config --show-origin）。</summary>
public sealed record CredentialHelperInfo(string Scope, string Helper, string Origin);

/// <summary>
/// Git 凭证管理：
/// - 读取 system / global 作用域的 credential.helper 配置（git config --show-origin）
/// - 枚举 / 删除 Windows 凭据管理器中 git:* 凭证（Git Credential Manager 与 wincred 都存这里）
/// 凭证密码不读出、不展示，仅支持查看与删除（删除后 git 下次访问会重新询问）。
/// </summary>
public static class GitCredentialService
{
    /// <summary>git 命令行是否可用（不可用时只影响 helper 配置一栏的展示）。</summary>
    public static bool GitCliAvailable { get; private set; } = true;

    // ==================== credential.helper 配置 ====================

    /// <summary>读取 system / global 作用域的 credential.helper 配置；来源文件一并返回。</summary>
    public static IReadOnlyList<CredentialHelperInfo> ReadCredentialHelpers()
    {
        var result = new List<CredentialHelperInfo>();
        foreach (var (scope, args) in new[] { ("系统", "--system"), ("全局", "--global") })
        {
            var output = RunGit($"config --show-origin --get-all credential.helper {args}");
            if (output is null) continue;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // 行格式：file:C:/Users/x/.gitconfig\tmanager
                var tab = line.IndexOf('\t');
                var origin = tab >= 0 ? line[..tab] : string.Empty;
                var helper = tab >= 0 ? line[(tab + 1)..].Trim() : line.Trim();
                if (origin.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) origin = origin[5..];
                if (helper.Length > 0) result.Add(new CredentialHelperInfo(scope, helper, origin));
            }
        }
        return result;
    }

    private static string? RunGit(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            GitCliAvailable = true;
            return output;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            GitCliAvailable = false;
            return null;
        }
    }

    // ==================== Windows 凭据管理器（git:*） ====================

    /// <summary>枚举 Windows 凭据管理器中所有 git:* 凭证。</summary>
    public static IReadOnlyList<GitCredentialEntry> ListGitCredentials()
    {
        var result = new List<GitCredentialEntry>();
        var buffer = IntPtr.Zero;
        try
        {
            if (!CredEnumerate("git:*", 0, out var count, out buffer, out _))
                return result;

            for (var i = 0; i < count; i++)
            {
                var credPtr = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                var target = Marshal.PtrToStringUni(cred.TargetName) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(target)) continue;

                var url = target.StartsWith("git:", StringComparison.OrdinalIgnoreCase) ? target[4..] : target;
                var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
                var userName = cred.UserName != IntPtr.Zero ? Marshal.PtrToStringUni(cred.UserName) : null;
                var fileTime = ((long)cred.LastWritten.dwHighDateTime << 32) | (uint)cred.LastWritten.dwLowDateTime;

                result.Add(new GitCredentialEntry(
                    target, url.TrimEnd('/'), host,
                    string.IsNullOrWhiteSpace(userName) ? null : userName,
                    DateTimeOffset.FromFileTime(fileTime)));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Host, b.Host));
        }
        finally
        {
            if (buffer != IntPtr.Zero) CredFree(buffer);
        }
        return result;
    }

    /// <summary>删除一条凭证；成功返回 true。</summary>
    public static bool DeleteCredential(string target) => CredDelete(target, CredTypeGeneric, 0);

    /// <summary>从 git 远程地址提取主机名（支持 https:// 与 git@host:path 两种写法）。</summary>
    public static string? ExtractRemoteHost(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;
        var url = remoteUrl.Trim();
        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = url.IndexOf(':');
            return colon > 4 ? url[4..colon] : null;
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;
    }

    private const int CredTypeGeneric = 1; // CRED_TYPE_GENERIC

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string? filter, int flag, out int count, out IntPtr credentials, out IntPtr free);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string targetName, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
