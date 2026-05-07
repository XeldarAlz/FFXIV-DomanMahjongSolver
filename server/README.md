# Telemetry server

Cloudflare Worker that receives anonymous diagnostic uploads from the
Mahjong plugin (gameplay logs, error reports, memory dumps, etc.) and
stores them in R2 for offline reverse-engineering analysis.

## What gets sent

Every upload carries:

| Header | Purpose |
| --- | --- |
| `X-Install-Id` | Random GUID minted on first config migration. The only stable handle to a single install. **No PII.** |
| `X-Plugin-Version` | Semver of the plugin build that produced the file. |
| `X-Plugin-Hash` | SHA-256 prefix of the plugin DLL. Lets us tell which build a log came from. |
| `X-Game-Version` | FFXIV client build version string. |
| `X-Client-Region` | Dalamud `ClientLanguage` (English / Japanese / French / German). |
| `X-Os-Platform` | Operating system identifier. |
| `X-Schema-Version` | Envelope schema version. |
| `X-Stream` | One of: `games`, `errors`, `findings`, `memdumps`, `discards`, `inputs`, `sigprobes`. |
| `X-Filename` | Original filename on the client. |
| Body | gzipped NDJSON (or, for `memdumps`, NDJSON with base64-encoded byte arrays). |

## Deploy

```bash
cd server
npm install -g wrangler

# 1. Create the R2 bucket
wrangler r2 bucket create mahjong-telemetry

# 2. Create the KV namespace for rate limiting
wrangler kv:namespace create RATE_LIMIT_KV
# Copy the returned id into wrangler.toml (kv_namespaces.id)

# 3. Deploy the Worker
wrangler deploy
# Note the printed *.workers.dev URL — that's the upload endpoint.

# 4. Update telemetry-endpoint.json with the URL and commit it. The plugin
#    fetches this file from raw.githubusercontent.com at startup, so a new
#    URL propagates to every install on next plugin load.
```

## Stream layout in R2

```
mahjong-telemetry/
├── games/{install_id}/{YYYY-MM-DD}/game-*.ndjson.gz
├── errors/{install_id}/{YYYY-MM-DD}/errors-*.ndjson.gz
├── findings/{install_id}/{YYYY-MM-DD}/findings-*.ndjson.gz
├── memdumps/{install_id}/{YYYY-MM-DD}/memdumps-*.ndjson.gz
├── discards/{install_id}/{YYYY-MM-DD}/emj-discards.log.gz
├── inputs/{install_id}/{YYYY-MM-DD}/emj-events.log.gz
└── sigprobes/{install_id}/{YYYY-MM-DD}/sigprobe-*.ndjson.gz
```

## Limits

- Per-request: **10 MB** (rejects with 413).
- Per-install daily: **200 MB** (rejects with 429). KV-backed rolling counter
  with a 25-hour TTL so day boundaries don't drop counts.
- Free Cloudflare plan is more than sufficient at any plausible plugin
  user-base size.

## Pulling data for analysis

```bash
# List today's memdumps for an install
wrangler r2 object list mahjong-telemetry --prefix "memdumps/$INSTALL_ID/$(date -I)/"

# Download an entire install's history
wrangler r2 object get mahjong-telemetry --prefix "memdumps/$INSTALL_ID/" \
  --pipe | tar -xz -C ./local/
```

## Disabling the pipeline

Edit `telemetry-endpoint.json` and set `enabled: false`. On next plugin
launch, every install picks up the change and stops uploading. Files
already on disk locally remain — they ship on the next launch where
`enabled` flips back to true.
