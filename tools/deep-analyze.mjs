// Deep analysis pass over .local/b2-all/ telemetry.
// Goal: produce findings useful for plugin RE + stability work.
//
// 1. Memdumps: cross-install byte-stability map of addon_b64 / root_b64,
//    duplicate-hash detection, atk_count distributions, seat_pools coverage.
// 2. Games: full event-type histogram across files, per-file line counts
//    (pre/post dedup), redundant-state detection, hand outcomes.
// 3. Discards: full content survey across all 10 records.
// 4. Sigprobes: per-install elapsed_ms distribution + ASLR-slide table.
// 5. Inputs: addon × value cluster across installs.

import { readdir, readFile, writeFile } from "node:fs/promises";
import { join, relative } from "node:path";
import { gunzipSync } from "node:zlib";

const ROOT = process.argv[2] ?? ".local/b2-all";
const OUT_MD = process.argv[3] ?? ".local/deep-analysis.md";

async function walk(dir) {
  const out = [];
  for (const e of await readdir(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) out.push(...await walk(p));
    else out.push(p);
  }
  return out;
}

const files = (await walk(ROOT)).map((p) => {
  const r = relative(ROOT, p).split(/[\\/]/);
  return { path: p, key: r.join("/"), stream: r[0], install: r[1], date: r[2], filename: r.slice(3).join("/") };
});

const inc = (m, k, n = 1) => m.set(k, (m.get(k) ?? 0) + n);

async function readNdjson(path) {
  const raw = await readFile(path);
  const body = (raw[0] === 0x1f && raw[1] === 0x8b) ? gunzipSync(raw) : raw;
  return body.toString("utf8").split("\n").filter(Boolean).map((l) => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);
}

const md = [];
md.push(`# Mahjong Plugin Telemetry — Deep Analysis`);
md.push(`Generated ${new Date().toISOString()}`);
md.push("");

// =====================================================================
// 1. MEMDUMPS — cross-install structural analysis
// =====================================================================
md.push(`## 1. memdumps`);

const memdumpFiles = files.filter((f) => f.stream === "memdumps");
let mdRecords = 0;
let mdHashes = new Map();        // hash -> {count, installs:Set, seatPoolsLen:Set, atkCount:Set}
const reasonCounts = new Map();
const atkCountDist = new Map();
const seatPoolsLenDist = new Map();
const addonAddrsByInstall = new Map();   // install -> Set(hex)
const rootAddrsByInstall = new Map();
const atkAddrsByInstall = new Map();

// For byte-stability analysis: count how often each byte position varies across distinct installs.
// We hold one (or two) representative addon blobs per install; per-byte we compute
// how many distinct values appear across installs.
const ADDON_LEN = 0x1300;       // 4864
const ROOT_LEN = 0x400;         // 1024
const addonBytesByInstall = new Map();   // install -> Buffer (first dump's addon_b64 decoded)
const rootBytesByInstall = new Map();

// Per-record per-install per-seat pool population (later: which pools coincide with which seat snapshots).
// Per-install map: seat number observed → atk_addr distribution.
const perInstall = new Map(); // install -> {records, files, distinctHashes:Set, addonAddr:Set, rootAddr:Set, reasons:Map, atkCount:Map}
function getInst(i) {
  if (!perInstall.has(i)) perInstall.set(i, { records: 0, files: 0, distinctHashes: new Set(), addonAddrs: new Set(), rootAddrs: new Set(), atkAddrs: new Set(), reasons: new Map(), atkCounts: new Map(), seatPoolsLens: new Map() });
  return perInstall.get(i);
}

