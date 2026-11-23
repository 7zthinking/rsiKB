# P2 acceptance smoke test: kbea-plugin-git loads in the real app, registers toolbar
# buttons via IToolbarContribution, and IGitOperations snapshot refresh works end-to-end.
# UIA-based (same approach as verify-core.ps1). ASCII only; Chinese via [char[]].
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Root
$app = Join-Path $Root 'artifacts\KBEA-Light\KBEA.exe'
$fixture = Join-Path $Root 'artifacts\git-verify-fixture'
$repo = Join-Path $fixture 'repo'
$config = Join-Path $Root 'artifacts\git-verify-config.json'

# Fixture: a local folder that is a real git repository with one commit
if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
New-Item $repo -ItemType Directory -Force | Out-Null
git -C $repo -c init.defaultBranch=main init | Out-Null
git -C $repo -c user.name=Smoke -c user.email=smoke@localhost commit --allow-empty -m seed | Out-Null

$seed = @{
    AuthorName = 'Smoke'; AuthorEmail = 'smoke@localhost'; Theme = 'Light'; RsyncExecutable = 'tools/rsync/rsync.exe'
    ArchiveRoot = ''; AutoDetect = $false; FavoriteFolders = @(); RecentLocalFolders = @(); RemoteLocations = @()
    Spaces = @(); Directories = @(); Workspaces = @()
}
$seed | ConvertTo-Json -Depth 6 | Set-Content $config -Encoding UTF8

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $app
$start.WorkingDirectory = Split-Path $app -Parent
$start.UseShellExecute = $false
$confArg = '--config "' + $config + '"'
$openArg = '--open "' + $repo + '"'
$start.Arguments = $confArg + ' ' + $openArg
$dataDir = Join-Path $Root 'artifacts\git-verify-data'
if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }
New-Item $dataDir -ItemType Directory -Force | Out-Null
$start.EnvironmentVariables['KBEA_DATA_DIR'] = $dataDir
$process = [Diagnostics.Process]::Start($start)

$checks = 0
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++
}

try {
    $window = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw "Application exited early with code $($process.ExitCode)." }
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
        foreach ($w in $windows) { if ($w.Current.ProcessId -eq $process.Id) { $window = $w } }
        if ($null -ne $window) { break }
        Start-Sleep -Milliseconds 300
    }
    if ($null -eq $window) { throw 'Application main window was not found.' }
    Start-Sleep -Milliseconds 2500

    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $names = @($elements | ForEach-Object { $_.Current.Name } | Where-Object { $_ })

    # Check 1: git plugin registered its toolbar buttons (button tooltip/name carries text)
    $pullBtn = -join ([char[]](0x62C9, 0x53D6, 0x66F4, 0x65B0))          # "拉取更新"
    Assert-True (@($names | Where-Object { $_ -like "*$pullBtn*" }).Count -gt 0) 'Git plugin toolbar button (pull) was not registered.'

    # Check 2: IGitOperations ran in the real app: branch "main" is displayed (git status line "已同步 · main")
    if (@($names | Where-Object { $_ -like '*main*' }).Count -eq 0) {
        Set-Content (Join-Path $Root 'artifacts\git-verify-names.txt') ($names -join "`n") -Encoding UTF8
        throw 'Git snapshot did not report branch main (IGitOperations failed?). See artifacts\git-verify-names.txt.'
    }

    Write-Output "PASS kbea-plugin-git: toolbar buttons registered and git snapshot refreshed ($checks checks)"
}
finally {
    if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    $process.Dispose()
}
