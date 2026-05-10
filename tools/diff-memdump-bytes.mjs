// Byte-position variability analysis across all memdump snapshots.
//
// For each fixed-size blob (addon, root) and for each byte position in
// that blob, count how many distinct values were observed across the full
// session. Output a per-position summary and a stride-aggregated map so
// it's easy to see which 16-byte / 32-byte struct fields carry signal.
//
// Output:
//   <dir>/_byte-variability.json    — full per-position distinct counts
//   <dir>/_byte-variability.csv     — same, CSV form for spreadsheets
//   <dir>/_byte-variability.txt     — human-readable summary tables

import { readFile, readdir, writeFile } from "node:fs/promises";
import { gunzipSync } from "node:zlib";
import { join } from "node:path";

const dir = process.argv[2];
if (!dir) { console.error("Usage: node tools/diff-memdump-bytes.mjs <dir>"); process.exit(2); }

const files = (await readdir(dir)).filter(f => f.endsWith(".gz")).sort();
console.log(`Analyzing ${files.length} file(s) in ${dir}`);

// We'll size buffers from the first record we see; addon=4866, root=4098.
let addonLen = -1;
let rootLen = -1;

// 256-bucket histogram per position. Encoded as Uint16 (max 65535 per bucket
// is fine — even at 11k records per file × thousands of files we'd need
// a lot more to overflow).
let addonHist = null;  // Uint16Array of length 256 * addonLen
let rootHist = null;

let totalRecords = 0;

const decodeB64 = (s) => Buffer.from(s, "base64");

for (const f of files) {
  const raw = await readFile(join(dir, f));
  const data = (raw[0] === 0x1f && raw[1] === 0x8b) ? gunzipSync(raw) : raw;
  const lines = data.toString("utf8").split("\n").filter(Boolean);

  for (const line of lines) {
    let rec;
    try { rec = JSON.parse(line); } catch { continue; }

    if (typeof rec.addon_b64 === "string") {
      const b = decodeB64(rec.addon_b64);
      if (addonLen < 0) {
        addonLen = b.length;
        addonHist = new Uint16Array(256 * addonLen);
      }
      if (b.length === addonLen) {
        for (let i = 0; i < addonLen; i++) {
          const v = b[i];
          const idx = i * 256 + v;
          if (addonHist[idx] < 0xffff) addonHist[idx]++;
        }
      }
    }

    if (typeof rec.root_b64 === "string") {
      const b = decodeB64(rec.root_b64);
      if (rootLen < 0) {
        rootLen = b.length;
        rootHist = new Uint16Array(256 * rootLen);
      }
      if (b.length === rootLen) {
        for (let i = 0; i < rootLen; i++) {
          const v = b[i];
          const idx = i * 256 + v;
          if (rootHist[idx] < 0xffff) rootHist[idx]++;
        }
      }
    }

    totalRecords++;
  }
}

console.log(`Processed ${totalRecords} records (addon=${addonLen}B, root=${rootLen}B)`);

// For each position, derive: distinct-values count, max-bucket count
// (= modal frequency), Shannon entropy (bits, 0..8).
const summarize = (hist, len) => {
  const distinct = new Uint16Array(len);
  const modeFreq = new Uint32Array(len);
  const entropy = new Float32Array(len);
  for (let i = 0; i < len; i++) {
    let nz = 0, total = 0, maxB = 0;
    for (let v = 0; v < 256; v++) {
      const c = hist[i * 256 + v];
      if (c > 0) { nz++; total += c; if (c > maxB) maxB = c; }
    }
    distinct[i] = nz;
    modeFreq[i] = maxB;
    if (total > 0) {
      let h = 0;
      for (let v = 0; v < 256; v++) {
        const c = hist[i * 256 + v];
        if (c > 0) {
          const p = c / total;
          h -= p * Math.log2(p);
        }
      }
      entropy[i] = h;
    }
  }
  return { distinct, modeFreq, entropy };
};

const addonSum = summarize(addonHist, addonLen);
const rootSum = summarize(rootHist, rootLen);

// Stride aggregation — for each block of `stride` bytes, take the max
// distinct count and mean entropy. 16-byte stride = typical x64 struct
// field alignment.
const strideSummary = (sum, len, stride) => {
  const blocks = [];
  for (let off = 0; off < len; off += stride) {
    const end = Math.min(off + stride, len);
    let maxDistinct = 0;
    let sumEntropy = 0;
    let n = 0;
    for (let i = off; i < end; i++) {
      if (sum.distinct[i] > maxDistinct) maxDistinct = sum.distinct[i];
      sumEntropy += sum.entropy[i];
      n++;
    }
    blocks.push({
      offset: off,
      offsetHex: "0x" + off.toString(16).padStart(4, "0"),
      maxDistinct,
      meanEntropy: +(sumEntropy / n).toFixed(3),
    });
  }
  return blocks;
};

const addonBlocks16 = strideSummary(addonSum, addonLen, 16);
const rootBlocks16 = strideSummary(rootSum, rootLen, 16);

