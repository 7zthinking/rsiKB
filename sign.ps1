# ============================================================
#  KBEA batch code signer (CN=7zThinking) — INCREMENTAL
#  Three tiers to avoid re-signing unchanged files:
#    1. Already has a valid signature  -> skip
#    2. Content matches signed cache   -> copy cached signed file back (no signtool)
#    3. New/changed content            -> signtool sign + store into cache
#  Cache key = SHA-256 of file CONTENT, stored under artifacts\.sign-cache\.
#  Timestamped via DigiCert RFC3161 (fallback servers below).
#  Run any time the artifacts are rebuilt.
# ============================================================
$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

$SIGNTOOL = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
$THUMB    = '1583576BBF01ADB342EDC1CB9BCA018E7FC71381'
$ARTIFACTS = Join-Path $PSScriptRoot 'artifacts'
$CACHE_DIR = Join-Path $ARTIFACTS '.sign-cache'

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
if (-not (Test-Path $CACHE_DIR)) { New-Item $CACHE_DIR -ItemType Directory -Force | Out-Null }

Write-Host ""
Write-Host "=== KBEA Incremental Signer ===" -ForegroundColor Cyan
Write-Host "Root     : $ARTIFACTS"
Write-Host "Cache    : $CACHE_DIR"
Write-Host "Signer   : CN=7zThinking  (thumbprint $THUMB)"
Write-Host "TS hosts : $($TS_SERVERS -join ', ')"
Write-Host ""

function Get-ContentHash {
    param([string]$Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $bytes = $sha.ComputeHash([System.IO.File]::ReadAllBytes($Path))
        return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
    } catch { return $null }
}

function Test-SignatureValid {
    param([string]$Path)
    try {
        return (Get-AuthenticodeSignature -FilePath $Path).Status -eq 'Valid'
    } catch { return $false }
}

function Sign-One {
    param([string]$Path)
    foreach ($ts in $TS_SERVERS) {
        & $SIGNTOOL sign /fd SHA256 /tr $ts /td SHA256 /sha1 $THUMB $Path 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return $true }
    }
    return $false
}

$files = Get-ChildItem $ARTIFACTS -Recurse -Include *.exe,*.dll |
    Where-Object { $_.DirectoryName -ne $CACHE_DIR }
$count = $files.Count
$skipSigned = 0
$cacheHit   = 0
$ok         = 0
$fail       = 0
$failedFiles = @()

$sw = [System.Diagnostics.Stopwatch]::StartNew()

for ($i = 0; $i -lt $count; $i++) {
    $f = $files[$i]
    $rel = $f.FullName.Replace("$ARTIFACTS\", '')

    # Tier 1: already signed (e.g. sign ran before without a rebuild)
    if (Test-SignatureValid $f.FullName) {
        $skipSigned++
        continue
    }

    $hash = Get-ContentHash $f.FullName
    $cachedPath = if ($hash) { Join-Path $CACHE_DIR ($hash + $f.Extension) } else { $null }

    # Tier 2: content identical to a previously signed file -> copy signed copy back
    if ($cachedPath -and (Test-Path $cachedPath) -and (Test-SignatureValid $cachedPath)) {
        try {
            Copy-Item $cachedPath $f.FullName -Force
            $cacheHit++
            continue
        } catch {
            # copy failed (locked?) -> fall through to real signing
        }
    }

    # Tier 3: actually sign with signtool, then cache the signed result
    if (Sign-One $f.FullName) {
        if ($cachedPath) {
            try { Copy-Item $f.FullName $cachedPath -Force } catch { }
        }
        $ok++
    } else {
        $failedFiles += $f.FullName
        $fail++
    }
}

$sw.Stop()

Write-Host ""
Write-Host "=== Done ($([math]::Round($sw.Elapsed.TotalSeconds, 1)) s) ===" -ForegroundColor Cyan
Write-Host "Files        : $count"
Write-Host "Skipped(signed): $skipSigned"
Write-Host "Cache hit    : $cacheHit"
Write-Host "Newly signed : $ok"
Write-Host "Failed       : $fail"

# Verify round — Get-AuthenticodeSignature is in-process (fast) and
# checks the same trust chain as signtool verify.
Write-Host ""
Write-Host "Verifying all signatures..."
$vOK = 0
$vBad = 0
foreach ($f in $files) {
    if (Test-SignatureValid $f.FullName) { $vOK++ } else {
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
exit 0
