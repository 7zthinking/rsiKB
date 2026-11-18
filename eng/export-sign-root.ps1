# ============================================================
#  Export KBEA code-signing root CA (public cert only) into the
#  portable artifacts folder, plus a one-click import .bat for
#  end users. Run after publish.ps1 whenever the signing cert
#  changes. NEVER exports private keys.
# ============================================================
$ErrorActionPreference = 'Stop'

$THUMB = '1583576BBF01ADB342EDC1CB9BCA018E7FC71381'   # CN=7zThinking leaf cert
$OUT_DIR = Join-Path $PSScriptRoot '..\artifacts\KBEA-Light\certs'

$leaf = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
    Where-Object Thumbprint -eq $THUMB | Select-Object -First 1
if (-not $leaf) { Write-Host "[ERROR] signing cert not found ($THUMB)" -ForegroundColor Red; exit 1 }

# Build the chain, take the last element = self-signed root CA
$chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
$null = $chain.Build($leaf)
$root = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate

New-Item $OUT_DIR -ItemType Directory -Force | Out-Null
$rootPath = Join-Path $OUT_DIR '7zThinking-Root-CA.cer'
$leafPath = Join-Path $OUT_DIR '7zThinking.cer'
[System.IO.File]::WriteAllBytes($rootPath, $root.Export('Cert'))
[System.IO.File]::WriteAllBytes($leafPath, $leaf.Export('Cert'))
# One-click import for end users (double-click friendly, ASCII content only)
$bat = @'
@echo off
rem Trust the KBEA code-signing publisher (CN=7zThinking).
rem Only imports PUBLIC certificates - no private key is included.
echo This will import "7zThinking Root CA" so Windows trusts KBEA signatures.
echo.
echo   [1] Current user only  (recommended, no admin needed)
echo   [2] Whole machine      (requires admin elevation)
echo   [Q] Quit
set /p CHOICE=Choose [1/2/Q]:
if /i "%CHOICE%"=="1" certutil -f -user -addstore Root "%~dp07zThinking-Root-CA.cer" & goto :done
if /i "%CHOICE%"=="2" certutil -f -addstore Root "%~dp07zThinking-Root-CA.cer" & goto :done
goto :eof
:done
echo.
echo Done. Signatures by CN=7zThinking are now trusted for this user/machine.
pause
'@
$batPath = Join-Path $OUT_DIR 'import-signing-cert.bat'
[System.IO.File]::WriteAllText($batPath, $bat, [System.Text.Encoding]::ASCII)

Write-Host ("Root CA : " + $root.Subject + " -> " + $rootPath) -ForegroundColor Green
Write-Host ("Leaf    : " + $leaf.Subject + " -> " + $leafPath) -ForegroundColor Green
Write-Host ("Import  : " + $batPath) -ForegroundColor Green

# Mirror into the portable release folder too (user-facing distribution dir)
$portableDir = Join-Path $PSScriptRoot '..\artifacts\KBEA-Portable'
if (Test-Path $portableDir) {
    Copy-Item $OUT_DIR (Join-Path $portableDir 'certs') -Recurse -Force
    Write-Host ("Mirrored -> " + (Join-Path $portableDir 'certs')) -ForegroundColor Green
}