for (const f of memdumpFiles) {
  const recs = await readNdjson(f.path);
  const inst = getInst(f.install);
  inst.files++;
  for (const r of recs) {
    mdRecords++;
    inst.records++;
    if (typeof r.reason === "string") { inc(reasonCounts, r.reason); inc(inst.reasons, r.reason); }
    if (typeof r.hash === "string") {
      inst.distinctHashes.add(r.hash);
      const e = mdHashes.get(r.hash) ?? { count: 0, installs: new Set(), atks: new Set(), pools: new Set() };
      e.count++;
      e.installs.add(f.install);
      if (typeof r.atk_count === "number") e.atks.add(r.atk_count);
      if (Array.isArray(r.seat_pools)) e.pools.add(r.seat_pools.length);
      mdHashes.set(r.hash, e);
    }
    if (typeof r.addon_addr === "number") inst.addonAddrs.add(r.addon_addr);
    if (typeof r.root_addr === "number") inst.rootAddrs.add(r.root_addr);
    if (typeof r.atk_addr === "number") inst.atkAddrs.add(r.atk_addr);
    if (typeof r.atk_count === "number") { inc(atkCountDist, r.atk_count); inc(inst.atkCounts, r.atk_count); }
    const spLen = Array.isArray(r.seat_pools) ? r.seat_pools.length : -1;
    inc(seatPoolsLenDist, spLen);
    inc(inst.seatPoolsLens, spLen);

    // Capture first addon/root blob per install (state-change reason for consistency).
    if (!addonBytesByInstall.has(f.install) && typeof r.addon_b64 === "string" && r.reason === "state-change") {
      const b = Buffer.from(r.addon_b64, "base64");
      if (b.byteLength >= ADDON_LEN) addonBytesByInstall.set(f.install, b.subarray(0, ADDON_LEN));
    }
    if (!rootBytesByInstall.has(f.install) && typeof r.root_b64 === "string" && r.reason === "state-change") {
      const b = Buffer.from(r.root_b64, "base64");
      if (b.byteLength >= ROOT_LEN) rootBytesByInstall.set(f.install, b.subarray(0, ROOT_LEN));
    }
  }
}

md.push(`- files: ${memdumpFiles.length}, records: ${mdRecords}, distinct hashes: ${mdHashes.size}`);
md.push(`- reason histogram:`);
for (const [r, c] of [...reasonCounts.entries()].sort((a, b) => b[1] - a[1])) md.push(`  - \`${r}\` × ${c}`);
md.push(`- atk_count distribution: ${[...atkCountDist.entries()].sort((a, b) => a[0] - b[0]).map(([k, v]) => `${k}:${v}`).join(" ")}`);
md.push(`- seat_pools.length distribution: ${[...seatPoolsLenDist.entries()].sort((a, b) => a[0] - b[0]).map(([k, v]) => `${k}:${v}`).join(" ")} (-1 = not array)`);
md.push("");

md.push(`### per-install memdump posture`);
md.push("```");
md.push("install   recs  hashes  addon_addrs  root_addrs  atk_addrs  atk_counts  pools_lens  reasons");
for (const [i, e] of [...perInstall.entries()].sort((a, b) => b[1].records - a[1].records)) {
  const rs = [...e.reasons.entries()].map(([k, v]) => `${k}:${v}`).join(",");
  const ac = [...e.atkCounts.entries()].sort((a, b) => a[0] - b[0]).map(([k, v]) => `${k}:${v}`).join(",");
  const pl = [...e.seatPoolsLens.entries()].sort((a, b) => a[0] - b[0]).map(([k, v]) => `${k}:${v}`).join(",");
  md.push(`${i.slice(0, 8)} ${String(e.records).padStart(5)} ${String(e.distinctHashes.size).padStart(7)} ${String(e.addonAddrs.size).padStart(12)} ${String(e.rootAddrs.size).padStart(11)} ${String(e.atkAddrs.size).padStart(10)} ${ac.padEnd(20)} ${pl.padEnd(15)} ${rs}`);
}
md.push("```");
md.push("");

// Cross-install duplicate hashes — a hash seen in multiple installs is a stable observation.
const crossInstallHashes = [...mdHashes.entries()].filter(([_, e]) => e.installs.size >= 2).sort((a, b) => b[1].count - a[1].count);
md.push(`### Hashes shared across multiple installs`);
md.push(`${crossInstallHashes.length} hash(es) appear under ≥2 install_ids — these represent identical addon state captured by different users:`);
md.push("```");
for (const [h, e] of crossInstallHashes.slice(0, 20)) md.push(`${h}  count=${e.count}  installs=${e.installs.size}  atk_counts={${[...e.atks].join(",")}}  pools={${[...e.pools].join(",")}}`);
md.push("```");
md.push("");

