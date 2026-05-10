// Categorize and summarize every telemetry artifact under .local/b2-all/.
// Walks the mirror tree, groups by stream + install + date, decompresses each
// .gz on the fly, and inspects content shape per stream. Emits a JSON report
// and a human-readable Markdown summary.
//
// Usage: node tools/categorize-telemetry.mjs [root] [out-md] [out-json]

import { readdir, readFile, stat, writeFile } from "node:fs/promises";
import { join, relative } from "node:path";
import { gunzipSync } from "node:zlib";

const ROOT = process.argv[2] ?? ".local/b2-all";
const OUT_MD = process.argv[3] ?? ".local/telemetry-report.md";
const OUT_JSON = process.argv[4] ?? ".local/telemetry-report.json";
const WORKER_NDJSON = ".local/worker-events-7d.ndjson";

async function walk(dir) {
  const out = [];
  const entries = await readdir(dir, { withFileTypes: true });
  for (const e of entries) {
    const p = join(dir, e.name);
    if (e.isDirectory()) out.push(...await walk(p));
    else if (e.isFile()) out.push(p);
  }
  return out;
}

const files = await walk(ROOT);
const objects = files
  .map((p) => {
    const rel = relative(ROOT, p).split(/[\\/]/);
    if (rel.length < 4) return null;
    const [stream, install, date, ...rest] = rel;
    return { path: p, key: rel.join("/"), stream, install, date, filename: rest.join("/") };
  })
  .filter(Boolean);

console.log(`[cat] ${objects.length} objects under ${ROOT}`);

// --- Aggregate dimensions ---
const byStream = new Map();
const byInstall = new Map();
const byInstallStream = new Map();
const byDate = new Map();
const byInstallDate = new Map();

const streamSamples = {};
const errorMessages = [];
const findingsRecords = [];
const sigprobeRecords = [];
const inputRecords = [];
const discardRecords = [];
const gameRecords = [];
const memdumpRecords = [];
const filenamePatterns = new Map(); // pattern -> count
const pluginVersions = new Map();
const gameVersions = new Map();

const numStat = (arr) => {
  if (!arr.length) return null;
  const s = [...arr].sort((a, b) => a - b);
  const sum = s.reduce((a, b) => a + b, 0);
  return { n: s.length, min: s[0], p50: s[Math.floor(s.length * 0.5)], p95: s[Math.floor(s.length * 0.95)], max: s[s.length - 1], sum, mean: sum / s.length };
};
const inc = (m, k, n = 1) => m.set(k, (m.get(k) ?? 0) + n);
const incBytes = (m, k, b) => {
  const e = m.get(k) ?? { count: 0, bytes: 0 };
  e.count++; e.bytes += b; m.set(k, e);
};

