# Pulls all structured Mahjong telemetry worker events for today and writes
# a flattened NDJSON. Filters out the noisy cf-worker-event request wrappers
# so only `accepted` / reject / fail events remain.

param(
  [string] $Token = $env:CLOUDFLARE_API_TOKEN,
  [string] $Account = $env:CLOUDFLARE_ACCOUNT_ID,
  [string] $Out = "$PSScriptRoot\..\.local\worker-events-today.ndjson"
)

if (-not $Token) { throw "CLOUDFLARE_API_TOKEN not set" }
if (-not $Account) { throw "CLOUDFLARE_ACCOUNT_ID not set (or pass -Account <id>); find yours at https://dash.cloudflare.com under any zone/worker overview" }
$null = New-Item -ItemType Directory -Force -Path (Split-Path $Out)

$h = @{ Authorization = "Bearer $Token"; "Content-Type" = "application/json" }
$todayStart = (Get-Date).ToUniversalTime().Date # 00:00 UTC today
$from = [DateTimeOffset]::new($todayStart, [TimeSpan]::Zero).ToUnixTimeMilliseconds()
$to = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
Write-Host "Window: $($todayStart.ToString('o')) -> now ($([math]::Round(($to-$from)/3600000.0,2)) h)"

$bodyObj = @{
  queryId = "events"
  view = "events"
  timeframe = @{ from = $from; to = $to }
  limit = 1000
  parameters = @{
    datasets = @("cloudflare-workers")
    filters = @(
      @{ id = "f1"; key = "`$metadata.service"; type = "string"; value = "mahjong-telemetry"; operation = "eq" },
      @{ id = "f2"; key = "`$metadata.type"; type = "string"; value = "cf-worker"; operation = "eq" }
    )
  }
}
$body = $bodyObj | ConvertTo-Json -Depth 6 -Compress
$r = Invoke-RestMethod -Method POST -Headers $h -Body $body `
  -Uri "https://api.cloudflare.com/client/v4/accounts/$Account/workers/observability/telemetry/query"

$events = $r.result.events.events
Write-Host "Fetched $($events.Count) structured events"

$flat = $events | ForEach-Object {
  $src = $_.source
  $meta = $_."`$metadata"
  [pscustomobject]@{
    ts        = ([DateTimeOffset]::FromUnixTimeMilliseconds([long]$_.timestamp)).ToString("o")
    level     = $src.level
    event     = $src.event
    install   = $src.install_id
    stream    = $src.stream
    key       = $src.key
    bytes     = $src.bytes
    plugin    = $src.plugin_version
    game      = $src.game_version
    status    = $src.status
    error     = $src.error
    region    = $src.client_region
    rate_key  = $src.rate_key
    used      = $src.used_bytes
    declared  = $src.declared_bytes
    requestId = $meta.requestId
  }
}

$flat | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 4 } | Set-Content -Path $Out -Encoding utf8
Write-Host "Wrote $Out"

# Quick summary tables
Write-Host "`n=== Events by (level, event) ==="
$flat | Group-Object level, event | Sort-Object Count -Descending | Select-Object Count, Name | Format-Table -AutoSize

Write-Host "`n=== Accepted uploads by stream ==="
$flat | Where-Object event -eq "accepted" | Group-Object stream | Sort-Object Count -Descending |
  ForEach-Object {
    $sumB = ($_.Group | Measure-Object bytes -Sum).Sum
    [pscustomobject]@{ stream = $_.Name; uploads = $_.Count; total_bytes = $sumB; total_mb = [math]::Round($sumB/1MB,2) }
  } | Format-Table -AutoSize

Write-Host "`n=== Accepted uploads by install ==="
$flat | Where-Object event -eq "accepted" | Group-Object install | Sort-Object Count -Descending |
  ForEach-Object {
    $sumB = ($_.Group | Measure-Object bytes -Sum).Sum
    $streams = ($_.Group.stream | Sort-Object -Unique) -join ","
    $plugin = ($_.Group.plugin | Sort-Object -Unique) -join ","
    [pscustomobject]@{ install = $_.Name.Substring(0,8); uploads = $_.Count; mb = [math]::Round($sumB/1MB,2); streams = $streams; plugin_v = $plugin }
  } | Format-Table -AutoSize

Write-Host "`n=== Non-accepted events (warn/error) ==="
$flat | Where-Object { $_.level -ne "info" -or $_.event -ne "accepted" } |
  Select-Object ts, level, event, install, stream, error |
  Format-Table -AutoSize -Wrap
