$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dotnet = Join-Path $Root ".dotnet/dotnet.exe"
if (-not (Test-Path $Dotnet)) { $Dotnet = "dotnet" }

$Proj   = Join-Path $Root "src/KBEA.App/KBEA.App.csproj"
$WorkerProj = Join-Path $Root "src/KBEA.Worker/KBEA.Worker.csproj"
$PortDir = Join-Path $Root "artifacts/KBEA-Portable"
$LightDir = Join-Path $Root "artifacts/KBEA-Light"

# ---- Portable: 单文件 self-contained（给最终用户）----
Write-Host "`n[1/6] Publishing Portable (single-file self-contained)..." -ForegroundColor Cyan
& $Dotnet publish $Proj `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false --output $PortDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($File in @("LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.md")) {
    Copy-Item (Join-Path $Root $File) (Join-Path $PortDir $File) -Force
}
Copy-Item (Join-Path $Root "packaging/Portable-Readme.txt") (Join-Path $PortDir "使用说明.txt") -Force
Write-Host "  => $PortDir/KBEA.exe" -ForegroundColor Green

# ---- 品牌固化配置：branding/ 随两个产物分发（发行方改 branding.yaml + logo.png 即可换品牌）----
foreach ($Dir in @($PortDir, $LightDir)) {
    $BrandDir = Join-Path $Dir "branding"
    New-Item -ItemType Directory -Force -Path $BrandDir | Out-Null
    Copy-Item (Join-Path $Root "branding/*") $BrandDir -Force
}
Write-Host "  => branding/ (branding.yaml + logo.png)" -ForegroundColor Green

# ---- 发行方默认启用策略：plugins.defaults.json 随两个产物分发（决定开箱默认启用哪些插件）----
foreach ($Dir in @($PortDir, $LightDir)) {
    Copy-Item (Join-Path $Root "packaging/plugins.defaults.json") $Dir -Force
}
Write-Host "  => plugins.defaults.json (开箱默认启用的插件)" -ForegroundColor Green

# ---- Light: 多文件 self-contained（方便逐个签名 + 开发调试）----
Write-Host "`n[2/6] Publishing Light (multi-file self-contained)..." -ForegroundColor Cyan
& $Dotnet publish $Proj `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None -p:DebugSymbols=false --output $LightDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "  => $LightDir ($((Get-ChildItem $LightDir -Recurse -File).Count) files)" -ForegroundColor Green

# ---- Worker: 后台元数据服务（独立进程，单文件 self-contained）----
Write-Host "`n[3/6] Publishing Worker (single-file self-contained)..." -ForegroundColor Cyan
& $Dotnet publish $WorkerProj `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false --output $PortDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "  => $PortDir/KBEA.Worker.exe" -ForegroundColor Green

# Light 版同样放一份 Worker，前端按相对路径拉起
& $Dotnet publish $WorkerProj `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false --output $LightDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "  => $LightDir/KBEA.Worker.exe" -ForegroundColor Green

# ---- Git plugin: P2 Git 插件拷到两个 artifacts 的 plugins/kbea-plugin-git ----
# 宿主契约（KBEA.Core.dll）不能拷（与发布脚本顶部 Sample 插件同一原则）；
# 但 LibGit2Sharp 托管 + git2 本机库是插件私有依赖，必须随插件目录分发。
Write-Host "`n[4/6] Copying git plugin..." -ForegroundColor Cyan
& $Dotnet build (Join-Path $Root "src/KBEA.Plugin.Git/KBEA.Plugin.Git.csproj") --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$GitOut = Join-Path $Root "src/KBEA.Plugin.Git/bin/Release/net8.0"
foreach ($Dir in @($PortDir, $LightDir)) {
    $PluginDir = Join-Path $Dir "plugins/kbea-plugin-git"
    New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
    Copy-Item (Join-Path $GitOut "plugin.json") $PluginDir -Force
    Copy-Item (Join-Path $GitOut "KBEA.Plugin.Git.dll") $PluginDir -Force
    Copy-Item (Join-Path $GitOut "LibGit2Sharp.dll") $PluginDir -Force
    # Native libs: keep runtimes/win-x64/native (LibGit2Sharp resolver path) + flat copy as fallback
    $NativeDir = Join-Path $PluginDir "runtimes/win-x64/native"
    New-Item -ItemType Directory -Force -Path $NativeDir | Out-Null
    Copy-Item (Join-Path $GitOut "runtimes/win-x64/native/*.dll") $NativeDir -Force
    Copy-Item (Join-Path $GitOut "runtimes/win-x64/native/*.dll") $PluginDir -Force
}
Write-Host "  => plugins/kbea-plugin-git" -ForegroundColor Green

