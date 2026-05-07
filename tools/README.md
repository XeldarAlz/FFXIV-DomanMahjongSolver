# tools/

Reverse-engineering scripts used to map FFXIV's Mahjong addon memory layout.
Not part of the runtime path — these run offline against captured hex dumps
to find offsets, AtkValue indices, and node IDs.

## Scripts

| Script | Purpose | Input |
|---|---|---|
| `analyze_snaps.py` | Walk a captured snapshot and surface candidate offsets | `emj-snapshot-*.txt` written by `/mjauto findtiles` |
| `scan_tiles.py` | Find tile-id encoding in a snapshot — pins the texture base | `emj-snapshot-*.txt` |
| `diff_nodes.py` | Diff two `/mjauto walknodes` captures to spot visibility / id changes between states | Two `emj-walknodes-*.txt` files |
| `gen_icon.ps1` | Generate the plugin's `images/icon.png` from a source SVG | `images/icon.svg` |
| `sync-corpus.ps1` | Mirror the R2 telemetry corpus to local `corpus/`, gunzipping as it arrives | `wrangler` + Cloudflare credentials |

## Workflow for a new variant

1. Sit at a Mahjong table on the unknown client.
2. `/mjauto findtiles` — captures hand tiles + memory window to
   `emj-findtiles-*.txt`.
3. `/mjauto walknodes` — captures the addon's node tree to
   `emj-walknodes-*.txt`.
4. `python tools/scan_tiles.py emj-findtiles-*.txt` — pins the variant's
   tile texture base.
5. `python tools/diff_nodes.py <state1> <state2>` — spots node IDs that
   differ between game states (call prompts, etc.).
6. Produce a new `data/layouts/<variant>.json` with the discovered values.
7. Plugin auto-discovers it on next launch.

See [`data/layouts/README.md`](../data/layouts/README.md) for the JSON
schema.

## Pulling user telemetry

Once the Cloudflare Worker is deployed (see `server/README.md`):

```powershell
# Pull everything new
.\tools\sync-corpus.ps1

# Just findings from a specific date — fast iteration loop
.\tools\sync-corpus.ps1 -Stream findings -Date 2026-05-07

# A single install's full memdump history
.\tools\sync-corpus.ps1 -Stream memdumps -InstallId 8e4c0a12-...
```

Output lands in `./corpus/{stream}/{install_id}/{date}/` — both the `.gz`
and the gunzipped NDJSON sit side by side. The script is incremental: a
local file existing means "already synced", so re-runs only fetch what's
new. Pass `-Force` to redownload everything if a sync was interrupted
mid-decompress.

After syncing, run Claude Code from the corpus directory for analysis:

```powershell
cd corpus
claude
```

Then ask things like *"group every `variant_miss` finding by addon_name +
game_version and tell me which client builds have no matching variant"*
— Claude has direct file access to the whole corpus.

## Status

Scripts are ad-hoc one-offs — no test coverage, no formal API, run on
demand. The `/mjauto` capture commands they consume live in the plugin's
`Commands/MjAutoCommand.cs`.
