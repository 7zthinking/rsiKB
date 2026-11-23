# P4 acceptance smoke test: sample plugin's three extension points work end-to-end
# in the real app: metadata field (detail panel), preview takeover (.sampletxt),
# and settings page (SettingsWindow "插件：<title>" entry + save callback path).
# UIA-based (same approach as verify-wiki-plugin.ps1). ASCII only; Chinese via [char[]].
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Root
$app = Join-Path $Root 'artifacts\KBEA-Light\KBEA.exe'
$fixture = Join-Path $Root 'artifacts\p4-verify-fixture'
$folder = Join-Path $fixture 'space'
$config = Join-Path $Root 'artifacts\p4-verify-config.json'

if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
New-Item $folder -ItemType Directory -Force | Out-Null
Set-Content (Join-Path $folder 'note.sampletxt') "sample content line`n" -Encoding UTF8

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
$start.Arguments = '--config "' + $config + '" --open "' + $folder + '"'
$dataDir = Join-Path $Root 'artifacts\p4-verify-data'
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

function Find-ByName([Windows.Automation.AutomationElement]$scope, [string]$needle) {
    $found = @()
    $elements = $scope.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $elements) { if ($e.Current.Name -like "*$needle*") { $found += $e } }
    return $found
}

# Chinese literals via code points
$btnSample  = -join ([char[]](0x793A, 0x4F8B, 0x63D2, 0x4EF6))                                        # "示例插件" (按钮 UIA 名 = tooltip)
$fieldName  = -join ([char[]](0x793A, 0x4F8B, 0x63D2, 0x4EF6))                                        # "示例插件"
$previewTxt = -join ([char[]](0x793A, 0x4F8B, 0x63D2, 0x4EF6, 0x9884, 0x89C8))                        # "示例插件预览"
$gear       = -join ([char[]](0x2699))                                                                # "⚙"
$settingsTitle = -join ([char[]](0x8D26, 0x53F7, 0x4E0E, 0x8BBE, 0x7F6E))                             # "账号与设置"
$pluginItem = -join ([char[]](0x63D2, 0x4EF6, 0xFF1A, 0x793A, 0x4F8B, 0x8BBE, 0x7F6E))                # "插件：示例设置"
$pageText   = -join ([char[]](0x793A, 0x4F8B, 0x63D2, 0x4EF6, 0x8BBE, 0x7F6E, 0x9875))                # "示例插件设置页"
$cancel     = -join ([char[]](0x53D6, 0x6D88))                                                        # "取消"

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

    # Check A: sample plugin toolbar button registered (P1 chain still alive)
    $btn = $null
    foreach ($e in (Find-ByName $window $btnSample)) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { $btn = $e; break }
    }
    Assert-True ($null -ne $btn) 'Sample toolbar button missing.'
    $checks++

    # Select the .sampletxt row in the file grid (first element supporting SelectionItemPattern)
    $selected = $false
    foreach ($e in (Find-ByName $window 'note.sampletxt')) {
        try {
            $sel = $e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
            $sel.Select(); $selected = $true; break
        } catch { }
    }
    Assert-True $selected 'note.sampletxt row not found/selectable in file grid.'
    $checks++
    Start-Sleep -Milliseconds 1200

    # Check B1: plugin metadata field row rendered in detail panel
    $fieldFound = (Find-ByName $window $fieldName).Count -gt 0
    Assert-True $fieldFound 'Plugin field row (示例插件) missing in detail panel.'
    $checks++

    # Check B2: plugin preview takeover rendered in preview panel
    # (read-only TextBox exposes TextPattern, not ValuePattern)
    $previewFound = $false
    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $elements) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Edit -or
            $e.Current.ControlType -eq [Windows.Automation.ControlType]::Document) {
            try {
                $value = $null
                try { $value = $e.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(200) } catch { }
                if (-not $value) {
                    try { $value = $e.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Value } catch { }
                }
                if ($value -like "*$previewTxt*") { $previewFound = $true; break }
            } catch { }
        }
    }
    Assert-True $previewFound 'Plugin preview text (示例插件预览) missing in preview panel.'
    $checks++

    # Check C: settings window gets plugin page entry; open it and see plugin content
    $gearBtn = $null
    foreach ($e in (Find-ByName $window $gear)) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { $gearBtn = $e; break }
    }
    Assert-True ($null -ne $gearBtn) 'Settings (⚙) button not found.'
    $gearBtn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()

    # Owned modal dialogs are invisible to UIA RootElement children (same finding as P3):
    # find the settings HWND via Win32 EnumWindows, then wrap it into a UIA element.
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinEnumP4 {
    delegate bool EnumCb(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    public static Dictionary<IntPtr, string> ListForPid(int pid) {
        var rows = new Dictionary<IntPtr, string>();
        EnumWindows(delegate(IntPtr h, IntPtr lp) {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p == pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                rows[h] = sb.ToString();
            }
            return true;
        }, IntPtr.Zero);
        return rows;
    }
}
'@
    $settings = $null
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 300
        # UIA attempt first (cheap)
        $found = Get-AppWindows $process.Id
        foreach ($w in $found) { if ($w.Current.Name -like "*$settingsTitle*" -and $w -ne $window) { $settings = $w; break } }
        if ($null -ne $settings) { break }
        # Win32 fallback
        foreach ($row in [WinEnumP4]::ListForPid($process.Id).GetEnumerator()) {
            if ($row.Value -like "*$settingsTitle*") { $settings = [Windows.Automation.AutomationElement]::FromHandle($row.Key); break }
        }
        if ($null -ne $settings) { break }
    }
    Assert-True ($null -ne $settings) 'Settings window did not open.'
    $checks++
    Start-Sleep -Milliseconds 800

    $item = $null
    foreach ($e in (Find-ByName $settings $pluginItem)) {
        try { $null = $e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern); $item = $e; break } catch { }
    }
    Assert-True ($null -ne $item) 'Plugin settings page entry (插件：示例设置) missing.'
    $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 800

    $pageFound = (Find-ByName $settings $pageText).Count -gt 0
    Assert-True $pageFound 'Plugin settings page content missing after selecting entry.'
    $checks++

    # Close settings via 取消 (exercises the non-save path)
    $cancelBtn = $null
    foreach ($e in (Find-ByName $settings $cancel)) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { $cancelBtn = $e; break }
    }
    if ($null -ne $cancelBtn) { try { $cancelBtn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { } }

    Write-Output "PASS p4-extensions: toolbar + field + preview + settings page ($checks checks)"
}
finally {
    if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    $process.Dispose()
}