# ---- Wiki plugin: P3 知识 WIKI 插件拷到两个 artifacts 的 plugins/kbea-plugin-wiki ----
# 插件只带自己的 plugin.json + 入口 DLL（无第三方依赖）：宿主契约（KBEA.Core）由宿主进程提供，不能拷。
Write-Host "`n[5/6] Copying wiki plugin..." -ForegroundColor Cyan
& $Dotnet build (Join-Path $Root "src/KBEA.Plugin.Wiki/KBEA.Plugin.Wiki.csproj") --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$WikiOut = Join-Path $Root "src/KBEA.Plugin.Wiki/bin/Release/net8.0"
foreach ($Dir in @($PortDir, $LightDir)) {
    $PluginDir = Join-Path $Dir "plugins/kbea-plugin-wiki"
    New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
    Copy-Item (Join-Path $WikiOut "plugin.json") $PluginDir -Force
    Copy-Item (Join-Path $WikiOut "KBEA.Plugin.Wiki.dll") $PluginDir -Force
}
Write-Host "  => plugins/kbea-plugin-wiki" -ForegroundColor Green

# ---- Sample plugin: 打样插件（P1 验收用）拷到两个 artifacts 的 plugins/kbea-plugin-sample ----
# 插件只带自己的 plugin.json + 入口 DLL：宿主契约（KBEA.Core）由宿主进程提供，
# 不能拷插件目录里的 KBEA.Core.dll 副本，否则 ALC 内会加载出第二份类型导致转换失败。
Write-Host "`n[6/6] Copying sample plugin..." -ForegroundColor Cyan
& $Dotnet build (Join-Path $Root "src/KBEA.Plugin.Sample/KBEA.Plugin.Sample.csproj") --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$SampleOut = Join-Path $Root "src/KBEA.Plugin.Sample/bin/Release/net8.0"
foreach ($Dir in @($PortDir, $LightDir)) {
    $PluginDir = Join-Path $Dir "plugins/kbea-plugin-sample"
    New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
    Copy-Item (Join-Path $SampleOut "plugin.json") $PluginDir -Force
    Copy-Item (Join-Path $SampleOut "KBEA.Plugin.Sample.dll") $PluginDir -Force
}
Write-Host "  => plugins/kbea-plugin-sample" -ForegroundColor Green

# ---- 清理产物里已下线插件的残留目录（插件被移除后旧目录会留在产物中）----
foreach ($Dir in @($PortDir, $LightDir)) {
    $PluginsDir = Join-Path $Dir "plugins"
    if (-not (Test-Path $PluginsDir)) { continue }
    foreach ($Stale in @("kbea-plugin-rsync")) {
        $StaleDir = Join-Path $PluginsDir $Stale
        if (Test-Path $StaleDir) {
            Remove-Item $StaleDir -Recurse -Force
            Write-Host "  => 清理残留插件目录: $Stale" -ForegroundColor Yellow
        }
    }
}

# ---- 清理旧的 framework-dependent 遗留 ----
Write-Host "`nDone." -ForegroundColor Cyan
Write-Host "Portable: $PortDir ($([math]::Round((Get-Item "$PortDir\KBEA.exe").Length/1MB, 1)) MB single-file)"
Write-Host "Light   : $LightDir ($([math]::Round((Get-ChildItem $LightDir -Recurse -File | Measure-Object Length -Sum).Sum/1MB, 1)) MB multi-file)"
Write-Host ""
Write-Host "Next: run sign.bat to code-sign both artifacts."

