// Cross-install seat-offset candidate scan. For each install that produced
// gameplay today, pick the FIRST memdump in the first non-truncated hand
// (>= 10 events), decode the addon blob, then enumerate small-int candidates
// (byte 0..3 and int32 0..3) across installs. A real seat field will vary
// between installs (different users sit in different seats); fields that are
// constant across all 8 installs are layout-baked constants, not state.
//
// Usage:
//   node tools/scan-seat-candidates.mjs <date-root>
//   e.g. node tools/scan-seat-candidates.mjs .local/by-date/2026-05-11

import { createReadStream } from "node:fs";
import { readdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createGunzip } from "node:zlib";
import readline from "node:readline";

const root = process.argv[2] ?? ".local/by-date/2026-05-11";

import { readFile } from "node:fs/promises";
import { gunzipSync } from "node:zlib";

async function* readGz(path) {
  let body;
  try {
    const raw = await readFile(path);
    body = (raw[0] === 0x1f && raw[1] === 0x8b) ? gunzipSync(raw) : raw;
  } catch (e) {
    console.error(`[scan-seat] skip ${path}: ${e.code ?? e.message}`);
    return;
  }
  for (const l of body.toString("utf8").split(/\r?\n/)) if (l.trim()) yield l;
}

async function listDir(p) { try { return await readdir(p); } catch { return []; } }

// Pick the EARLIEST memdump record with reason=state-change for each install,
// from a hand file with at least 10 events (avoids the spurious truncated hands).
async function pickRepresentativeDump(install) {
  // 1. Find a "real" hand file: >=10 lines.
  const gameDir = join(root, "games", install);
  let chosenStartT = null;
  for (const f of (await listDir(gameDir)).filter((n) => n.endsWith(".gz"))) {
    let lines = 0;
    let firstStartT = null;
    for await (const line of readGz(join(gameDir, f))) {
      lines++;
      if (firstStartT === null) {
        const j = JSON.parse(line);
        if (j.e === "hand-start") firstStartT = j.t;
      }
      if (lines >= 10) break;
    }
    if (lines >= 10 && firstStartT) { chosenStartT = firstStartT; break; }
  }
  if (!chosenStartT) return null;

  // 2. Find the first memdump record with t >= chosenStartT.
  const memDir = join(root, "memdumps", install);
  const files = (await listDir(memDir)).filter((n) => n.endsWith(".gz")).sort();
  for (const f of files) {
    for await (const line of readGz(join(memDir, f))) {
      const j = JSON.parse(line);
      if (typeof j.addon_b64 !== "string") continue;
      if (j.t < chosenStartT) continue;
      return { install, t: j.t, seq: j.seq, reason: j.reason, buf: Buffer.from(j.addon_b64, "base64") };
    }
  }
  return null;
}

const installs = (await listDir(join(root, "memdumps"))).filter((d) => d.length > 8);
console.log(`[scan-seat] ${installs.length} active installs to sample`);

const samples = [];
for (const i of installs) {
  const s = await pickRepresentativeDump(i);
  if (s) {
    samples.push(s);
    console.log(`  ${i.slice(0, 8)}  seq=${s.seq}  buf=${s.buf.length}B  ${s.reason}`);
  } else {
    console.log(`  ${i.slice(0, 8)}  (no representative dump)`);
  }
}

if (samples.length < 2) { console.error("not enough samples"); process.exit(1); }

const minLen = Math.min(...samples.map((s) => s.buf.length));
console.log(`[scan-seat] common length: ${minLen} bytes`);

// BYTE candidates: offset where every install reads a small int (0..3) AND at
// least 2 distinct values appear across installs.
const byteCands = [];
for (let off = 0; off < minLen; off++) {
  const vals = samples.map((s) => s.buf[off]);
  if (!vals.every((v) => v <= 3)) continue;
  const distinct = new Set(vals);
  if (distinct.size < 2) continue;
  byteCands.push({ off, vals });
}