// Per-install single-blob snapshot byte-stability.
// For each byte offset 0..ADDON_LEN-1, count distinct values across the per-install representative blobs.
const installs = [...addonBytesByInstall.keys()];
md.push(`### addon_b64 byte stability across ${installs.length} installs`);
if (installs.length >= 2) {
  const blobs = installs.map((i) => addonBytesByInstall.get(i));
  const stableRanges = [];
  let curStart = -1;
  for (let off = 0; off < ADDON_LEN; off++) {
    let stable = true;
    const v0 = blobs[0][off];
    for (let i = 1; i < blobs.length; i++) {
      if (blobs[i][off] !== v0) { stable = false; break; }
    }
    if (stable) {
      if (curStart < 0) curStart = off;
    } else {
      if (curStart >= 0) { stableRanges.push([curStart, off - 1]); curStart = -1; }
    }
  }
  if (curStart >= 0) stableRanges.push([curStart, ADDON_LEN - 1]);
  // Significant ranges = ≥4 contiguous bytes
  const sig = stableRanges.filter(([a, b]) => b - a + 1 >= 4);
  const sigBytes = sig.reduce((s, [a, b]) => s + (b - a + 1), 0);
  md.push(`- stable byte count: ${blobs.length === 0 ? 0 : ADDON_LEN - blobs.reduce((acc, b, idx) => acc, 0)} (computed below)`);
  let stableTotal = 0;
  for (let off = 0; off < ADDON_LEN; off++) {
    const v0 = blobs[0][off];
    if (blobs.every((b) => b[off] === v0)) stableTotal++;
  }
  md.push(`- stable bytes: ${stableTotal} / ${ADDON_LEN} (${((stableTotal / ADDON_LEN) * 100).toFixed(1)}%)`);
  md.push(`- contiguous stable runs ≥4 bytes: ${sig.length}, covering ${sigBytes} bytes`);
  md.push(`- top 30 longest stable runs (offset hex .. end hex (length)):`);
  md.push("```");
  const top = [...sig].sort((a, b) => (b[1] - b[0]) - (a[1] - a[0])).slice(0, 30);
  for (const [a, b] of top) md.push(`  0x${a.toString(16).padStart(4, "0")} .. 0x${b.toString(16).padStart(4, "0")}  len=${b - a + 1}`);
  md.push("```");

  // Highlight known seat-block offsets and report stability there.
  const seatBlocks = [
    ["self     0x04FE", 0x04FE, 0x07DD],
    ["shimo    0x07DE", 0x07DE, 0x0ABD],
    ["toimen   0x0ABE", 0x0ABE, 0x0D9D],
    ["kamicha  0x0D9E", 0x0D9E, 0x107D],
  ];
  md.push(`- seat-block stability (% of bytes identical across installs in each block):`);
  md.push("```");
  for (const [name, a, b] of seatBlocks) {
    let s = 0;
    for (let off = a; off <= b && off < ADDON_LEN; off++) {
      const v0 = blobs[0][off];
      if (blobs.every((blob) => blob[off] === v0)) s++;
    }
    const len = Math.min(b, ADDON_LEN - 1) - a + 1;
    md.push(`  ${name}  ${s}/${len} stable (${((s / len) * 100).toFixed(1)}%)`);
  }
  md.push("```");
} else {
  md.push(`- skipped (need ≥2 installs)`);
}
md.push("");

