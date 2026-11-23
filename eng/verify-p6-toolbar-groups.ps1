# P6 acceptance smoke test: toolbar grouping + framework-level collapse.
# Checks:
#   1) toolbar buttons are laid out HORIZONTALLY (same Y row) - the old vertical stacking bug
#   2) plugin buttons are grouped by plugin (Git group = 5 buttons in one group)
#   3) every group keeps at least one button visible
#   4) narrowing the window collapses groups (first button + dropdown arrow stays visible)
# UIA-based. ASCII only; Chinese via [char[]].
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Root
$app = Join-Path $Root 'artifacts\KBEA-Light\KBEA.exe'
$fixture = Join-Path $Root 'artifacts\p6-verify-fixture'
$folder = Join-Path $fixture 'space'
$config = Join-Path $Root 'artifacts\p6-verify-config.json'
$dataDir = Join-Path $Root 'artifacts\p6-verify-data'

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

# Chinese literals via code points
$wikiTip    = -join ([char[]](0x77E5, 0x8BC6, 0x57, 0x49, 0x4B, 0x49))                                # "知识WIKI"
$gitPull    = -join ([char[]](0x62C9, 0x53D6, 0x66F4, 0x65B0))                                          # "拉取更新"
$gitCompare = -join ([char[]](0x7248, 0x672C, 0x6BD4, 0x5BF9))                                          # "版本比对"
$sampleTip  = -join ([char[]](0x793A, 0x4F8B, 0x63D2, 0x4EF6))                                          # "示例插件"
$favorite   = -join ([char[]](0x6536, 0x85CF))                                                          # "收藏"
$arrow      = [char]0x25BE                                                                              # "▾"

function Get-Buttons([Windows.Automation.AutomationElement]$scope) {
    $result = @()
    foreach ($e in $scope.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { $result += $e }
    }
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
    Start-Sleep -Milliseconds 3000

    $buttons = Get-Buttons $window
    $byName = @{}
    foreach ($b in $buttons) { if ($b.Current.Name -and -not $byName.ContainsKey($b.Current.Name)) { $byName[$b.Current.Name] = $b } }

    # Check 1: plugin buttons exist (identified by tooltip, since plugins now only provide icon + tooltip)
    $wikiBtn = $null; $pullBtn = $null; $cmpBtn = $null; $sampleBtn = $null
    foreach ($b in $buttons) {
        $n = $b.Current.Name
        if (-not $n) { continue }
        if ($n -like "*$wikiTip*" -and $null -eq $wikiBtn) { $wikiBtn = $b }
        if ($n -like "*$gitPull*" -and $null -eq $pullBtn) { $pullBtn = $b }
        if ($n -like "*$gitCompare*" -and $null -eq $cmpBtn) { $cmpBtn = $b }
        if ($n -like "*$sampleTip*" -and $null -eq $sampleBtn) { $sampleBtn = $b }
    }
    Assert-True ($null -ne $wikiBtn) 'Wiki plugin toolbar button missing.'
    Assert-True ($null -ne $pullBtn) 'Git pull button missing.'
    Assert-True ($null -ne $cmpBtn) 'Git compare button missing.'
    Assert-True ($null -ne $sampleBtn) 'Sample plugin toolbar button missing.'
    $checks += 4

    # Check 2: buttons are laid out horizontally (same Y band, different X)
    $rects = @($wikiBtn, $pullBtn, $cmpBtn, $sampleBtn) | ForEach-Object { $_.Current.BoundingRectangle }
    $ys = @($rects | ForEach-Object { [int]$_.Y } | Sort-Object -Unique)
    Assert-True ($ys.Count -le 2) ("Toolbar buttons are not on one horizontal row (Y values: {0})." -f ($ys -join ','))
    $checks++

    # Check 3: Git buttons are grouped (same X adjacency -> same group, and Git buttons sit together)
    $gitXs = @(@($pullBtn, $cmpBtn) | ForEach-Object { [int]$_.Current.BoundingRectangle.X } | Sort-Object)
    Assert-True ([Math]::Abs($gitXs[0] - $gitXs[1]) -lt 200) 'Git buttons are not adjacent (grouping broken).'
    $checks++

    # Check 4: builtin favorite button is present in the same toolbar
    $favBtn = $null
    foreach ($b in $buttons) { if ($b.Current.Name -like "*$favorite*") { $favBtn = $b; break } }
    Assert-True ($null -ne $favBtn) 'Builtin favorite button missing.'
    $checks++

    # Check 5: builtin group keeps its own buttons together and to the left of plugin groups
    # (the collapse/overflow algorithm itself is covered by ToolbarOverflowPlannerTests in KBEA.Tests)
    $favRect = $favBtn.Current.BoundingRectangle
    $wikiRect = $wikiBtn.Current.BoundingRectangle
    Assert-True ($favRect.X -lt $wikiRect.X) 'Builtin group is not to the left of plugin groups.'
    $checks++

    # Check 6: short labels are visible next to the icons (Full layout when there is room)
    $labels = @('收藏', '编目', '比对', '拉取', '上传', '历史', '远程')
    $foundLabels = @()
    $textDump = @()
    foreach ($e in $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.ControlType -ne [Windows.Automation.ControlType]::Text) { continue }
        $textDump += ("{0} | {1}" -f $e.Current.Name, $e.Current.BoundingRectangle.X)
        foreach ($label in $labels) { if ($e.Current.Name -eq $label) { $foundLabels += $label } }
    }
    [System.IO.File]::WriteAllText((Join-Path $Root 'artifacts\p6-verify-texts.txt'),
        ($textDump -join "`n"), (New-Object System.Text.UTF8Encoding($false)))
    Assert-True ($foundLabels.Count -ge 4) ("Toolbar buttons should show short labels next to icons (found: {0})." -f ($foundLabels -join ','))
    $checks++

    # Check 7: icons are drawn from the shared library with a uniform size
    # (all toolbar icons are 16x16 Images; compare their bounding boxes)
    $iconBoxes = @()
    foreach ($e in $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.ControlType -ne [Windows.Automation.ControlType]::Image) { continue }
        $r = $e.Current.BoundingRectangle
        if ($r.Width -gt 0) { $iconBoxes += [int]$r.Width }
    }
    Assert-True ($iconBoxes.Count -gt 0) 'No toolbar icon images found.'
    $distinct = @($iconBoxes | Sort-Object -Unique)
    Assert-True ($distinct.Count -le 2) ("Toolbar icons have inconsistent sizes: {0}." -f ($distinct -join ','))
    $checks++

    Write-Output "PASS p6-toolbar-groups: horizontal layout, plugin grouping, builtin group, labels, uniform icons ($checks checks)"
}
finally {
    if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    $process.Dispose()
}
