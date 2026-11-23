# P5 acceptance smoke test: plugin enable/disable toggle.
# Flow: open Settings > "插件管理" > uncheck the sample plugin > 保存设置
#       => sample plugin's plugin.json gets enabled=false
#       => restart app => sample plugin toolbar button is gone.
# Structure mirrors verify-p4-extensions.ps1 exactly (flat script scope).
# UIA-based. ASCII only; Chinese via [char[]].
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Root
$app = Join-Path $Root 'artifacts\KBEA-Light\KBEA.exe'
$fixture = Join-Path $Root 'artifacts\p5-verify-fixture'
$folder = Join-Path $fixture 'space'
$config = Join-Path $Root 'artifacts\p5-verify-config.json'
$dataDir = Join-Path $Root 'artifacts\p5-verify-data'
$sampleManifest = Join-Path $Root 'artifacts\KBEA-Light\plugins\kbea-plugin-sample\plugin.json'

# Reset the sample plugin to enabled (strip BOM: ReadAllText keeps U+FEFF in the string)
if (Test-Path $sampleManifest) {
    $text = [System.IO.File]::ReadAllText($sampleManifest).TrimStart([char]0xFEFF)
    $text = [regex]::Replace($text, '"enabled"\s*:\s*(true|false)', '"enabled": true')
    [System.IO.File]::WriteAllText($sampleManifest, $text, (New-Object System.Text.UTF8Encoding($false)))
}

if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
New-Item $folder -ItemType Directory -Force | Out-Null
Set-Content (Join-Path $folder 'note.md') "smoke`n" -Encoding UTF8

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
$btnSample     = -join ([char[]](0x793A, 0x4F8B, 0x63D2, 0x4EF6))                                        # "示例插件" (按钮 UIA 名 = tooltip)
$gear          = -join ([char[]](0x2699))                                                                 # "⚙"
$settingsTitle = -join ([char[]](0x8D26, 0x53F7, 0x4E0E, 0x8BBE, 0x7F6E))                                  # "账号与设置"
$pluginsTab    = -join ([char[]](0x63D2, 0x4EF6, 0x7BA1, 0x7406))                                          # "插件管理"
$saveBtn       = -join ([char[]](0x4FDD, 0x5B58, 0x8BBE, 0x7F6E))                                          # "保存设置"
$sampleName    = -join ([char[]](0x793A, 0x4F8B, 0x63D2, 0x4EF6))                                          # "示例插件"

# Owned modal dialogs are invisible to UIA RootElement children: use Win32 to find the HWND.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinEnumP5 {
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

function Find-SampleButton([Windows.Automation.AutomationElement]$scope) {
    foreach ($e in (Find-ByName $scope $btnSample)) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { return $e }
    }
    return $null
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
    Start-Sleep -Milliseconds 3000

    # Check A: sample plugin toolbar button registered
    Assert-True ($null -ne (Find-SampleButton $window)) 'Sample toolbar button missing before disabling.'
    $checks++

    # Open settings
    $gearBtn = $null
    foreach ($e in (Find-ByName $window $gear)) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { $gearBtn = $e; break }
    }
    Assert-True ($null -ne $gearBtn) 'Settings (⚙) button not found.'
    $gearBtn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()

    $settings = $null
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 300
        foreach ($row in [WinEnumP5]::ListForPid($process.Id).GetEnumerator()) {
            if ($row.Value -like "*$settingsTitle*") { $settings = [Windows.Automation.AutomationElement]::FromHandle($row.Key); break }
        }
        if ($null -ne $settings) { break }
    }
    Assert-True ($null -ne $settings) 'Settings window did not open.'
    $checks++
    Start-Sleep -Milliseconds 800

    # Select the "插件管理" category
    $tab = $null
    foreach ($e in (Find-ByName $settings $pluginsTab)) {
        try { $null = $e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern); $tab = $e; break } catch { }
    }
    Assert-True ($null -ne $tab) 'Plugin management category missing in settings.'
    $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1000
    $checks++

    # Uncheck the enable checkbox on the sample plugin row.
    # The UIA tree is flat here (WPF Grid rows are not exposed), so pair the plugin-name
    # Text with the FIRST CheckBox that comes after it in document order.
    $toggled = $false
    $seenSample = $false
    foreach ($e in $settings.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Text -and
            $e.Current.Name -like "*$sampleName*" -and $e.Current.Name -like "*v*") {
            $seenSample = $true
            continue
        }
        if (-not $seenSample) { continue }
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::CheckBox) {
            $tp = $e.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
            if ($tp.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) { $tp.Toggle(); $toggled = $true }
            break
        }
    }
    Assert-True $toggled 'Could not toggle the sample plugin enable checkbox.'
    $checks++
    Start-Sleep -Milliseconds 400

    # Save settings
    $save = $null
    foreach ($e in (Find-ByName $settings $saveBtn)) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { $save = $e; break }
    }
    Assert-True ($null -ne $save) 'Save settings button not found.'
    $save.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 2000

    # plugin.json must now have enabled=false
    $manifestText = [System.IO.File]::ReadAllText($sampleManifest)
    Assert-True ($manifestText -match '"enabled"\s*:\s*false') 'plugin.json was not updated with enabled=false.'
    $checks++

    # Other plugins must stay enabled (the toggle must target the sample plugin only)
    $gitManifest = Join-Path $Root 'artifacts\KBEA-Light\plugins\kbea-plugin-git\plugin.json'
    if (Test-Path $gitManifest) {
        $gitText = [System.IO.File]::ReadAllText($gitManifest)
        Assert-True ($gitText -match '"enabled"\s*:\s*true') 'Another plugin was disabled by mistake.'
        $checks++
    }

    if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    $process.Dispose()
    Start-Sleep -Milliseconds 1000

    # --- Run 2: plugin disabled, toolbar button gone ---
    $start2 = [Diagnostics.ProcessStartInfo]::new()
    $start2.FileName = $app
    $start2.WorkingDirectory = Split-Path $app -Parent
    $start2.UseShellExecute = $false
    $start2.Arguments = '--config "' + $config + '" --open "' + $folder + '"'
    $start2.EnvironmentVariables['KBEA_DATA_DIR'] = $dataDir
    $process2 = [Diagnostics.Process]::Start($start2)

    $window2 = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process2.HasExited) { throw "Second instance exited early with code $($process2.ExitCode)." }
        $found2 = Get-AppWindows $process2.Id
        if ($found2.Count -gt 0) { $window2 = $found2[0]; break }
        Start-Sleep -Milliseconds 300
    }
    if ($null -eq $window2) { throw 'Second instance main window was not found.' }
    Start-Sleep -Milliseconds 3000

    Assert-True ($null -eq (Find-SampleButton $window2)) 'Sample toolbar button still present after disabling the plugin.'
    $checks++

    if (-not $process2.HasExited) { try { $process2.Kill() } catch { } }
    $process2.Dispose()

    # restore enabled state for the next run
    $text = [System.IO.File]::ReadAllText($sampleManifest).TrimStart([char]0xFEFF)
    $text = [regex]::Replace($text, '"enabled"\s*:\s*(true|false)', '"enabled": true')
    [System.IO.File]::WriteAllText($sampleManifest, $text, (New-Object System.Text.UTF8Encoding($false)))

    Write-Output "PASS p5-plugin-toggle: disable writes plugin.json and plugin stops loading ($checks checks)"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { try { $process.Kill() } catch { } }
    if ($null -ne $process2 -and -not $process2.HasExited) { try { $process2.Kill() } catch { } }
}