// =====================================================================
// 2. GAMES — event survey
// =====================================================================
md.push(`## 2. games`);
const gameFiles = files.filter((f) => f.stream === "games");
const eventCounts = new Map();
const stateLineCountByFile = [];
const handStartByInstall = new Map();
const legalStateValues = new Map();
const wallMinByFile = [];
const winsByInstall = new Map();
let totalGameLines = 0;
for (const f of gameFiles) {
  const recs = await readNdjson(f.path);
  totalGameLines += recs.length;
  let states = 0;
  let minWall = Infinity;
  for (const r of recs) {
    inc(eventCounts, r.e ?? "(no e)");
    if (r.e === "state") {
      states++;
      if (typeof r.legal === "string") inc(legalStateValues, r.legal);
      if (typeof r.wall === "number" && r.wall < minWall) minWall = r.wall;
    }
    if (r.e === "hand-start") inc(handStartByInstall, f.install);
  }
  stateLineCountByFile.push({ file: f.filename, install: f.install, date: f.date, total: recs.length, states });
  if (minWall !== Infinity) wallMinByFile.push(minWall);
}
md.push(`- files: ${gameFiles.length}, total lines: ${totalGameLines}`);
md.push(`- event histogram:`);
for (const [e, c] of [...eventCounts.entries()].sort((a, b) => b[1] - a[1])) md.push(`  - \`${e}\` × ${c}`);
md.push(`- legal-move-class distribution (state.legal):`);
for (const [v, c] of [...legalStateValues.entries()].sort((a, b) => b[1] - a[1]).slice(0, 15)) md.push(`  - \`${v}\` × ${c}`);
md.push(`- hand-start counts by install: ${[...handStartByInstall.entries()].map(([i, n]) => `${i.slice(0, 8)}=${n}`).join(", ")}`);
md.push("");
md.push(`### Game files: lines per file by date (dedup health)`);
md.push("```");
md.push("date         install   total_lines  state_lines  file");
for (const r of stateLineCountByFile.sort((a, b) => (a.date + a.install).localeCompare(b.date + b.install) || (b.total - a.total)).slice(0, 30)) {
  md.push(`${r.date}  ${r.install.slice(0, 8)} ${String(r.total).padStart(11)}  ${String(r.states).padStart(11)}  ${r.file}`);
}
md.push("```");
const bigFiles = stateLineCountByFile.filter((r) => r.total > 500).sort((a, b) => b.total - a.total);
md.push(`### Files >500 lines (suspect: dedup not engaged or pre-fix)`);
md.push("```");
for (const r of bigFiles.slice(0, 20)) md.push(`${r.date}  ${r.install.slice(0, 8)} ${String(r.total).padStart(6)}  ${r.file}`);
md.push("```");
md.push("");

// =====================================================================
// 3. DISCARDS — full content
// =====================================================================
md.push(`## 3. discards`);
const dFiles = files.filter((f) => f.stream === "discards");
let allDiscards = [];
for (const f of dFiles) for (const r of await readNdjson(f.path)) allDiscards.push({ ...r, install: f.install, date: f.date, file: f.filename });
md.push(`- files: ${dFiles.length}, records: ${allDiscards.length}`);
const dStrat = new Map();
const dSeat = new Map();
const dTileId = new Map();
const dTile = new Map();
for (const r of allDiscards) {
  inc(dStrat, String(r.strategy));
  inc(dSeat, String(r.seat));
  inc(dTileId, String(r.tile_id));
  inc(dTile, String(r.tile));
}
md.push(`- strategy distribution: ${[...dStrat.entries()].map(([k, v]) => `${k}=${v}`).join(", ")}`);
md.push(`- seat distribution: ${[...dSeat.entries()].map(([k, v]) => `${k}=${v}`).join(", ")}`);
md.push(`- tile_id distribution: ${[...dTileId.entries()].map(([k, v]) => `${k}=${v}`).join(", ")}`);
md.push(`- tile distribution: ${[...dTile.entries()].map(([k, v]) => `${k}=${v}`).join(", ")}`);
md.push(`- ALL records:`);
md.push("```json");
for (const r of allDiscards) md.push(JSON.stringify(r));
md.push("```");
md.push("");

