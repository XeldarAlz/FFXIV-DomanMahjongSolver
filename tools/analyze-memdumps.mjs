// Aggregate analyzer for memdump NDJSON files in a directory.
// Produces summary stats useful for offline RE / capture-quality review.

import { readFile, readdir, writeFile } from "node:fs/promises";
import { gunzipSync } from "node:zlib";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const dir = process.argv[2];
const outJson = process.argv[3] ?? join(dir ?? ".", "_analysis.json");
if (!dir) { console.error("Usage: node tools/analyze-memdumps.mjs <dir> [out-json]"); process.exit(2); }

const files = (await readdir(dir)).filter(f => f.endsWith(".gz")).sort();
console.log(`${files.length} file(s) in ${dir}\n`);

let totalBytesRaw = 0;
let totalRecords = 0;
const recordsPerFile = [];
const reasonCounts = {};
const seqGaps = []; // gap between consecutive records' timestamps (ms)
const seqJumps = []; // gap between consecutive seq numbers
const seqMissing = []; // gaps where seq jumps > 1
const hashes = new Set();
const dupHashes = new Map();
const addonAddrs = new Set();
const rootAddrs = new Set();
const atkAddrs = new Set();
const atkCounts = {};
const seatPoolsCounts = {};
const blobSizes = { addon: [], root: [], atk: [] };
const allTimes = [];
let prevTs = null;
let prevSeq = null;
let firstTs = null;
let lastTs = null;
let earliestSeq = Infinity;
let latestSeq = -Infinity;
const tileCharSet = new Set(); // any string-typed values that look like tile descriptors

const fileGapsSeq = []; // when seq resets across files (likely session boundary)

for (const f of files) {
  const raw = await readFile(join(dir, f));
  totalBytesRaw += raw.byteLength;
  const data = (raw[0] === 0x1f && raw[1] === 0x8b) ? gunzipSync(raw) : raw;
  const lines = data.toString("utf8").split("\n").filter(Boolean);
  recordsPerFile.push({ file: f, records: lines.length, bytes: data.byteLength });

  for (const line of lines) {
    let rec;
    try { rec = JSON.parse(line); } catch { continue; }
    totalRecords++;

    if (rec.reason) reasonCounts[rec.reason] = (reasonCounts[rec.reason] ?? 0) + 1;

    if (typeof rec.t === "string") {
      const ts = Date.parse(rec.t);
      if (!isNaN(ts)) {
        if (firstTs === null || ts < firstTs) firstTs = ts;
        if (lastTs === null || ts > lastTs) lastTs = ts;
        allTimes.push(ts);
        if (prevTs !== null) seqGaps.push(ts - prevTs);
        prevTs = ts;
      }
    }

    if (typeof rec.seq === "number") {
      if (rec.seq < earliestSeq) earliestSeq = rec.seq;
      if (rec.seq > latestSeq) latestSeq = rec.seq;
      if (prevSeq !== null) {
        const jump = rec.seq - prevSeq;
        seqJumps.push(jump);
        if (jump > 1) seqMissing.push({ from: prevSeq, to: rec.seq, gap: jump - 1 });
        if (jump < 1) fileGapsSeq.push({ from: prevSeq, to: rec.seq });
      }
      prevSeq = rec.seq;
    }

    if (typeof rec.hash === "string") {
      if (hashes.has(rec.hash)) dupHashes.set(rec.hash, (dupHashes.get(rec.hash) ?? 1) + 1);
      else hashes.add(rec.hash);
    }
    if (typeof rec.addon_addr === "number") addonAddrs.add(rec.addon_addr);
    if (typeof rec.root_addr === "number") rootAddrs.add(rec.root_addr);
    if (typeof rec.atk_addr === "number") atkAddrs.add(rec.atk_addr);
    if (typeof rec.atk_count === "number") atkCounts[rec.atk_count] = (atkCounts[rec.atk_count] ?? 0) + 1;

    const sp = rec.seat_pools;
    const spLen = Array.isArray(sp) ? sp.length : (sp == null ? 0 : -1);
    seatPoolsCounts[spLen] = (seatPoolsCounts[spLen] ?? 0) + 1;

    if (typeof rec.addon_b64 === "string") blobSizes.addon.push(rec.addon_b64.length);
    if (typeof rec.root_b64 === "string") blobSizes.root.push(rec.root_b64.length);
    if (typeof rec.atk_b64 === "string") blobSizes.atk.push(rec.atk_b64.length);
  }
}

const pct = (arr, p) => {
  if (!arr.length) return null;
  const sorted = [...arr].sort((a,b) => a-b);
  const idx = Math.min(sorted.length - 1, Math.max(0, Math.floor(p * sorted.length)));
  return sorted[idx];
};
const stats = (arr) => arr.length ? {
  n: arr.length,
  min: Math.min(...arr),
  p50: pct(arr, 0.5),
  p95: pct(arr, 0.95),
  p99: pct(arr, 0.99),
  max: Math.max(...arr),
  mean: arr.reduce((a,b)=>a+b,0) / arr.length,
} : null;

const fmtAddr = (n) => "0x" + BigInt(n).toString(16);
const summary = {
  files: files.length,
  totalBytesOnDisk: totalBytesRaw,
  totalRecords,
  recordsPerFile: stats(recordsPerFile.map(x => x.records)),
  bytesPerFile: stats(recordsPerFile.map(x => x.bytes)),
  timeSpan: firstTs && lastTs ? {
    from: new Date(firstTs).toISOString(),
    to: new Date(lastTs).toISOString(),
    durationMin: (lastTs - firstTs) / 60000,
  } : null,
  seqRange: { earliest: earliestSeq, latest: latestSeq, span: latestSeq - earliestSeq },
  seqResets: fileGapsSeq.length, // monotonic violations (likely process restart)
  seqMissingCount: seqMissing.reduce((s, x) => s + x.gap, 0),
  seqMissingExamples: seqMissing.slice(0, 5),
  inter_record_ms: stats(seqGaps),
  reasonCounts,
  uniqueHashes: hashes.size,
  duplicateHashes: dupHashes.size,
  topDuplicates: [...dupHashes.entries()].sort((a,b) => b[1] - a[1]).slice(0, 5),
  addonAddrs: { unique: addonAddrs.size, sample: [...addonAddrs].slice(0, 5).map(fmtAddr) },
  rootAddrs:  { unique: rootAddrs.size,  sample: [...rootAddrs].slice(0, 5).map(fmtAddr) },
  atkAddrs:   { unique: atkAddrs.size,   sample: [...atkAddrs].slice(0, 5).map(fmtAddr) },
  atkCountDistribution: atkCounts,
  seatPoolsLengthDistribution: seatPoolsCounts,
  blob_b64_chars: {
    addon: stats(blobSizes.addon),
    root: stats(blobSizes.root),
    atk: stats(blobSizes.atk),
  },
};

console.log(JSON.stringify(summary, null, 2));
await writeFile(outJson, JSON.stringify(summary, null, 2), "utf8");
console.log(`\nWrote ${outJson}`);
