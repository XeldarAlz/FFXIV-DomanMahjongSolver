# Pulls structured worker events for an arbitrary lookback window.
# Cloudflare Workers Observability typically retains ~3 days on free, ~7 paid.
# Usage:
#   . .\.local\secrets.ps1
#   pwsh tools\fetch-worker-logs-window.ps1 -Days 7 -Out .local\worker-events-7d.ndjson

param(
  [int] $Days = 7,
  [string] $Token = $env:CLOUDFLARE_API_TOKEN,
  [string] $Account = $env:CLOUDFLARE_ACCOUNT_ID,
  [string] $Out = "$PSScriptRoot\..\.local\worker-events-7d.ndjson"
)

if (-not $Token) { throw "CLOUDFLARE_API_TOKEN not set" }
if (-not $Account) { throw "CLOUDFLARE_ACCOUNT_ID not set" }
$null = New-Item -ItemType Directory -Force -Path (Split-Path $Out)

$h = @{ Authorization = "Bearer $Token"; "Content-Type" = "application/json" }
$now = [DateTimeOffset]::UtcNow
$fromDt = $now.AddDays(-1 * $Days)
$from = $fromDt.ToUnixTimeMilliseconds()
$to = $now.ToUnixTimeMilliseconds()
Write-Host "Window: $($fromDt.ToString('o')) -> $($now.ToString('o'))  ($Days days)"

# Pagination: API limits per call, so iterate by walking $to backwards.
$all = New-Object System.Collections.Generic.List[object]
$cursorTo = $to
$page = 0
while ($true) {
  $page++
  $bodyObj = @{
    queryId = "events"
    view = "events"
    timeframe = @{ from = $from; to = $cursorTo }
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
  if (-not $events -or $events.Count -eq 0) { break }
  Write-Host ("  page {0}: {1} events" -f $page, $events.Count)
  foreach ($e in $events) { $all.Add($e) }
  if ($events.Count -lt 1000) { break }
  # Walk window back to the oldest event seen, minus 1 ms, to fetch next page.
  $oldest = ($events | Measure-Object -Property timestamp -Minimum).Minimum
  if ($oldest -le $from) { break }
  $cursorTo = [long]$oldest - 1
}
Write-Host "Total events: $($all.Count)"

$flat = $all | ForEach-Object {
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
Write-Host "Wrote $Out ($($flat.Count) lines)"
