using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace KBEA.App.Services;

/// <summary>
/// 防火墙首跑引导：团队共享依赖 5353/UDP（mDNS 组播）和服务端口 TCP 入站。
/// 通过检查/添加 Windows 防火墙规则（netsh advfirewall，需要管理员确认）来放行。
/// </summary>
public static class FirewallGuide
{
    public const string MdnsRuleName = "KBEA TeamShare mDNS";
    public const string ServiceRuleName = "KBEA TeamShare Service";

    /// <summary>两条放行规则是否都已存在。</summary>
    public static bool RulesReady(int servicePort)
    {
        return RuleExists(MdnsRuleName) && RuleExists(ServiceRuleName);
    }

    /// <summary>服务端口对应的 TCP 放行规则是否存在，且端口范围覆盖 servicePort..servicePort+5（兼容旧单端口规则）。</summary>
    public static bool ServiceRuleMatches(int servicePort)
    {
        var output = RunNetsh($"advfirewall firewall show rule name=\"{ServiceRuleName}\"");
        if (output is null) return false;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("LocalPort:", StringComparison.OrdinalIgnoreCase)) continue;
            return PortRangeCovers(trimmed["LocalPort:".Length..].Trim(), servicePort, servicePort + 5);
        }
        return false;
    }

    /// <summary>解析 netsh LocalPort 值（"52235" 或 "52235-52240"），判断是否完整覆盖 [from, to]。</summary>
    private static bool PortRangeCovers(string localPort, int from, int to)
    {
        var dash = localPort.IndexOf('-');
        int lo, hi;
        if (dash >= 0)
        {
            if (!int.TryParse(localPort[..dash].Trim(), out lo) || !int.TryParse(localPort[(dash + 1)..].Trim(), out hi))
                return false;
        }
        else
        {
            if (!int.TryParse(localPort, out lo)) return false;
            hi = lo;
        }
        return lo <= from && hi >= to;
    }

    private static bool RuleExists(string ruleName)
    {
        var output = RunNetsh($"advfirewall firewall show rule name=\"{ruleName}\"");
        return output is not null && output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 弹 UAC 提权，一次性添加两条放行规则（mDNS 5353/UDP + 服务端口范围 TCP，仅限本地子网）。
    /// 返回是否成功。**成功标准 = 回读规则验证通过**（cmd 退出码只透传最后一条命令，不可靠）。
    /// </summary>
    public static bool TryAddRules(int servicePort)
    {
        try
        {
            // 写临时 .cmd 批处理再提权执行：内联 cmd /c 多命令链的引号解析不可控（曾致规则静默未加上）
            var scriptPath = Path.Combine(Path.GetTempPath(), $"kbea-fw-{Guid.NewGuid():N}.cmd");
            var script =
                "@echo off\r\n" +
                $"netsh advfirewall firewall delete rule name=\"{MdnsRuleName}\" >nul 2>&1\r\n" +
                $"netsh advfirewall firewall delete rule name=\"{ServiceRuleName}\" >nul 2>&1\r\n" +
                $"netsh advfirewall firewall add rule name=\"{MdnsRuleName}\" dir=in action=allow protocol=UDP localport=5353 remoteip=LocalSubnet profile=any\r\n" +
                $"netsh advfirewall firewall add rule name=\"{ServiceRuleName}\" dir=in action=allow protocol=TCP localport={servicePort}-{servicePort + 5} remoteip=LocalSubnet profile=any\r\n";
            File.WriteAllText(scriptPath, script, System.Text.Encoding.ASCII);
            try
            {
                RunElevated("cmd.exe", "/c \"" + scriptPath + "\"");
                return ServiceRuleMatches(servicePort) && RuleExists(MdnsRuleName);
            }
            finally
            {
                try { File.Delete(scriptPath); } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return false;
        }
    }

    public static void OpenFirewallSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("CONTROL.EXE", "/name Microsoft.WindowsFirewall") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // 打不开控制面板不致命
        }
    }

    private static string? RunNetsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool RunElevated(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(startInfo);
        if (process is null) return false;
        process.WaitForExit(30000);
        return process.ExitCode == 0;
    }
}
