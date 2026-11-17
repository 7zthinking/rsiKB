param([string]$Root = (Split-Path $PSScriptRoot -Parent))

# Verifies the tab source model:
#  1) Startup opens the first workspace (not C:\ drive)
#  2) With no workspaces, the welcome page is shown
#  3) Every tab can switch between workspace / local / remote sources
#  4) The three sidebar sections are mutually exclusive
# ASCII only (PowerShell 5.1 reads .ps1 as ANSI).

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class TabMouse { [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra); [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); public static void Click(int x, int y) { System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); } public static void AltTap() { keybd_event(0x12, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(50); keybd_event(0x12, 0, 2, UIntPtr.Zero); } public static bool Activate(IntPtr hwnd) { for (int i = 0; i < 6; i++) { AltTap(); ShowWindow(hwnd, 3); SetForegroundWindow(hwnd); System.Threading.Thread.Sleep(300); if (GetForegroundWindow() == hwnd) return true; } return false; } }' -ReferencedAssemblies System.Windows.Forms, System.Drawing
[TabMouse]::SetProcessDPIAware() | Out-Null

$dotnetRoot = Join-Path $Root '.dotnet'
$app = Join-Path $Root 'artifacts\KBEA-Light\KBEA.exe'
$fixture = Join-Path $Root 'artifacts\tab-verify-fixture'
if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
New-Item (Join-Path $fixture 'space-a') -ItemType Directory -Force | Out-Null
New-Item (Join-Path $fixture 'space-b') -ItemType Directory -Force | Out-Null
New-Item (Join-Path $fixture 'local-c') -ItemType Directory -Force | Out-Null
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
    $start.EnvironmentVariables['DOTNET_ROOT'] = $dotnetRoot
    $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.Arguments = '"' + ((@('--config', $config)) -join '" "') + '"'
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

function Get-Button($window, [string]$text) {
    for ($attempt = 0; $attempt -lt 6; $attempt++) {
        $button = @(Get-Elements $window | Where-Object {
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -like "*$text*"
        }) | Select-Object -First 1
        if ($null -ne $button) { return $button }
        Start-Sleep -Milliseconds 400
    }
    throw "Button not found: $text"
}