// INT32-LE candidates: same, 4-byte aligned to multiples of 4.
const intCands = [];
for (let off = 0; off + 4 <= minLen; off += 4) {
  const vals = samples.map((s) => s.buf.readInt32LE(off));
  if (!vals.every((v) => v >= 0 && v <= 3)) continue;
  const distinct = new Set(vals);
  if (distinct.size < 2) continue;
  intCands.push({ off, vals });
}

// Tighter filter: prefer candidates whose values use 3+ distinct seats (not
// just two), and concentrate in offset ranges we'd expect for seat/round/dealer
// (the existing find_seat_offsets.py noted +0x130, +0x1248, +0x12BC).
const interesting = (c) => new Set(c.vals).size >= 3;
const sortByInterest = (a, b) => {
  const ad = new Set(a.vals).size, bd = new Set(b.vals).size;
  if (ad !== bd) return bd - ad;
  return a.off - b.off;
};

const out = [];
out.push(`# Seat-Offset Cross-Install Scan`);
out.push("");
out.push(`Sampled ${samples.length} active installs from ${root}.`);
out.push(`Common addon length: ${minLen} bytes.`);
out.push("");
out.push(`## Install → first usable memdump`);
out.push("```");
for (const s of samples) out.push(`  ${s.install}  t=${s.t}  seq=${s.seq}  ${s.reason}  ${s.buf.length}B`);
out.push("```");
out.push("");
out.push(`## Byte offsets holding 0..3 with ≥2 distinct values across installs`);
out.push(`Found ${byteCands.length}. Most diverse first:`);
out.push("```");
for (const c of byteCands.sort(sortByInterest).slice(0, 80)) {
  out.push(`  +0x${c.off.toString(16).padStart(4, "0")}  [${c.vals.join(",")}]  (${new Set(c.vals).size} distinct)`);
}
out.push("```");
out.push("");
out.push(`## Int32-LE offsets holding 0..3 with ≥2 distinct values across installs`);
out.push(`Found ${intCands.length}. Most diverse first:`);
out.push("```");
for (const c of intCands.sort(sortByInterest).slice(0, 80)) {
  out.push(`  +0x${c.off.toString(16).padStart(4, "0")}  [${c.vals.join(",")}]  (${new Set(c.vals).size} distinct)`);
}
out.push("```");
out.push("");
out.push(`## Known prior candidates (from tools/find_seat_offsets.py)`);
out.push("");
for (const off of [0x130, 0x1248, 0x12BC]) {
  if (off >= minLen) { out.push(`- +0x${off.toString(16)}: OOB (minLen=${minLen})`); continue; }
  const bytes = samples.map((s) => s.buf[off]);
  const ints  = (off + 4 <= minLen) ? samples.map((s) => s.buf.readInt32LE(off)) : null;
  out.push(`- +0x${off.toString(16).padStart(4, "0")}  byte=[${bytes.join(",")}]  int32=[${ints ? ints.join(",") : "OOB"}]`);
}

const reportPath = join(root, "_seat-offset-scan.md");
await writeFile(reportPath, out.join("\n"), "utf8");
console.log(`[scan-seat] wrote ${reportPath}`);

// Print top 12 of each category for terminal triage:
console.log("\n=== top byte candidates (most diverse) ===");
for (const c of byteCands.sort(sortByInterest).slice(0, 12)) {
  console.log(`  +0x${c.off.toString(16).padStart(4, "0")}  [${c.vals.join(",")}]  (${new Set(c.vals).size} distinct)`);
}
console.log("\n=== top int32 candidates (most diverse) ===");
for (const c of intCands.sort(sortByInterest).slice(0, 12)) {
  console.log(`  +0x${c.off.toString(16).padStart(4, "0")}  [${c.vals.join(",")}]  (${new Set(c.vals).size} distinct)`);
}
