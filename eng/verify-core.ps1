# UIA verification using accessibility patterns (Invoke/SelectionItem) instead of synthetic mouse.
# Works in non-interactive sessions where mouse_event cannot reach the app window.
# ASCII only. Chinese literals assembled via [char[]].
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Root
$app = Join-Path $Root 'artifacts\KBEA-Light\KBEA.exe'
$fixture = Join-Path $Root 'artifacts\tab-verify-fixture'
if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
New-Item (Join-Path $fixture 'space-a') -ItemType Directory -Force | Out-Null
New-Item (Join-Path $fixture 'space-b') -ItemType Directory -Force | Out-Null
$config = Join-Path $Root 'artifacts\tab-verify-config.json'

function Write-Config([bool]$withSpaces) {
    $seed = @{
        AuthorName = 'Tab'; AuthorEmail = 'tab@localhost'; Theme = 'Light'; RsyncExecutable = 'tools/rsync/rsync.exe'
        ArchiveRoot = ''; AutoDetect = $false; FavoriteFolders = @(); RecentLocalFolders = @(); RemoteLocations = @()
        Spaces = @(); Directories = @(); Workspaces = @()
    }
    if ($withSpaces) {
        $seed.Spaces = @(
            @{ Id = [Guid]::NewGuid(); Name = 'Space A'; Path = (Join-Path $fixture 'space-a'); RemoteUrl = ''; RemoteKind = 0; DirectoryId = $null; AutoSync = $false; LastSyncAt = $null },
            @{ Id = [Guid]::NewGuid(); Name = 'Space B'; Path = (Join-Path $fixture 'space-b'); RemoteUrl = ''; RemoteKind = 0; DirectoryId = $null; AutoSync = $false; LastSyncAt = $null }
        )
    }
    $seed | ConvertTo-Json -Depth 6 | Set-Content $config -Encoding UTF8
}

function Start-App {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $app
    $start.WorkingDirectory = Split-Path $app -Parent
    $start.UseShellExecute = $false
    $start.Arguments = '"' + ((@('--config', $config)) -join '" "') + '"'
    # Isolate team-share data (profile/teams) from the real user profile
    $dataDir = Join-Path $Root 'artifacts\tab-verify-data'
    if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }
    New-Item $dataDir -ItemType Directory -Force | Out-Null
    $start.EnvironmentVariables['KBEA_DATA_DIR'] = $dataDir
    return [Diagnostics.Process]::Start($start)
}

function Get-AppWindow($process) {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw "Application exited early with code $($process.ExitCode)." }
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
        foreach ($window in $windows) {
            if ($window.Current.ProcessId -eq $process.Id) { return $window }
        }
        Start-Sleep -Milliseconds 300
    }
    throw 'Application main window was not found.'
}

function Get-Elements($window) {
    return $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
}

function Test-VisibleText($window, [string]$text) {
    return @(Get-Elements $window | Where-Object { $_.Current.Name -eq $text -and -not $_.Current.IsOffscreen }).Count -gt 0
}

function Invoke-Named($window, [string]$name) {
    for ($attempt = 0; $attempt -lt 6; $attempt++) {
        $el = @(Get-Elements $window | Where-Object {
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -eq $name
        }) | Select-Object -First 1
        if ($null -ne $el) {
            $inv = $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
            $inv.Invoke()
            Start-Sleep -Milliseconds 900
            return $true
        }
        Start-Sleep -Milliseconds 400
    }
    throw "Button not found: $name"
}

function Get-ToggleState($window, [string]$name) {
    $toggle = @(Get-Elements $window | Where-Object {
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -eq $name
    }) | Select-Object -First 1
    if ($null -eq $toggle) { return $null }
    try {
        $pattern = $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        return $pattern.Current.ToggleState
    } catch { return 'Unknown' }
}

function Get-TabNames($window) {
    return @(Get-Elements $window | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::TabItem } | ForEach-Object { $_.Current.Name })
}

$checks = 0
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++
}

