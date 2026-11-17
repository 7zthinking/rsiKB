param([string]$Root = (Split-Path $PSScriptRoot -Parent))
# One-shot: dump the workspace tree items of the running smoke config.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$dotnetRoot = Join-Path $Root '.dotnet'
$app = Join-Path $Root 'artifacts\KBEA-Light\KBEA.exe'
$config = Join-Path $Root 'artifacts\ui-smoke-config.json'
if (-not (Test-Path $config)) {
    $fixture = Join-Path $Root 'artifacts\ui-smoke-fixture'
    New-Item (Join-Path $fixture 'sub') -ItemType Directory -Force | Out-Null
    $seed = @{
        AuthorName = 'Smoke'; AuthorEmail = 'smoke@localhost'; Theme = 'Light'; RsyncExecutable = 'tools/rsync/rsync.exe'
        ArchiveRoot = ''; AutoDetect = $false; FavoriteFolders = @(); RecentLocalFolders = @(); RemoteLocations = @()
        Spaces = @(@{ Id = [Guid]::NewGuid(); Name = 'Smoke Fixture'; Path = $fixture; RemoteUrl = ''; AutoSync = $false })
        Directories = @(); Workspaces = @()
    }
    $seed | ConvertTo-Json -Depth 6 | Set-Content $config -Encoding UTF8
}

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $app
$start.WorkingDirectory = Split-Path $app -Parent
$start.UseShellExecute = $false
$start.EnvironmentVariables['DOTNET_ROOT'] = $dotnetRoot
$start.Arguments = '"' + ((@('--config', $config)) -join '" "') + '"'
$process = [Diagnostics.Process]::Start($start)

try {
    $window = $null
    for ($i = 0; $i -lt 40 -and $null -eq $window; $i++) {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
        foreach ($w in $windows) { if ($w.Current.ProcessId -eq $process.Id) { $window = $w; break } }
        Start-Sleep -Milliseconds 300
    }
    if ($null -eq $window) { throw 'main window not found' }
    Start-Sleep -Milliseconds 2000
    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    Write-Output "--- all TreeItem elements ---"
    foreach ($e in $elements) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::TreeItem) {
            Write-Output ("  TreeItem Name='{0}' offscreen={1}" -f $e.Current.Name, $e.Current.IsOffscreen)
        }
    }
    Write-Output "--- all Tree elements ---"
    foreach ($e in $elements) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Tree) {
            Write-Output ("  Tree Name='{0}'" -f $e.Current.Name)
        }
    }
    Write-Output "--- visible text containing 'Smoke' ---"
    foreach ($e in $elements) {
        if ($e.Current.Name -like '*Smoke*') {
            Write-Output ("  {0} Name='{1}'" -f $e.Current.ControlType.ProgrammaticName, $e.Current.Name)
        }
    }
}
finally {
    if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    $process.Dispose()
}