for (const obj of objects) {
  const st = await stat(obj.path);
  obj.size = st.size;
  // Pattern from filename: replace date/time digits with N
  const pat = obj.filename.replace(/\d/g, "N");
  inc(filenamePatterns, `${obj.stream}: ${pat}`);

  incBytes(byStream, obj.stream, obj.size);
  incBytes(byInstall, obj.install, obj.size);
  incBytes(byInstallStream, `${obj.install}|${obj.stream}`, obj.size);
  incBytes(byDate, obj.date, obj.size);
  incBytes(byInstallDate, `${obj.install}|${obj.date}`, obj.size);

  // Decompress + sample first record
  let raw;
  try { raw = await readFile(obj.path); } catch { continue; }
  let body;
  try {
    body = (raw[0] === 0x1f && raw[1] === 0x8b) ? gunzipSync(raw) : raw;
  } catch (e) {
    body = null;
  }
  if (!body) continue;
  const text = body.toString("utf8");
  const lines = text.split("\n").filter(Boolean);
  obj.lines = lines.length;
  obj.uncompressed = body.byteLength;

  if (!streamSamples[obj.stream]) {
    streamSamples[obj.stream] = {
      sampleFile: obj.key,
      lineCount: lines.length,
      firstLine: lines[0]?.slice(0, 1500) ?? "",
      lastLine: lines[lines.length - 1]?.slice(0, 800) ?? "",
    };
  }

  // Per-stream parsing
  for (let li = 0; li < lines.length; li++) {
    const line = lines[li];
    let rec;
    try { rec = JSON.parse(line); } catch { continue; }
    if (rec.plugin_version) inc(pluginVersions, rec.plugin_version);
    if (rec.game_version) inc(gameVersions, rec.game_version);

    if (obj.stream === "errors") {
      errorMessages.push({ install: obj.install, date: obj.date, msg: rec.message ?? rec.error ?? rec.msg ?? "(no msg)", level: rec.level, t: rec.t ?? rec.timestamp, exc: rec.exception?.split("\n")[0] });
    } else if (obj.stream === "findings") {
      findingsRecords.push({ install: obj.install, date: obj.date, ...rec });
    } else if (obj.stream === "sigprobes") {
      sigprobeRecords.push({ install: obj.install, date: obj.date, ...rec });
    } else if (obj.stream === "inputs") {
      inputRecords.push({ install: obj.install, date: obj.date, kind: rec.kind ?? rec.type ?? rec.event });
    } else if (obj.stream === "discards") {
      discardRecords.push({ install: obj.install, date: obj.date, ...rec });
    } else if (obj.stream === "games") {
      if (li === 0) gameRecords.push({ install: obj.install, date: obj.date, file: obj.filename, firstReason: rec.reason ?? rec.event ?? rec.kind, lines: lines.length });
    } else if (obj.stream === "memdumps") {
      if (li === 0) memdumpRecords.push({ install: obj.install, date: obj.date, file: obj.filename, lines: lines.length, firstReason: rec.reason });
    }
  }
}

// --- Build summary ---
const fmtBytes = (b) => b >= 1048576 ? `${(b / 1048576).toFixed(2)} MB` : b >= 1024 ? `${(b / 1024).toFixed(1)} KB` : `${b} B`;
const sortByBytes = (m) => [...m.entries()].sort((a, b) => b[1].bytes - a[1].bytes);

const streamLines = sortByBytes(byStream).map(([s, e]) => `  ${s.padEnd(10)} ${String(e.count).padStart(5)} files  ${fmtBytes(e.bytes).padStart(10)}`).join("\n");
const installLines = sortByBytes(byInstall).map(([i, e]) => `  ${i.slice(0, 8)}  ${String(e.count).padStart(5)} files  ${fmtBytes(e.bytes).padStart(10)}`).join("\n");
const dateLines = sortByBytes(byDate).map(([d, e]) => `  ${d}  ${String(e.count).padStart(5)} files  ${fmtBytes(e.bytes).padStart(10)}`).join("\n");

const installStreamMatrix = (() => {
  const installs = [...byInstall.keys()].sort();
  const streams = [...byStream.keys()].sort();
  const rows = [];
  rows.push("install\\stream | " + streams.join(" | "));
  for (const i of installs) {
    const cells = streams.map((s) => {
      const e = byInstallStream.get(`${i}|${s}`);
      return e ? `${e.count}/${fmtBytes(e.bytes)}` : "-";
    });
    rows.push(i.slice(0, 8) + " | " + cells.join(" | "));
  }
  return rows.join("\n");
})();

// Worker events (acceptance/reject ratios) from CF
let workerSummary = null;
try {
  const wraw = await readFile(WORKER_NDJSON, "utf8");
  const wlines = wraw.replace(/^﻿/, "").split("\n").filter(Boolean);
  const events = wlines.map((l) => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);
  const byEvent = new Map();
  const byInstallW = new Map();
  let acceptedBytes = 0;
  for (const e of events) {
    inc(byEvent, `${e.level || "?"}|${e.event || "?"}`);
    if (e.install) inc(byInstallW, e.install);
    if (e.event === "accepted" && typeof e.bytes === "number") acceptedBytes += e.bytes;
  }
  workerSummary = { totalEvents: events.length, byEvent: [...byEvent.entries()].sort((a, b) => b[1] - a[1]), distinctInstalls: byInstallW.size, acceptedBytes };
} catch (e) {
  workerSummary = { error: e.message };
}

