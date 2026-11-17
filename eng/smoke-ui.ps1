param([string]$Root = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class SmokeMouse { [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra); [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); public static void Click(int x, int y) { System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); } public static void AltTap() { keybd_event(0x12, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(50); keybd_event(0x12, 0, 2, UIntPtr.Zero); } public static bool Activate(IntPtr hwnd) { for (int i = 0; i < 6; i++) { AltTap(); ShowWindow(hwnd, 3); SetForegroundWindow(hwnd); System.Threading.Thread.Sleep(300); if (GetForegroundWindow() == hwnd) return true; } return false; } }' -ReferencedAssemblies System.Windows.Forms, System.Drawing
# UIA 报告物理像素；非 DPI 感知进程的绝对坐标点击会因缩放落错位置
[SmokeMouse]::SetProcessDPIAware() | Out-Null

$dotnetRoot = Join-Path $Root '.dotnet'
$app = Join-Path $Root 'artifacts\KBEA-Light\KBEA.exe'
$config = Join-Path $Root 'artifacts\ui-smoke-config.json'
$plain = [IO.Path]::GetTempPath().TrimEnd('\')
$projectSubdirectory = Join-Path $Root 'src'
$fixture = Join-Path $Root 'artifacts\ui-smoke-fixture'
if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
$metadata = New-Item (Join-Path $fixture '.7z') -ItemType Directory -Force
Set-Content (Join-Path $metadata.FullName 'marker.txt') 'smoke metadata'
$seed = @{
    AuthorName = 'Smoke'; AuthorEmail = 'smoke@localhost'; Theme = 'Light'; RsyncExecutable = 'tools/rsync/rsync.exe'
    ArchiveRoot = ''; AutoDetect = $true; FavoriteFolders = @(); RecentLocalFolders = @(); RemoteLocations = @()
    Workspaces = @(@{ Id = [Guid]::NewGuid(); Name = 'Smoke Seed'; Categories = @(); Favorites = @(); Mounts = @(@{
        Id = [Guid]::NewGuid(); Name = 'Smoke Fixture'; Path = $fixture; RemoteUrl = ''; AutoSync = $false
    }) })
}
$seed | ConvertTo-Json -Depth 6 | Set-Content $config -Encoding UTF8

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $app
$start.WorkingDirectory = Split-Path $app -Parent
$start.UseShellExecute = $false
$start.EnvironmentVariables['DOTNET_ROOT'] = $dotnetRoot
$start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$start.Arguments = '"' + ((@('--config', $config, '--open', $projectSubdirectory)) -join '" "') + '"'
$process = [Diagnostics.Process]::Start($start)

function Get-AppWindow {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ($process.HasExited) { throw "Application exited early with code $($process.ExitCode)." }
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Children,
            [Windows.Automation.Condition]::TrueCondition)
        foreach ($window in $windows) {
            if ($window.Current.ProcessId -eq $process.Id) { return $window }
        }
        Start-Sleep -Milliseconds 300
    }
    throw 'Application main window was not found.'
}

function Get-Button([Windows.Automation.AutomationElement]$window, [string]$text) {
    # UIA 树在点击/视图切换后有刷新延迟，重试几次避免 flaky
    $button = $null
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        $button = @($elements | Where-Object {
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -like "*$text*"
        }) | Select-Object -First 1
        if ($null -ne $button) { return $button }
        Start-Sleep -Milliseconds 400
    }
    throw "Button not found: $text"
}

