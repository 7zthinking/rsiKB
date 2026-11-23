# P3 acceptance smoke test: kbea-plugin-wiki loads in the real app, registers the
# "知识WIKI" toolbar button via IToolbarContribution, and the button opens the
# MetadataConfigWindow (host-owned dialog) end-to-end.
# UIA-based (same approach as verify-git-plugin.ps1). ASCII only; Chinese via [char[]].
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Root
$app = Join-Path $Root 'artifacts\KBEA-Light\KBEA.exe'
$fixture = Join-Path $Root 'artifacts\wiki-verify-fixture'
$folder = Join-Path $fixture 'space'
$config = Join-Path $Root 'artifacts\wiki-verify-config.json'

# Fixture: a plain local folder with one file (no ingest needed; plugin entry test only)
if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
New-Item $folder -ItemType Directory -Force | Out-Null
Set-Content (Join-Path $folder 'note.md') "# smoke`n" -Encoding UTF8

$seed = @{
    AuthorName = 'Smoke'; AuthorEmail = 'smoke@localhost'; Theme = 'Light'; RsyncExecutable = 'tools/rsync/rsync.exe'
    ArchiveRoot = ''; AutoDetect = $false; FavoriteFolders = @(); RecentLocalFolders = @(); RemoteLocations = @()
    Spaces = @(
        @{ Id = [Guid]::NewGuid(); Name = 'Smoke Space'; Path = $folder; RemoteUrl = ''; RemoteKind = 0; DirectoryId = $null; AutoSync = $false; LastSyncAt = $null }
    ); Directories = @(); Workspaces = @()
}
$seed | ConvertTo-Json -Depth 6 | Set-Content $config -Encoding UTF8

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $app
$start.WorkingDirectory = Split-Path $app -Parent
$start.UseShellExecute = $false
$confArg = '--config "' + $config + '"'
$openArg = '--open "' + $folder + '"'
$start.Arguments = $confArg + ' ' + $openArg
$dataDir = Join-Path $Root 'artifacts\wiki-verify-data'
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }
New-Item $dataDir -ItemType Directory -Force | Out-Null
$start.EnvironmentVariables['KBEA_DATA_DIR'] = $dataDir
$process = [Diagnostics.Process]::Start($start)

$checks = 0
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++
}

function Get-AppWindows([int]$procId) {
    $result = @()
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $windows) { if ($w.Current.ProcessId -eq $procId) { $result += $w } }
    return $result
}

try {
    $window = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw "Application exited early with code $($process.ExitCode)." }
        $found = Get-AppWindows $process.Id
        if ($found.Count -gt 0) { $window = $found[0]; break }
        Start-Sleep -Milliseconds 300
    }
    if ($null -eq $window) { throw 'Application main window was not found.' }
    Start-Sleep -Milliseconds 2500

    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $names = @($elements | ForEach-Object { $_.Current.Name } | Where-Object { $_ })

    # Check 1: wiki plugin registered its toolbar button ("知识WIKI")
    $wikiBtn = -join ([char[]](0x77E5, 0x8BC6)) + 'WIKI'   # "知识WIKI"
    # Diagnostics: list every element whose name matches, with control type / state / rect
    $diag = @()
    foreach ($e in $elements) {
        if ($e.Current.Name -like "*$wikiBtn*") {
            $r = $e.Current.BoundingRectangle
            $diag += ("name={0} type={1} enabled={2} offscreen={3} rect={4:F0},{5:F0},{6:F0},{7:F0}" -f `
                $e.Current.Name, $e.Current.ControlType.ProgrammaticName, $e.Current.IsEnabled, `
                $e.Current.IsOffscreen, $r.X, $r.Y, $r.Width, $r.Height)
        }
    }
    Set-Content (Join-Path $Root 'artifacts\wiki-verify-diag.txt') ($diag -join "`n") -Encoding UTF8
    # Pick the element whose ControlType is Button (skip DataItem wrappers and inner Text)
    $btnElement = $null
    foreach ($e in $elements) {
        if ($e.Current.Name -like "*$wikiBtn*" -and $e.Current.ControlType -eq [Windows.Automation.ControlType]::Button) {
            $btnElement = $e; break
        }
    }
    Assert-True ($null -ne $btnElement) 'Wiki plugin toolbar button (知识WIKI) was not registered. See artifacts\wiki-verify-diag.txt.'

    # Check 2: clicking the plugin button opens the host-owned MetadataConfigWindow.
    # Re-find the button FRESH at click time (cached AutomationElement references can go stale).
    $fresh = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $btnElement = $null
    foreach ($e in $fresh) {
        if ($e.Current.Name -like "*$wikiBtn*" -and $e.Current.ControlType -eq [Windows.Automation.ControlType]::Button) {
            $btnElement = $e; break
        }
    }
    if ($null -eq $btnElement) { throw 'Wiki button vanished before click.' }
    $invoke = $btnElement.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    $dialog = $null
    $dialogTitle = -join ([char[]](0x77E5, 0x8BC6, 0x0057, 0x0049, 0x004B, 0x0049, 0x5143, 0x6570, 0x636E))  # "知识WIKI元数据"
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 300
        $found = Get-AppWindows $process.Id
        foreach ($w in $found) {
            if ($w.Current.Name -like "*$dialogTitle*") { $dialog = $w; break }
        }
        if ($null -ne $dialog) { break }
    }
    if ($null -eq $dialog) {
        # UIA RootElement children may not list owned modal dialogs: fall back to Win32 EnumWindows
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinEnum {
    delegate bool EnumCb(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    public static List<string> ListForPid(int pid) {
        var rows = new List<string>();
        EnumWindows((h, lp) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p == pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                rows.Add(sb.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return rows;
    }
}
'@
        foreach ($title in [WinEnum]::ListForPid($process.Id)) {
            if ($title -like "*$dialogTitle*") { $dialog = $true; break }
        }
    }
    Assert-True ($null -ne $dialog) 'Invoking wiki toolbar button did not open MetadataConfigWindow.'

    Write-Output "PASS kbea-plugin-wiki: toolbar button registered and metadata dialog opened ($checks checks)"
}
finally {
    if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    $process.Dispose()
}