const errorBreakdown = (() => {
  const counts = new Map();
  for (const e of errorMessages) inc(counts, (e.exc ?? e.msg ?? "").slice(0, 120));
  return [...counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 30);
})();

const findingTypes = (() => {
  const counts = new Map();
  for (const r of findingsRecords) {
    const t = r.kind ?? r.type ?? r.event ?? r.signal ?? r.name ?? "(unknown)";
    inc(counts, t);
  }
  return [...counts.entries()].sort((a, b) => b[1] - a[1]);
})();

const sigprobeTypes = (() => {
  const counts = new Map();
  for (const r of sigprobeRecords) {
    const t = r.kind ?? r.type ?? r.signature ?? r.name ?? "(unknown)";
    inc(counts, t);
  }
  return [...counts.entries()].sort((a, b) => b[1] - a[1]);
})();

const inputKinds = (() => {
  const counts = new Map();
  for (const r of inputRecords) inc(counts, String(r.kind ?? "(unknown)"));
  return [...counts.entries()].sort((a, b) => b[1] - a[1]);
})();

// Files per stream stats
const filesizeStats = {};
for (const [s, _] of byStream) {
  const sizes = objects.filter((o) => o.stream === s).map((o) => o.size);
  filesizeStats[s] = numStat(sizes);
}

// File pattern groups
const topPatterns = [...filenamePatterns.entries()].sort((a, b) => b[1] - a[1]);