function Get-VisibleButton([Windows.Automation.AutomationElement]$window, [string]$text) {
    # Collapsed panels keep their buttons in the UIA tree (only IsOffscreen flips),
    # so "the panel switched" must be judged by visibility, not by element presence.
    for ($attempt = 0; $attempt -lt 6; $attempt++) {
        $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        $button = @($elements | Where-Object {
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

function Get-GitButtons([Windows.Automation.AutomationElement]$window) {
    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $buttons = @($elements | Where-Object {
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -like '*Git*'
    })
    if ($buttons.Count -eq 0) { throw 'Git buttons were not found.' }
    return $buttons
}

function Invoke-Button([Windows.Automation.AutomationElement]$window, [string]$text) {
    # 优先点可见的同名按钮：折叠面板里的按钮仍留在 UIA 树里（只是 IsOffscreen），
    # 按它们的坐标点击会落到别的控件上。
    $button = $null
    try { $button = Get-VisibleButton $window $text } catch { $button = Get-Button $window $text }
    # 统一走真实鼠标点击：
    # 1) 跨进程 Invoke 打开模态窗口后，UIA 树会残缺（COM 重入假象），后续枚举不可靠；
    # 2) InvokePattern.Invoke 偶发不触发 Command；
    # 3) ToggleButton 只有 TogglePattern，且 Toggle() 不触发 Click/Command。
    # 真实鼠标点击是唯一与用户行为一致、最可靠的方式。
    $null = [SmokeMouse]::Activate([IntPtr]$window.Current.NativeWindowHandle)
    $rect = $button.Current.BoundingRectangle
    [SmokeMouse]::Click([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
    Start-Sleep -Milliseconds 500
}

function Set-Address([Windows.Automation.AutomationElement]$window, [string]$path) {
    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $address = @($elements | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Edit }) | Select-Object -First 1
    if ($null -eq $address) { throw 'Address bar was not found.' }
    $value = $address.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue($path)
    $address.SetFocus()
    [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 1000
}

function Get-Dialog([string]$title) {
    # 只按名字精确匹配本进程的窗口。绝不 fallback 到"最后一个窗口"——
    # 那会把无名的辅助窗口（name=''、descendants=0）当成对话框返回，掩盖真实失败。
    $appWindows = @()
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.Condition]::TrueCondition)
        $appWindows = @($windows | Where-Object { $_.Current.ProcessId -eq $process.Id -and $_.Current.ControlType -eq [Windows.Automation.ControlType]::Window })
        $dialog = @($appWindows | Where-Object { $_.Current.Name -eq $title }) | Select-Object -First 1
        if ($null -ne $dialog) { return $dialog }
        Start-Sleep -Milliseconds 200
    }
    [Console]::WriteLine("DEBUG process=$($process.Id) appWindows=$($appWindows.Count)")
    [Console]::WriteLine("DEBUG appWindowNames=" + (@($appWindows | ForEach-Object { '[' + $_.Current.Name + ']' }) -join ' '))
    Add-Type -AssemblyName System.Drawing
    $bounds = [Windows.Automation.AutomationElement]::RootElement.Current.BoundingRectangle
    $bitmap = [Drawing.Bitmap]::new([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen(0, 0, 0, 0, $bitmap.Size)
    $bitmap.Save((Join-Path $Root 'artifacts\ui-dialog-failure.png'), [Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    throw "Dialog not found: $title"
}

function Dump-SidebarState($window, [string]$label) {
    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $names = @(
        (-join ([char[]](0x5DE5, 0x4F5C, 0x7A7A, 0x95F4))),
        (-join ([char[]](0x672C, 0x5730, 0x76EE, 0x5F55))),
        (-join ([char[]](0x5171, 0x4EAB, 0x76EE, 0x5F55)))
    )
    foreach ($name in $names) {
        $toggle = @($elements | Where-Object {
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -eq $name
        }) | Select-Object -First 1
        $state = 'n/a'
        if ($null -ne $toggle) {
            try { $state = $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } catch { $state = 'no-pattern' }
        }
        [Console]::WriteLine("DEBUG sidebar[$label] toggle[$name] = $state")
    }
    $newSpace = -join ([char[]](0x65B0, 0x5EFA, 0x7A7A, 0x95F4))
    foreach ($e in $elements) {
        if ($e.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $e.Current.Name -like "*$newSpace*") {
            $r = $e.Current.BoundingRectangle
            [Console]::WriteLine("DEBUG sidebar[$label] new-space btn offscreen={0} rect={1},{2} {3}x{4}" -f $e.Current.IsOffscreen, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
        }
    }
}

function Set-FirstEdit([Windows.Automation.AutomationElement]$window, [string]$text) {
    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $edit = @($elements | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Edit }) | Select-Object -First 1
    if ($null -eq $edit) { throw 'Dialog edit box was not found.' }
    $value = $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue($text)
}

function Test-VisibleText([Windows.Automation.AutomationElement]$window, [string]$text) {
    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    return @($elements | Where-Object { $_.Current.Name -eq $text -and -not $_.Current.IsOffscreen }).Count -gt 0
}

try {
    $window = Get-AppWindow
    # Startup now lands on the welcome page (no tab auto-opened).
    # Open the fixture space from the sidebar tree, like a user would.
    $fixtureSpace = 'Smoke Fixture'
    # 窗口刚出现时 UIA 全树枚举可能抛元素失效异常（实测），先延时并对单次枚举容错
    Start-Sleep -Milliseconds 2500
    $treeItem = $null
    for ($attempt = 0; $attempt -lt 15 -and $null -eq $treeItem; $attempt++) {
        try {
            $treeItems = @($window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | Where-Object {
                $_.Current.ControlType -eq [Windows.Automation.ControlType]::TreeItem -and $_.Current.Name -like "*$fixtureSpace*"
            })
            $treeItem = $treeItems | Select-Object -First 1
        } catch { $treeItem = $null }
        if ($null -eq $treeItem) { Start-Sleep -Milliseconds 500 }
    }
    if ($null -eq $treeItem) { throw 'The fixture space node was not found in the workspace tree.' }
    $treeItem.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1500
    $pullName = -join ([char[]](0x62C9, 0x53D6, 0x66F4, 0x65B0))
    $pull = Get-Button $window $pullName
    if (-not $pull.Current.IsEnabled) {
        throw 'Git pull was not enabled in the repository subdirectory.'
    }
    Write-Output "PASS Git discovery in: $projectSubdirectory"

    $gitConfig = -join ([char[]](0x47, 0x69, 0x74, 0x20, 0x8FDC, 0x7A0B, 0x914D, 0x7F6E))
    $gitDialogTitle = -join ([char[]](0x47, 0x69, 0x74, 0x20, 0x4ED3, 0x5E93))
    $cancel = -join ([char[]](0x53D6, 0x6D88))
    Invoke-Button $window $gitConfig
    $dialog = Get-Dialog $gitDialogTitle
    $branchPrefix = -join ([char[]](0x5F53, 0x524D, 0x5206, 0x652F))
    $dialogElements = $dialog.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    if (@($dialogElements | Where-Object { $_.Current.Name.StartsWith($branchPrefix) }).Count -eq 0) {
        throw 'Existing Git repository information was not displayed.'
    }
    Invoke-Button $dialog $cancel
    Write-Output 'PASS Existing Git repository information panel'

    $newTabName = -join ([char[]](0x65B0, 0x5EFA, 0x9009, 0x9879, 0x5361))
    $plusButtons = @($window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | Where-Object {
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -eq $newTabName
    })
    if ($plusButtons.Count -ne 1) { throw 'The new-tab button was not uniquely identified.' }
    $plusButtons[0].GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1000
    # 新标签默认跟随第一个工作空间（无工作空间时才退到"我的电脑"）
    $fixtureName = 'Smoke Fixture'
    $tabItems = @($window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | Where-Object {
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::TabItem
    })
    $realTabs = @($tabItems | Where-Object { $_.Current.Name -and $_.Current.Name -notlike "*$newTabName*" })
    if ($realTabs.Count -lt 1) { throw 'The new tab was not created.' }
    if (-not (@($realTabs | Where-Object { $_.Current.Name -like "*$fixtureName*" }).Count -ge 1)) {
        throw 'The new tab did not follow the first workspace.'
    }
    Write-Output 'PASS New tab follows the first workspace'

    $windowPattern = $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)
    $windowPattern.Close()
    if (-not $process.WaitForExit(3000)) { throw 'Repository test instance did not close.' }
    $process.Dispose()
    $start.Arguments = '"' + ((@('--config', $config, '--open', $plain)) -join '" "') + '"'
    $process = [Diagnostics.Process]::Start($start)
    $window = Get-AppWindow
    $pull = Get-Button $window $pullName
    if ($pull.Current.IsEnabled) {
        throw 'Git pull remained enabled in a non-repository directory.'
    }
    Write-Output "PASS Git pull disabled in: $plain"

    $remote = -join ([char[]](0x5171, 0x4EAB, 0x76EE, 0x5F55))
    $local = -join ([char[]](0x672C, 0x5730, 0x76EE, 0x5F55))
    $workspace = -join ([char[]](0x5DE5, 0x4F5C, 0x7A7A, 0x95F4))
    $addRemote = -join ([char[]](0x6DFB, 0x52A0, 0x8FDC, 0x7A0B, 0x4F4D, 0x7F6E))
    Invoke-Button $window $remote
    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $remoteText = @($elements | Where-Object {
        $_.Current.Name -like "*$addRemote*" -and -not $_.Current.IsOffscreen
    }) | Select-Object -First 1
    if ($null -eq $remoteText) { throw 'Remote sidebar was not displayed.' }
    Write-Output 'PASS Remote sidebar is visible'
    Invoke-Button $window $local
    # 连续 AltTap 前台切换后，最后一次 ToggleButton 点击偶发不触发 Command；
    # 工具条（新建空间等）仅在 IsWorkspaceSidebar 时可见，以它为准验证并重试。
    $newWorkspace = -join ([char[]](0x65B0, 0x5EFA, 0x7A7A, 0x95F4))
    $workspaceReady = $false
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try { $null = Get-VisibleButton $window $newWorkspace; $workspaceReady = $true; break }
        catch { Invoke-Button $window $workspace; Start-Sleep -Milliseconds 600 }
    }
    if (-not $workspaceReady) { throw 'Workspace toolbar did not appear after selecting the workspace tab.' }
    Write-Output 'PASS All three sidebar sections can be selected'

    $newWorkspaceTitle = -join ([char[]](0x65B0, 0x5EFA, 0x7A7A, 0x95F4))
    $createWorkspace = -join ([char[]](0x521B, 0x5EFA, 0x7A7A, 0x95F4))
    # 真实鼠标点击偶发不触发 Command（与前台切换时序相关），重试直到对话框出现。
    # 每次点击前打印按钮状态，便于定位点击落空的原因。
    $dialog = $null
    for ($attempt = 0; $attempt -lt 5 -and $null -eq $dialog; $attempt++) {
        Dump-SidebarState $window "click-$attempt"
        $probe = Get-VisibleButton $window $newWorkspace
        $pr = $probe.Current.BoundingRectangle
        [Console]::WriteLine("DEBUG click-$attempt btn rect={0},{1} {2}x{3} enabled={4}" -f [int]$pr.X, [int]$pr.Y, [int]$pr.Width, [int]$pr.Height, $probe.Current.IsEnabled)
        Invoke-Button $window $newWorkspace
        try { $dialog = Get-Dialog $newWorkspaceTitle } catch { Start-Sleep -Milliseconds 800; $dialog = $null }
    }
    if ($null -eq $dialog) { throw "Dialog not found: $newWorkspaceTitle" }
    $selectFolder = -join ([char[]](0x9009, 0x62E9, 0x6587, 0x4EF6, 0x5939))
    $gitButton = -join ([char[]](0x5173, 0x8054, 0x20, 0x47, 0x69, 0x74, 0x20, 0x4ED3, 0x5E93))
    $dialogElements = $null
    # 模态对话框打开初期 UIA 内部树短暂残缺（COM 重入），重新获取引用并重试枚举。
    # 关键：复用已经拿到的 $dialog 元素，不要再调 Get-Dialog —— 跨进程枚举过一次之后，
    # RootElement 的 Descendants 查询会返回残缺的树，把对话框整个漏掉。
    $dialogReady = $false
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        try {
            [Console]::WriteLine("DEBUG dialog attempt=$attempt name='$($dialog.Current.Name)'")
            $dialogElements = $dialog.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
            [Console]::WriteLine("DEBUG dialog attempt=$attempt descendants=$($dialogElements.Count)")
            $null = Get-Button $dialog $selectFolder
            $null = Get-Button $dialog $gitButton
            $dialogReady = $true
            break
        }
        catch {
            [Console]::WriteLine("DEBUG dialog attempt=$attempt failed: $($_.Exception.Message)")
            Start-Sleep -Milliseconds 700
            # 重新取一次对话框引用（元素可能已被重建）
            try { $dialog = Get-Dialog $newWorkspaceTitle; [Console]::WriteLine("DEBUG dialog reacquired") } catch { [Console]::WriteLine("DEBUG dialog reacquire failed") }
        }
    }
    if (-not $dialogReady) { throw 'Workspace dialog internals were not visible.' }
    if (@($dialogElements | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::RadioButton }).Count -ne 0) {
        throw 'Legacy workspace mount choices are still displayed.'
    }
    Set-FirstEdit $dialog 'Smoke Space'
    if ((Get-Button $dialog $createWorkspace).Current.IsEnabled) { throw 'Create should be disabled before selecting a folder.' }
    Invoke-Button $dialog $cancel
    if (-not (Test-VisibleText $window 'Smoke Seed')) { throw 'Workspace fixture was not displayed.' }
    Write-Output 'PASS Workspace panel validation and toolbar'

    $editWorkspace = [string][char]0x270E
    $workspaceInfo = -join ([char[]](0x5DE5, 0x4F5C, 0x7A7A, 0x95F4, 0x4FE1, 0x606F))
    $saveChanges = -join ([char[]](0x4FDD, 0x5B58, 0x4FEE, 0x6539))
    Invoke-Button $window $editWorkspace
    $dialog = Get-Dialog $workspaceInfo
    Set-FirstEdit $dialog 'Smoke Renamed'
    Invoke-Button $dialog $saveChanges
    Start-Sleep -Milliseconds 700
    if (-not (Test-VisibleText $window 'Smoke Renamed')) { throw 'Workspace was not renamed.' }
    Write-Output 'PASS Workspace rename'

    $deleteWorkspace = -join ([char[]](0x5220, 0x9664, 0x5DE5, 0x4F5C, 0x7A7A, 0x95F4))
    $confirmTitle = -join ([char[]](0x8BF7, 0x786E, 0x8BA4))
    $yes = -join ([char[]](0x662F))
    Invoke-Button $window $editWorkspace
    $dialog = Get-Dialog $workspaceInfo
    Invoke-Button $dialog $deleteWorkspace
    $confirmation = Get-Dialog $confirmTitle
    Invoke-Button $confirmation $yes
    Start-Sleep -Milliseconds 700
    if (Test-VisibleText $window 'Smoke Renamed') { throw 'Workspace was not deleted.' }
    if (-not (Test-Path $fixture)) { throw 'Workspace deletion removed the physical folder.' }
    if (Test-Path (Join-Path $fixture '.7z')) { throw 'Workspace metadata was not deleted.' }
    Write-Output 'PASS Workspace deletion preserves files and removes metadata'
}
finally {
    if (-not $process.HasExited) {
        try {
            $window = Get-AppWindow
            $pattern = $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)
            $pattern.Close()
            if (-not $process.WaitForExit(3000)) { $process.Kill() }
        }
        catch {
            if (-not $process.HasExited) { $process.Kill() }
        }
    }
    $process.Dispose()
}
