#requires -Version 5.1
<#
.SYNOPSIS
    Mirror the Cloudflare R2 telemetry corpus to a local folder, gunzipping
    objects as they arrive so analysis tools can grep / read directly.

.DESCRIPTION
    The Mahjong plugin uploads gzipped NDJSON (and raw bytes for memdumps)
    to the `mahjong-telemetry` R2 bucket. Bucket keys follow the immutable
    layout `{stream}/{install_id}/{YYYY-MM-DD}/{filename}.gz` — once a file
    is shipped its key never changes, which is what makes "skip if the
    local file exists" a correct incremental policy.

    For every R2 key that isn't already on disk, this script downloads it
    and decompresses it (keeping the .gz too in case re-decoding is ever
    needed). The state of "what's been synced" is just whatever's in the
    output directory — no separate manifest file.

.PARAMETER Bucket
    R2 bucket name. Defaults to `mahjong-telemetry` (matches wrangler.toml).

.PARAMETER OutDir
    Local mirror root. Created if missing. Defaults to `./corpus`.

.PARAMETER Stream
    Optional stream filter — one of: games, errors, findings, memdumps,
    discards, inputs, sigprobes. Omitting syncs every stream.

.PARAMETER InstallId
    Optional install-id GUID filter. Lets you pull a single user's data.

.PARAMETER Date
    Optional YYYY-MM-DD filter. Only objects under that date prefix sync.

.PARAMETER Force
    Re-download every key even if a local copy exists. Useful when an
    earlier sync was interrupted mid-decompress.

.EXAMPLE
    # Pull everything new
    .\tools\sync-corpus.ps1

.EXAMPLE
    # Just findings from today
    .\tools\sync-corpus.ps1 -Stream findings -Date 2026-05-07

.EXAMPLE
    # One install's full memdump history
    .\tools\sync-corpus.ps1 -Stream memdumps -InstallId 8e4c0a12-...

.NOTES
    Requires `wrangler` on PATH and an authenticated Cloudflare account.
    Run `wrangler login` once if you haven't already.
#>
[CmdletBinding()]
param(
    [string]$Bucket = "mahjong-telemetry",
    [string]$OutDir = (Join-Path $PSScriptRoot ".." "corpus"),
    [ValidateSet("games", "errors", "findings", "memdumps", "discards", "inputs", "sigprobes")]
    [string]$Stream,
    [string]$InstallId,
    [string]$Date,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# ---- Sanity checks ----
$wrangler = Get-Command wrangler -ErrorAction SilentlyContinue
if (-not $wrangler) {
    Write-Error "wrangler not found on PATH. Install with: npm install -g wrangler"
    exit 1
}

# ---- Build the prefix filter ----
# Order matters: stream / install / date are nested in that order in R2.
# We can only filter at one prefix level — anything finer happens client-side.
$prefix = ""
if ($Stream)    { $prefix = "$Stream/" }
if ($Stream -and $InstallId) { $prefix = "$Stream/$InstallId/" }
if ($Stream -and $InstallId -and $Date) { $prefix = "$Stream/$InstallId/$Date/" }

$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Write-Host "Mirror root: $OutDir"
if ($prefix) { Write-Host "Prefix filter: $prefix" }

# ---- Enumerate keys ----
# wrangler r2 object list paginates by 1000. We follow `truncated`/`cursor`
# until exhausted, accumulating every key into a single list.
$allKeys = @()
$cursor = $null
do {
    $args = @("r2", "object", "list", $Bucket, "--output-format=json")
    if ($prefix) { $args += @("--prefix", $prefix) }
    if ($cursor) { $args += @("--cursor", $cursor) }

    $raw = & wrangler @args 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "wrangler r2 object list failed (exit=$LASTEXITCODE)"
        exit 1
    }

    $page = $raw | ConvertFrom-Json
    if ($page.objects) {
        foreach ($obj in $page.objects) { $allKeys += $obj.key }
    }
    $cursor = $page.cursor
    $truncated = $page.truncated -eq $true
} while ($truncated -and $cursor)

Write-Host ("Found {0} objects matching filter." -f $allKeys.Count)

# ---- Client-side post-filter for InstallId / Date when a Stream wasn't
# given (we can only build one prefix level). Cheap string-contains check. ----
if ($InstallId -and -not $Stream) {
    $allKeys = $allKeys | Where-Object { $_ -like "*/$InstallId/*" }
}
if ($Date -and (-not $Stream -or -not $InstallId)) {
    $allKeys = $allKeys | Where-Object { $_ -like "*/$Date/*" }
}

# ---- Download + decompress ----
$downloaded = 0
$skipped = 0
$failed = 0

foreach ($key in $allKeys) {
    $localGz = Join-Path $OutDir $key
    $localPlain = $localGz -replace '\.gz$', ''
    $alreadyHave = (Test-Path $localPlain) -and -not $Force

    if ($alreadyHave) {
        $skipped++
        continue
    }

    # R2 keys can contain slashes; CreateDirectory on the parent path
    # handles arbitrary nesting in one call.
    $parent = Split-Path $localGz -Parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null

    try {
        & wrangler r2 object get $Bucket $key --file $localGz 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "  fetch failed: $key (exit=$LASTEXITCODE)"
            $failed++
            continue
        }

        # Inline gunzip to the sibling path. We keep the .gz too — cheap,
        # and lets the analysis side re-decode if a parser disagrees with
        # how PowerShell handled the stream.
        $gz = [System.IO.File]::OpenRead($localGz)
        try {
            $gunzip = New-Object System.IO.Compression.GZipStream(
                $gz, [System.IO.Compression.CompressionMode]::Decompress)
            $out = [System.IO.File]::Create($localPlain)
            try { $gunzip.CopyTo($out) }
            finally { $out.Dispose(); $gunzip.Dispose() }
        }
        finally { $gz.Dispose() }

        $downloaded++
        if (($downloaded % 20) -eq 0) {
            Write-Host ("  ...synced {0}" -f $downloaded)
        }
    }
    catch {
        Write-Warning "  decode failed: $key — $($_.Exception.Message)"
        $failed++
    }
}

Write-Host ""
Write-Host ("Done. downloaded={0} skipped={1} failed={2}" -f $downloaded, $skipped, $failed)
if ($failed -gt 0) { exit 2 }