// --- Markdown report ---
const md = [];
md.push(`# Mahjong Plugin Telemetry — Categorized Inventory`);
md.push(`Generated ${new Date().toISOString()} from \`${ROOT}\` (${objects.length} objects).`);
md.push(``);
md.push(`## 1. Top-line totals`);
md.push(`- Objects on B2: **${objects.length}**`);
md.push(`- Bytes on B2 (compressed): **${fmtBytes(objects.reduce((s, o) => s + o.size, 0))}**`);
md.push(`- Distinct install_ids: **${byInstall.size}**`);
md.push(`- Distinct dates: **${byDate.size}**`);
md.push(`- Streams represented: **${[...byStream.keys()].sort().join(", ")}**`);
md.push(``);
md.push(`## 2. By stream (the categorization)`);
md.push("```");
md.push("stream      files       bytes");
md.push(streamLines);
md.push("```");
md.push(``);
md.push(`### Stream-by-stream`);
for (const [s, e] of sortByBytes(byStream)) {
  md.push(`#### \`${s}\` — ${e.count} files, ${fmtBytes(e.bytes)}`);
  const sample = streamSamples[s];
  const sz = filesizeStats[s];
  if (sz) md.push(`- File-size (compressed): min=${fmtBytes(sz.min)} p50=${fmtBytes(sz.p50)} p95=${fmtBytes(sz.p95)} max=${fmtBytes(sz.max)}`);
  if (sample) {
    md.push(`- Sample file: \`${sample.sampleFile}\` (${sample.lineCount} line${sample.lineCount === 1 ? "" : "s"})`);
    if (sample.firstLine) {
      md.push("```json");
      md.push(sample.firstLine);
      md.push("```");
    }
  }
}
md.push(``);
md.push(`## 3. By install (anonymous GUID — first 8 chars)`);
md.push("```");
md.push("install   files       bytes");
md.push(installLines);
md.push("```");
md.push(``);
md.push(`## 4. By date (UTC)`);
md.push("```");
md.push("date         files       bytes");
md.push(dateLines);
md.push("```");
md.push(``);
md.push(`## 5. Install × stream matrix (count/size)`);
md.push("```");
md.push(installStreamMatrix);
md.push("```");
md.push(``);
md.push(`## 6. Plugin / game versions seen in payloads`);
md.push(`Plugin versions:`);
for (const [v, c] of [...pluginVersions.entries()].sort((a, b) => b[1] - a[1])) md.push(`- \`${v}\` × ${c}`);
md.push(`Game versions:`);
for (const [v, c] of [...gameVersions.entries()].sort((a, b) => b[1] - a[1])) md.push(`- \`${v}\` × ${c}`);
md.push(``);
md.push(`## 7. Filename patterns (digits → N)`);
md.push("```");
for (const [p, c] of topPatterns) md.push(`${String(c).padStart(5)}  ${p}`);
md.push("```");
md.push(``);
if (errorMessages.length) {
  md.push(`## 8. errors stream — top exception/messages`);
  md.push("```");
  for (const [m, c] of errorBreakdown) md.push(`${String(c).padStart(4)}  ${m}`);
  md.push("```");
}
if (findingsRecords.length) {
  md.push(`## 9. findings stream — type histogram`);
  md.push("```");
  for (const [t, c] of findingTypes) md.push(`${String(c).padStart(4)}  ${t}`);
  md.push("```");
  md.push(`Sample finding records:`);
  md.push("```json");
  for (const r of findingsRecords.slice(0, 5)) md.push(JSON.stringify(r).slice(0, 400));
  md.push("```");
}
if (sigprobeRecords.length) {
  md.push(`## 10. sigprobes stream — kind histogram`);
  md.push("```");
  for (const [t, c] of sigprobeTypes) md.push(`${String(c).padStart(4)}  ${t}`);
  md.push("```");
  md.push(`Sample sigprobe records:`);
  md.push("```json");
  for (const r of sigprobeRecords.slice(0, 5)) md.push(JSON.stringify(r).slice(0, 400));
  md.push("```");
}
if (inputRecords.length) {
  md.push(`## 11. inputs stream — kind histogram`);
  md.push("```");
  for (const [t, c] of inputKinds) md.push(`${String(c).padStart(4)}  ${t}`);
  md.push("```");
}
if (discardRecords.length) {
  md.push(`## 12. discards stream — sample`);
  md.push("```json");
  for (const r of discardRecords.slice(0, 5)) md.push(JSON.stringify(r).slice(0, 400));
  md.push("```");
}
md.push(``);
md.push(`## 13. Cloudflare worker events (last 7 days, observability API)`);
md.push("```json");
md.push(JSON.stringify(workerSummary, null, 2));
md.push("```");

await writeFile(OUT_MD, md.join("\n"), "utf8");

const json = {
  totals: { objects: objects.length, bytes: objects.reduce((s, o) => s + o.size, 0), installs: byInstall.size, dates: byDate.size },
  streams: Object.fromEntries([...byStream.entries()].map(([s, e]) => [s, { count: e.count, bytes: e.bytes, sizeStats: filesizeStats[s] }])),
  installs: Object.fromEntries([...byInstall.entries()].map(([i, e]) => [i, { count: e.count, bytes: e.bytes }])),
  dates: Object.fromEntries(byDate.entries()),
  install_x_stream: Object.fromEntries([...byInstallStream.entries()]),
  pluginVersions: Object.fromEntries(pluginVersions),
  gameVersions: Object.fromEntries(gameVersions),
  filenamePatterns: Object.fromEntries(filenamePatterns),
  errorBreakdown,
  findingTypes,
  sigprobeTypes,
  inputKinds,
  discardSample: discardRecords.slice(0, 10),
  workerSummary,
  samplePerStream: streamSamples,
};
await writeFile(OUT_JSON, JSON.stringify(json, null, 2), "utf8");

console.log(`[cat] wrote ${OUT_MD}`);
console.log(`[cat] wrote ${OUT_JSON}`);
