# ============================================================
#  KBEA batch code signer (CN=7zThinking)
#  Recursively signs ALL .exe / .dll under artifacts\.
#  Signature time-stamped via DigiCert RFC3161.
#  Run any time the artifacts are rebuilt.
# ============================================================
$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

$SIGNTOOL = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
$THUMB    = '1583576BBF01ADB342EDC1CB9BCA018E7FC71381'
$ARTIFACTS = Join-Path $PSScriptRoot 'artifacts'

# Multiple RFC3161 timestamp servers — first reachable wins.
$TS_SERVERS = @(
    'http://timestamp.digicert.com'
    'http://timestamp.sectigo.com'
    'http://timestamp.globalsign.com/tsa/rfc3161'
    'http://tsa.starfieldtech.com'
)

if (-not (Test-Path $SIGNTOOL)) {
    Write-Host "[ERROR] signtool not found: $SIGNTOOL" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $ARTIFACTS)) {
    Write-Host "[ERROR] artifacts folder not found: $ARTIFACTS" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== KBEA Batch Signer ===" -ForegroundColor Cyan
Write-Host "Root     : $ARTIFACTS"
Write-Host "Signer   : CN=7zThinking  (thumbprint $THUMB)"
Write-Host "TS hosts : $($TS_SERVERS -join ', ')"
Write-Host ""

function Sign-One {
    param([string]$Path)
    foreach ($ts in $TS_SERVERS) {
        & $SIGNTOOL sign /fd SHA256 /tr $ts /td SHA256 /sha1 $THUMB $Path 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return $true }
    }
    return $false
}

$files = Get-ChildItem $ARTIFACTS -Recurse -Include *.exe,*.dll
$count = $files.Count
$ok    = 0
$fail  = 0
$failedFiles = @()

for ($i = 0; $i -lt $count; $i++) {
    $f = $files[$i]
    $pct = [math]::Round(($i + 1) / $count * 100, 0)
    Write-Host ("  [{0,3}%] Signing {1}" -f $pct, $f.FullName.Replace("$ARTIFACTS\", '')) -NoNewline

    if (Sign-One $f.FullName) {
        Write-Host " [OK]" -ForegroundColor Green
        $ok++
    } else {
        Write-Host " [FAIL]" -ForegroundColor Red
        $failedFiles += $f.FullName
        $fail++
    }
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
Write-Host "Files  : $count   Signed OK : $ok   Failed : $fail"

# Verify round
Write-Host ""
Write-Host "Verifying all signatures..."
$vOK = 0
$vBad = 0
foreach ($f in $files) {
    & $SIGNTOOL verify /pa $f.FullName 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { $vOK++ } else {
        Write-Host ("  [BAD] " + $f.FullName.Replace("$ARTIFACTS\", '')) -ForegroundColor Yellow
        $vBad++
    }
}
Write-Host "Verify valid : $vOK   invalid : $vBad" -ForegroundColor $(if ($vBad -eq 0) {'Green'} else {'Yellow'})

if ($fail -gt 0) {
    Write-Host ""
    Write-Host "[WARNING] These files could not be signed (may be locked by running process):" -ForegroundColor Yellow
    $failedFiles | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}
if ($vBad -gt 0) { Write-Host "[WARNING] $vBad file(s) failed verification!" -ForegroundColor Red; exit 2 }