function Click-Element($window, $element) {
    $null = [TabMouse]::Activate([IntPtr]$window.Current.NativeWindowHandle)
    $rect = $element.Current.BoundingRectangle
    [TabMouse]::Click([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
    Start-Sleep -Milliseconds 600
}

function Invoke-Button($window, [string]$text) {
    Click-Element $window (Get-Button $window $text)
}

function Get-TabItems($window) {
    return @(Get-Elements $window | Where-Object {
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::TabItem
    })
}

function Test-VisibleText($window, [string]$text) {
    return @(Get-Elements $window | Where-Object { $_.Current.Name -eq $text -and -not $_.Current.IsOffscreen }).Count -gt 0
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

$checks = 0
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++
}

# ===== Case 1: no workspaces -> welcome page =====
Write-Config $false
$process = Start-App
try {
    $window = Get-AppWindow $process
    Start-Sleep -Milliseconds 1200
    $welcome = -join ([char[]](0x4ECE, 0x4E00, 0x4E2A, 0x76EE, 0x5F55, 0x6216, 0x5DE5, 0x4F5C, 0x7A7A, 0x95F4, 0x5F00, 0x59CB))
    Assert-True (Test-VisibleText $window $welcome) 'Welcome page was not shown when no workspace exists.'
    $tabs = Get-TabItems $window
    Assert-True ($tabs.Count -le 1) "Expected at most the placeholder tab, found $($tabs.Count)."
    Write-Output 'PASS No workspace -> welcome page, no real tab created'
}
finally {
    if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    $process.Dispose()
}

# ===== Case 2: with workspaces -> first workspace opens, sources switch, sidebar exclusive =====
Write-Config $true
$process = Start-App
try {
    $window = Get-AppWindow $process
    Start-Sleep -Milliseconds 1500

    $newTabName = -join ([char[]](0x65B0, 0x5EFA, 0x9009, 0x9879, 0x5361))
    $spaceA = -join ([char[]](0x53, 0x70, 0x61, 0x63, 0x65, 0x20, 0x41))
    $spaceB = -join ([char[]](0x53, 0x70, 0x61, 0x63, 0x65, 0x20, 0x42))
    $welcome = -join ([char[]](0x4ECE, 0x4E00, 0x4E2A, 0x76EE, 0x5F55, 0x6216, 0x5DE5, 0x4F5C, 0x7A7A, 0x95F4, 0x5F00, 0x59CB))
    $workspaceTab = -join ([char[]](0x5DE5, 0x4F5C, 0x7A7A, 0x95F4))
    $localTab = -join ([char[]](0x672C, 0x5730, 0x76EE, 0x5F55))
    $remoteTab = -join ([char[]](0x5171, 0x4EAB, 0x76EE, 0x5F55))

    # Startup: welcome page, no tab auto-opened, sidebar defaults to the workspace section
    Assert-True (Test-VisibleText $window $welcome) 'The welcome page was not shown on startup.'
    $tabNames = @(Get-TabItems $window | ForEach-Object { $_.Current.Name })
    $realTabs = @($tabNames | Where-Object { $_ -and $_ -notlike "*$newTabName*" })
    Assert-True ($realTabs.Count -eq 0) "Startup must not open any tab, found: $($realTabs -join ' | ')."
    $wsState0 = Get-ToggleState $window $workspaceTab
    Assert-True ($wsState0 -eq 'On') "The sidebar does not default to the workspace section (state=$wsState0)."
    Write-Output 'PASS Startup shows the welcome page with the workspace sidebar'

    # User action: click the "+" placeholder to create a tab -> it follows the first workspace
    $plusTab = @(Get-TabItems $window | Where-Object { $_.Current.Name -like "*$newTabName*" }) | Select-Object -First 1
    Assert-True ($null -ne $plusTab) 'The placeholder new-tab button was not found.'
    $plusButton = @(Get-Elements $window | Where-Object {
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -eq $newTabName
    }) | Select-Object -First 1
    Assert-True ($null -ne $plusButton) 'The new-tab button was not found.'
    Click-Element $window $plusButton
    Start-Sleep -Milliseconds 1200
    Assert-True (Test-VisibleText $window $spaceA) 'The new tab did not follow the first workspace.'
    Write-Output 'PASS New tab follows the first workspace'

    # Tab card must carry a source badge
    $badgeText = -join ([char[]](0x7A7A, 0x95F4))   # workspace
    $localBadge = -join ([char[]](0x672C, 0x5730))   # local
    Assert-True (Test-VisibleText $window $badgeText) 'The workspace source badge was not rendered on the tab card.'
    Write-Output 'PASS Tab card shows the workspace source badge'

    # Sidebar: the three toggles must be mutually exclusive
    Invoke-Button $window $localTab
    Start-Sleep -Milliseconds 800
    $wsState = Get-ToggleState $window $workspaceTab
    $locState = Get-ToggleState $window $localTab
    $remState = Get-ToggleState $window $remoteTab
    Assert-True ($locState -eq 'On') "Local sidebar toggle is not on (state=$locState)."
    Assert-True ($wsState -eq 'Off') "Workspace sidebar toggle is still on (state=$wsState)."
    Assert-True ($remState -eq 'Off') "Remote sidebar toggle is still on (state=$remState)."
    Write-Output 'PASS Sidebar sections are mutually exclusive (local selected)'

    Invoke-Button $window $remoteTab
    Start-Sleep -Milliseconds 800
    $wsState = Get-ToggleState $window $workspaceTab
    $locState = Get-ToggleState $window $localTab
    $remState = Get-ToggleState $window $remoteTab
    Assert-True ($remState -eq 'On') "Remote sidebar toggle is not on (state=$remState)."
    Assert-True ($wsState -eq 'Off') "Workspace sidebar toggle is still on (state=$wsState)."
    Assert-True ($locState -eq 'Off') "Local sidebar toggle is still on (state=$locState)."
    Write-Output 'PASS Sidebar sections are mutually exclusive (remote selected)'

    Invoke-Button $window $workspaceTab
    Start-Sleep -Milliseconds 800
    $wsState = Get-ToggleState $window $workspaceTab
    $locState = Get-ToggleState $window $localTab
    $remState = Get-ToggleState $window $remoteTab
    Assert-True ($wsState -eq 'On') "Workspace sidebar toggle is not on (state=$wsState)."
    Assert-True ($locState -eq 'Off') "Local sidebar toggle is still on (state=$locState)."
    Assert-True ($remState -eq 'Off') "Remote sidebar toggle is still on (state=$remState)."
    Write-Output 'PASS Sidebar sections are mutually exclusive (workspace selected)'

    # Source switching now lives in the sidebar: pick a local folder entry there
    Invoke-Button $window $localTab
    Start-Sleep -Milliseconds 600
    $desktop = -join ([char[]](0x684C, 0x9762))   # "Desktop" quick folder
    $desktopItem = @(Get-Elements $window | Where-Object {
        $_.Current.Name -like "*$desktop*" -and -not $_.Current.IsOffscreen
    }) | Select-Object -First 1
    Assert-True ($null -ne $desktopItem) 'The local quick folder entry was not found in the sidebar.'
    Click-Element $window $desktopItem
    Start-Sleep -Milliseconds 1200

    # The tab source badge must now read "local" and the sidebar must follow
    Assert-True (Test-VisibleText $window $localBadge) 'The tab card did not switch to the local source badge.'
    $locState = Get-ToggleState $window $localTab
    Assert-True ($locState -eq 'On') "Sidebar did not follow the tab to the local section (state=$locState)."
    Write-Output 'PASS Tab switches to the local source from the sidebar'

    # Switching back to a workspace via the sidebar tree must work (the original defect)
    Invoke-Button $window $workspaceTab
    Start-Sleep -Milliseconds 600
    $spaceATreeItem = @(Get-Elements $window | Where-Object {
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::TreeItem -and $_.Current.Name -eq $spaceA
    }) | Select-Object -First 1
    Assert-True ($null -ne $spaceATreeItem) 'Space A tree item was not found in the workspace tree.'
    Click-Element $window $spaceATreeItem
    Start-Sleep -Milliseconds 1200
    Assert-True (Test-VisibleText $window $spaceA) 'The tab did not switch back to the workspace.'
    Assert-True (Test-VisibleText $window $badgeText) 'The tab card did not restore the workspace source badge.'
    $wsState = Get-ToggleState $window $workspaceTab
    Assert-True ($wsState -eq 'On') "The sidebar did not follow the tab back to the workspace section (state=$wsState)."
    Write-Output 'PASS Tab can switch back from local to a workspace'

    # The workspace config must not be corrupted by local switching
    $saved = Get-Content $config -Raw | ConvertFrom-Json
    $spaceAEntry = @($saved.Spaces | Where-Object { $_.Name -eq 'Space A' })[0]
    $spaceBEntry = @($saved.Spaces | Where-Object { $_.Name -eq 'Space B' })[0]
    Assert-True ($spaceAEntry.Path -eq (Join-Path $fixture 'space-a')) "Space A path was corrupted: $($spaceAEntry.Path)."
    Assert-True ($spaceBEntry.Path -eq (Join-Path $fixture 'space-b')) "Space B path was corrupted: $($spaceBEntry.Path)."
    Write-Output 'PASS Workspace configuration is not corrupted by source switching'
}
finally {
    if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    $process.Dispose()
}

Write-Output ""
Write-Output "All $checks tab checks passed."