// Categorize blocks: dead (distinct=1), low (distinct<=4), medium (<=32), high (>32).
const categorize = (blocks) => {
  const cat = { dead: 0, low: 0, medium: 0, high: 0 };
  for (const b of blocks) {
    if (b.maxDistinct <= 1) cat.dead++;
    else if (b.maxDistinct <= 4) cat.low++;
    else if (b.maxDistinct <= 32) cat.medium++;
    else cat.high++;
  }
  return cat;
};

console.log("\n=== addon (4866B) — 16-byte block variability ===");
console.log(`blocks: ${addonBlocks16.length}, ${JSON.stringify(categorize(addonBlocks16))}`);
console.log("\nTop 20 highest-entropy 16-byte blocks (likely signal-bearing):");
const addonTop = [...addonBlocks16].sort((a, b) => b.meanEntropy - a.meanEntropy).slice(0, 20);
for (const b of addonTop) console.log(`  ${b.offsetHex}  distinct=${b.maxDistinct}  entropy=${b.meanEntropy}`);

console.log("\n=== root (4098B) — 16-byte block variability ===");
console.log(`blocks: ${rootBlocks16.length}, ${JSON.stringify(categorize(rootBlocks16))}`);
console.log("\nTop 20 highest-entropy 16-byte blocks (likely signal-bearing):");
const rootTop = [...rootBlocks16].sort((a, b) => b.meanEntropy - a.meanEntropy).slice(0, 20);
for (const b of rootTop) console.log(`  ${b.offsetHex}  distinct=${b.maxDistinct}  entropy=${b.meanEntropy}`);

// "Plateau" detection: contiguous runs of dead blocks. Useful for spotting
// big regions you can drop from capture entirely.
const findDeadRuns = (blocks, blockBytes) => {
  const runs = [];
  let runStart = -1;
  for (let i = 0; i <= blocks.length; i++) {
    const dead = i < blocks.length && blocks[i].maxDistinct <= 1;
    if (dead && runStart < 0) runStart = i;
    else if (!dead && runStart >= 0) {
      const lengthBytes = (i - runStart) * blockBytes;
      runs.push({ startBlock: runStart, endBlock: i - 1, lengthBytes });
      runStart = -1;
    }
  }
  return runs.sort((a, b) => b.lengthBytes - a.lengthBytes);
};

const addonDead = findDeadRuns(addonBlocks16, 16);
const rootDead = findDeadRuns(rootBlocks16, 16);
console.log("\n=== addon — longest contiguous dead-byte runs (16B blocks where every byte was constant) ===");
for (const r of addonDead.slice(0, 10)) {
  const startOff = r.startBlock * 16;
  const endOff = (r.endBlock + 1) * 16;
  console.log(`  0x${startOff.toString(16).padStart(4,"0")}..0x${endOff.toString(16).padStart(4,"0")}  ${r.lengthBytes}B`);
}
console.log("\n=== root — longest contiguous dead-byte runs ===");
for (const r of rootDead.slice(0, 10)) {
  const startOff = r.startBlock * 16;
  const endOff = (r.endBlock + 1) * 16;
  console.log(`  0x${startOff.toString(16).padStart(4,"0")}..0x${endOff.toString(16).padStart(4,"0")}  ${r.lengthBytes}B`);
}

// Persist artifacts.
const outBase = dir;
const dump = {
  totalRecords,
  addonLen,
  rootLen,
  addonBlocks16,
  rootBlocks16,
  addonCategories: categorize(addonBlocks16),
  rootCategories: categorize(rootBlocks16),
  addonDeadRuns: addonDead,
  rootDeadRuns: rootDead,
};
await writeFile(join(outBase, "_byte-variability.json"), JSON.stringify(dump, null, 2), "utf8");

// CSV with per-byte detail (addon only, since root is similar pattern)
const csvLines = ["offset,offset_hex,distinct,entropy_bits,mode_freq"];
for (let i = 0; i < addonLen; i++) {
  csvLines.push([i, "0x" + i.toString(16).padStart(4, "0"), addonSum.distinct[i], addonSum.entropy[i].toFixed(3), addonSum.modeFreq[i]].join(","));
}
await writeFile(join(outBase, "_byte-variability-addon.csv"), csvLines.join("\n"), "utf8");

console.log(`\nWrote ${join(outBase, "_byte-variability.json")}`);
console.log(`Wrote ${join(outBase, "_byte-variability-addon.csv")}`);

// Sanity dump: total bytes savings if we dropped every 16-byte dead block
const addonDeadBytes = addonDead.reduce((s, r) => s + r.lengthBytes, 0);
const rootDeadBytes = rootDead.reduce((s, r) => s + r.lengthBytes, 0);
console.log(`\nAddon dead bytes (if dropped): ${addonDeadBytes}/${addonLen} = ${(addonDeadBytes/addonLen*100).toFixed(1)}%`);
console.log(`Root  dead bytes (if dropped): ${rootDeadBytes}/${rootLen} = ${(rootDeadBytes/rootLen*100).toFixed(1)}%`);
