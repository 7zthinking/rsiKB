$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dotnet = Join-Path $Root ".dotnet/dotnet.exe"
if (-not (Test-Path $Dotnet)) { $Dotnet = "dotnet" }
$Output = Join-Path $Root "artifacts/KBEA-Portable"
& $Dotnet publish (Join-Path $Root "src/KBEA.App/KBEA.App.csproj") `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false --output $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($File in @("LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.md")) {
    Copy-Item (Join-Path $Root $File) (Join-Path $Output $File) -Force
}
Copy-Item (Join-Path $Root "packaging/Portable-Readme.txt") (Join-Path $Output "使用说明.txt") -Force
Write-Host "Portable build ready: $Output/KBEA.exe"