$T = @{ newtab = -join ([char[]](0x65B0, 0x5EFA, 0x9009, 0x9879, 0x5361)) }
$C = @{
    welcome    = -join ([char[]](0x4ECE, 0x4E00, 0x4E2A, 0x76EE, 0x5F55, 0x6216, 0x5DE5, 0x4F5C, 0x7A7A, 0x95F4, 0x5F00, 0x59CB))
    spaceA     = 'Space A'
    badge      = -join ([char[]](0x7A7A, 0x95F4))
    wsToggle   = -join ([char[]](0x5DE5, 0x4F5C, 0x7A7A, 0x95F4))
    locToggle  = -join ([char[]](0x672C, 0x5730, 0x76EE, 0x5F55))
    remToggle  = -join ([char[]](0x56E2, 0x961F, 0x5171, 0x4EAB))
    teamMgr    = -join ([char[]](0x56E2, 0x961F, 0x7BA1, 0x7406))
    identity   = -join ([char[]](0x672C, 0x673A, 0x8EAB, 0x4EFD))
    newSpace   = -join ([char[]](0x65B0, 0x5EFA, 0x7A7A, 0x95F4))
}

# ===== Case A: no workspaces -> welcome page =====
Write-Config $false
$process = Start-App
try {
    $window = Get-AppWindow $process
    Start-Sleep -Milliseconds 1200
    Assert-True (Test-VisibleText $window $C.welcome) 'Welcome page was not shown when no workspace exists.'
    $names = Get-TabNames $window
    Assert-True ($names.Count -le 1) "Expected at most the placeholder tab."
    Write-Output 'PASS No workspace -> welcome page, no real tab'
}
finally {
    if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    $process.Dispose()
}

# ===== Case B: workspaces -> + creates tab with source badge; sidebar exclusive; team panel =====
Write-Config $true
$process = Start-App
try {
    $window = Get-AppWindow $process
    Start-Sleep -Milliseconds 1500

    # Startup: welcome, no tab, workspace sidebar default
    Assert-True (Test-VisibleText $window $C.welcome) 'The welcome page was not shown on startup.'
    $names = @(Get-TabNames $window | Where-Object { $_ -and $_ -notlike "*$($T.newtab)*" })
    Assert-True ($names.Count -eq 0) "Startup must not open any tab."
    Assert-True ((Get-ToggleState $window $C.wsToggle) -eq 'On') 'Sidebar does not default to workspace section.'
    Write-Output 'PASS Startup shows welcome page with workspace sidebar'

    # Team sharing service: status bar shows machine identity (server + mDNS started).
    # Must run BEFORE opening a tab, which overwrites the status bar text.
    $identityFound = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $identityEls = @(Get-Elements $window | Where-Object { $_.Current.Name -like "*$($C.identity)*" })
        if ($identityEls.Count -gt 0) { $identityFound = $true; break }
        Start-Sleep -Milliseconds 800
    }
    Assert-True $identityFound 'Team share service status (machine identity) missing in status bar.'
    Write-Output 'PASS Team share service started (identity shown in status bar)'

    # + -> tab follows first workspace with source badge
    Assert-True (Invoke-Named $window $T.newtab) 'New-tab button not found.'
    $names = Get-TabNames $window
    Assert-True ($names.Count -ge 2) 'Clicking + did not create a tab.'
    Assert-True (Test-VisibleText $window $C.spaceA) 'New tab does not show Space A.'
    Assert-True (Test-VisibleText $window $C.badge) 'Workspace source badge missing on tab card.'
    Write-Output 'PASS New tab follows the first workspace with source badge'

    # Sidebar default state: exactly one section on (mouse-based toggle switch is
    # verified by eng/verify-tabs.ps1 in an interactive session; synthetic mouse
    # cannot reach the window in headless sessions, so we only assert defaults here)
    Assert-True ((Get-ToggleState $window $C.wsToggle) -eq 'On') 'Workspace toggle should be on.'
    Assert-True ((Get-ToggleState $window $C.locToggle) -eq 'Off') 'Local toggle should default off.'
    Assert-True ((Get-ToggleState $window $C.remToggle) -eq 'Off') 'Team toggle should default off.'
    Write-Output 'PASS Sidebar default state (workspace on, others off)'
}
finally {
    if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    $process.Dispose()
}

Write-Output ("ALL PASS - {0} checks" -f $checks)
exit 0
