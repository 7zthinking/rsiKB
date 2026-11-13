$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dotnet = Join-Path $Root ".dotnet/dotnet.exe"
if (-not (Test-Path $Dotnet)) { $Dotnet = "dotnet" }

$Proj   = Join-Path $Root "src/KBEA.App/KBEA.App.csproj"
$WorkerProj = Join-Path $Root "src/KBEA.Worker/KBEA.Worker.csproj"
$PortDir = Join-Path $Root "artifacts/KBEA-Portable"
$LightDir = Join-Path $Root "artifacts/KBEA-Light"

# ---- Portable: 单文件 self-contained（给最终用户）----
Write-Host "`n[1/3] Publishing Portable (single-file self-contained)..." -ForegroundColor Cyan
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

# ---- Light: 多文件 self-contained（方便逐个签名 + 开发调试）----
Write-Host "`n[2/3] Publishing Light (multi-file self-contained)..." -ForegroundColor Cyan
& $Dotnet publish $Proj `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None -p:DebugSymbols=false --output $LightDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "  => $LightDir ($((Get-ChildItem $LightDir -Recurse -File).Count) files)" -ForegroundColor Green

# ---- Worker: 后台元数据服务（独立进程，单文件 self-contained）----
Write-Host "`n[3/3] Publishing Worker (single-file self-contained)..." -ForegroundColor Cyan
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

# ---- 清理旧的 framework-dependent 遗留 ----
Write-Host "`nDone." -ForegroundColor Cyan
Write-Host "Portable: $PortDir ($([math]::Round((Get-Item "$PortDir\KBEA.exe").Length/1MB, 1)) MB single-file)"
Write-Host "Light   : $LightDir ($([math]::Round((Get-ChildItem $LightDir -Recurse -File | Measure-Object Length -Sum).Sum/1MB, 1)) MB multi-file)"
Write-Host ""
Write-Host "Next: run sign.bat to code-sign both artifacts."
