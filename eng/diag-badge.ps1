# One-shot UIA dump: find any element named like the source badge.
# ASCII only.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();' -Name DPI -Namespace W2
[W2.DPI]::SetProcessDPIAware() | Out-Null

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Root
$app = Join-Path $Root 'artifacts\KBEA-Light\KBEA.exe'
$fixture = Join-Path $Root 'artifacts\tab-verify-fixture'
$config = Join-Path $Root 'artifacts\tab-verify-config.json'

if (-not (Test-Path $config)) { Write-Output 'no fixture config; run verify-tabs first'; exit 1 }

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $app
$start.WorkingDirectory = Split-Path $app -Parent
$start.UseShellExecute = $false
$start.Arguments = '"' + ((@('--config', $config)) -join '" "') + '"'
$p = [Diagnostics.Process]::Start($start)
Start-Sleep -Seconds 6

$window = $null
$rootEl = [Windows.Automation.AutomationElement]::RootElement
$windows = $rootEl.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
foreach ($w in $windows) { if ($w.Current.ProcessId -eq $p.Id) { $window = $w } }
if ($null -eq $window) { Write-Output 'window not found'; Stop-Process -Id $p.Id -Force; exit 1 }

# Click the plus placeholder tab (same as verify-tabs Case 2)
$all0 = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
$plus = $null
foreach ($e in $all0) { if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $e.Current.Name -eq ([string][char]0x65B0 + [string][char]0x5EFA + [string][char]0x9009 + [string][char]0x9879 + [string][char]0x5361)) { $plus = $e } }
if ($null -eq $plus) { Write-Output 'plus placeholder not found'; Stop-Process -Id $p.Id -Force; exit 1 }
$rect = $plus.Current.BoundingRectangle
$cx = [int]($rect.X + $rect.Width / 2); $cy = [int]($rect.Y + $rect.Height / 2)
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);' -Name U32 -Namespace W
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);' -Name U32b -Namespace W
[W.U32b]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300
[System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($cx, $cy)
[W.U32]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
[W.U32]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Seconds 3

# Fallback: InvokePattern (real button invoke)
$all1 = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
$plus2 = $null
foreach ($e in $all1) { if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $e.Current.Name -eq ([string][char]0x65B0 + [string][char]0x5EFA + [string][char]0x9009 + [string][char]0x9879 + [string][char]0x5361)) { $plus2 = $e } }
if ($null -ne $plus2) {
    try {
        $inv = $plus2.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
        $inv.Invoke()
        Write-Output 'invoked via InvokePattern'
    } catch { Write-Output ('invoke failed: ' + $_.Exception.Message) }
}
Start-Sleep -Seconds 3

$all = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
Write-Output ("total elements: " + $all.Count)
foreach ($e in $all) {
    Write-Output ("EL name='" + $e.Current.Name + "' type=" + $e.Current.ControlType.ProgrammaticName + " offscreen=" + $e.Current.IsOffscreen)
}
$tabs = @($all | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::TabItem })
Write-Output ("tabs after click: " + $tabs.Count)
foreach ($t in $tabs) {
    Write-Output ("TAB name='" + $t.Current.Name + "' offscreen=" + $t.Current.IsOffscreen)
    $kids = $t.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    foreach ($k in $kids) {
        Write-Output ("  child name='" + $k.Current.Name + "' type=" + $k.Current.ControlType.ProgrammaticName)
    }
}
Write-Output '--- crash log tail ---'
Get-Content "$env:TEMP\kbea-crash.log" -ErrorAction SilentlyContinue | Select-Object -Last 12
Stop-Process -Id $p.Id -Force
Write-Output 'done'