// =====================================================================
// 4. SIGPROBES — timing + slide table
// =====================================================================
md.push(`## 4. sigprobes`);
const sFiles = files.filter((f) => f.stream === "sigprobes");
const sigEntries = [];
for (const f of sFiles) for (const r of await readNdjson(f.path)) sigEntries.push({ ...r, install: f.install, date: f.date });
md.push(`- ${sigEntries.length} probes, all signature \`${[...new Set(sigEntries.map((s) => s.name))].join(",")}\``);
md.push(`- success rate: ${sigEntries.filter((s) => s.success).length}/${sigEntries.length}`);
md.push(`- elapsed_ms quantiles: min=${Math.min(...sigEntries.map((s) => s.elapsed_ms))}, max=${Math.max(...sigEntries.map((s) => s.elapsed_ms))}, mean=${(sigEntries.reduce((s, x) => s + x.elapsed_ms, 0) / sigEntries.length).toFixed(2)}`);
md.push(`### per-probe detail (install / date / addr / elapsed)`);
md.push("```");
for (const s of sigEntries.sort((a, b) => (a.date + a.install).localeCompare(b.date + b.install))) {
  md.push(`${s.date}  ${s.install.slice(0, 8)}  addr=${s.addr ?? "(null)"}  elapsed_ms=${s.elapsed_ms}  ok=${s.success}`);
}
md.push("```");
// Compute apparent base of ffxiv_dx11.exe per install (handler offset is 0x1A20A36)
const HANDLER_RVA = 0x1A20A36;
md.push(`### Apparent ffxiv_dx11.exe base addresses (handler rva 0x${HANDLER_RVA.toString(16)})`);
md.push("```");
for (const s of sigEntries) {
  if (!s.addr) continue;
  const a = BigInt(s.addr);
  const base = a - BigInt(HANDLER_RVA);
  md.push(`${s.date}  ${s.install.slice(0, 8)}  base=0x${base.toString(16)}`);
}
md.push("```");
md.push("");

// =====================================================================
// 5. INPUTS — full survey
// =====================================================================
md.push(`## 5. inputs`);
const iFiles = files.filter((f) => f.stream === "inputs");
const iRecs = [];
for (const f of iFiles) for (const r of await readNdjson(f.path)) iRecs.push({ ...r, install: f.install, date: f.date });
const iAddons = new Map();
const iCounts = new Map();
const iValuePatterns = new Map();
for (const r of iRecs) {
  inc(iAddons, String(r.addon));
  inc(iCounts, String(r.count));
  if (Array.isArray(r.values)) {
    const pat = r.values.map((v) => typeof v === "number" && v > 100 ? `n>${100}` : String(v)).join(",");
    inc(iValuePatterns, pat);
  }
}
md.push(`- ${iRecs.length} records across ${iFiles.length} files`);
md.push(`- addon distribution: ${[...iAddons.entries()].map(([k, v]) => `${k}=${v}`).join(", ")}`);
md.push(`- count distribution: ${[...iCounts.entries()].map(([k, v]) => `${k}=${v}`).join(", ")}`);
md.push(`- top values patterns:`);
for (const [p, c] of [...iValuePatterns.entries()].sort((a, b) => b[1] - a[1]).slice(0, 20)) md.push(`  - \`[${p}]\` × ${c}`);
md.push(`### sample inputs across installs`);
md.push("```json");
for (const r of iRecs.slice(0, 30)) md.push(JSON.stringify(r));
md.push("```");

// =====================================================================
// 6. FINDINGS — paths leaked
// =====================================================================
md.push("");
md.push(`## 6. findings`);
const ffiles = files.filter((f) => f.stream === "findings");
const fRecs = [];
for (const f of ffiles) for (const r of await readNdjson(f.path)) fRecs.push({ ...r, install: f.install, date: f.date });
md.push(`- ${fRecs.length} records (all kind=layouts_loaded)`);
md.push(`- distinct \`data.dir\` values across installs:`);
md.push("```");
const dirs = new Set(fRecs.map((r) => r.data?.dir));
for (const d of dirs) md.push(d);
md.push("```");

await writeFile(OUT_MD, md.join("\n"), "utf8");
console.log(`Wrote ${OUT_MD} (${md.length} lines)`);
