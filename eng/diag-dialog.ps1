param([string]$Root = (Split-Path $PSScriptRoot -Parent))

# Replicates the smoke test's second phase to find why the "new space" dialog never opens.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class DiagMouse { [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra); [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); public static void Click(int x, int y) { System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); } public static void AltTap() { keybd_event(0x12, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(50); keybd_event(0x12, 0, 2, UIntPtr.Zero); } public static bool Activate(IntPtr hwnd) { for (int i = 0; i < 6; i++) { AltTap(); ShowWindow(hwnd, 3); SetForegroundWindow(hwnd); System.Threading.Thread.Sleep(300); if (GetForegroundWindow() == hwnd) return true; } return false; } }' -ReferencedAssemblies System.Windows.Forms, System.Drawing
[DiagMouse]::SetProcessDPIAware() | Out-Null

$dotnetRoot = Join-Path $Root '.dotnet'
$app = Join-Path $Root 'artifacts\KBEA-Light\KBEA.exe'
$config = Join-Path $Root 'artifacts\diag-config.json'
$plain = [IO.Path]::GetTempPath().TrimEnd('\')

$seed = @{
    AuthorName = 'Diag'; AuthorEmail = 'diag@localhost'; Theme = 'Light'; RsyncExecutable = 'tools/rsync/rsync.exe'
    ArchiveRoot = ''; AutoDetect = $false; FavoriteFolders = @(); RecentLocalFolders = @(); RemoteLocations = @()
    Spaces = @(@{ Id = [Guid]::NewGuid(); Name = 'Diag Space'; Path = (Join-Path $Root 'artifacts\diag-fixture'); RemoteUrl = ''; RemoteKind = 0; DirectoryId = $null; AutoSync = $false; LastSyncAt = $null })
    Directories = @(); Workspaces = @()
}
$seed | ConvertTo-Json -Depth 6 | Set-Content $config -Encoding UTF8

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $app
$start.WorkingDirectory = Split-Path $app -Parent
$start.UseShellExecute = $false
$start.EnvironmentVariables['DOTNET_ROOT'] = $dotnetRoot
$start.Arguments = '"' + ((@('--config', $config, '--open', $plain)) -join '" "') + '"'
$process = [Diagnostics.Process]::Start($start)

function Get-AppWindow {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
        foreach ($window in $windows) { if ($window.Current.ProcessId -eq $process.Id) { return $window } }
        Start-Sleep -Milliseconds 300
    }
    throw 'Main window not found.'
}

function Get-Elements($window) {
    return $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
}

function Get-VisibleButton($window, [string]$text) {
    for ($attempt = 0; $attempt -lt 6; $attempt++) {
        $button = @(Get-Elements $window | Where-Object {
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and
            ($_.Current.Name -like "*$text*") -and
            (-not $_.Current.IsOffscreen) -and
            ($_.Current.BoundingRectangle.Width -gt 0) -and
            ($_.Current.BoundingRectangle.Height -gt 0)
        }) | Select-Object -First 1
        if ($null -ne $button) { return $button }
        Start-Sleep -Milliseconds 400
    }
    throw "Visible button not found: $text"
}

function Click-Button($window, [string]$text) {
    $button = Get-VisibleButton $window $text
    $rect = $button.Current.BoundingRectangle
    $x = [int]($rect.X + $rect.Width / 2)
    $y = [int]($rect.Y + $rect.Height / 2)
    $activated = [DiagMouse]::Activate([IntPtr]$window.Current.NativeWindowHandle)
    Write-Output ("  click '{0}' at {1},{2} activated={3}" -f $text, $x, $y, $activated)
    [DiagMouse]::Click($x, $y)
    Start-Sleep -Milliseconds 800
}

function Dump-State($window, [string]$label) {
    Write-Output "--- $label ---"
    $names = @(
        (-join ([char[]](0x5DE5, 0x4F5C, 0x7A7A, 0x95F4))),
        (-join ([char[]](0x672C, 0x5730, 0x76EE, 0x5F55))),
        (-join ([char[]](0x5171, 0x4EAB, 0x76EE, 0x5F55)))
    )
    foreach ($name in $names) {
        $toggle = @(Get-Elements $window | Where-Object {
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -eq $name
        }) | Select-Object -First 1
        $state = 'n/a'
        if ($null -ne $toggle) {
            try { $state = $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } catch { $state = 'no-pattern' }
        }
        Write-Output ("  toggle '{0}' = {1}" -f $name, $state)
    }
    $appWindows = @([Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ProcessId -eq $process.Id -and $_.Current.ControlType -eq [Windows.Automation.ControlType]::Window })
    Write-Output ("  appWindows={0}: {1}" -f $appWindows.Count, (@($appWindows | ForEach-Object { $_.Current.Name }) -join ' | '))
}

try {
    $tWorkspace = -join ([char[]](0x5DE5, 0x4F5C, 0x7A7A, 0x95F4))
    $tLocal = -join ([char[]](0x672C, 0x5730, 0x76EE, 0x5F55))
    $tRemote = -join ([char[]](0x5171, 0x4EAB, 0x76EE, 0x5F55))
    $tNewSpace = -join ([char[]](0x65B0, 0x5EFA, 0x7A7A, 0x95F4))

    $window = Get-AppWindow
    Start-Sleep -Milliseconds 2000
    Dump-State $window 'startup'

    Click-Button $window $tRemote
    Dump-State $window 'after remote'

    Click-Button $window $tLocal
    Dump-State $window 'after local'

    Click-Button $window $tWorkspace
    Dump-State $window 'after workspace'

    Write-Output "--- buttons matching new-space ---"
    foreach ($e in (Get-Elements $window)) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $e.Current.Name -like "*$tNewSpace*") {
            $r = $e.Current.BoundingRectangle
            Write-Output ("  '{0}' offscreen={1} rect={2},{3} {4}x{5}" -f $e.Current.Name, $e.Current.IsOffscreen, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
        }
    }

    Click-Button $window $tNewSpace
    Dump-State $window 'after new-space click'
}
finally {
    if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    $process.Dispose()
}
