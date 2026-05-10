// Empirical discovery of the per-seat discard-array offsets in the Doman
// addon. Reads a directory of memdump NDJSON.gz files (typically one
// session's worth, pulled from B2 with tools/b2-pull.mjs) and:
//
//   1. Walks records in seq order.
//   2. For every consecutive pair where exactly one seat's discard-count byte
//      incremented by 1, computes the addon byte-diff inside that seat's
//      0x2E0 block.
//   3. Aggregates the per-seat byte offsets that changed in lockstep with
//      the count increment.
//   4. Fits a (start, stride) line to the changed-byte positions across
//      multiple transitions, and infers the per-tile encoding (raw byte,
//      4-byte int, texture-relative).
//   5. Cross-checks the candidate against the inputs stream when available
//      (a self-discard in inputs has values=[15, tileTextureBase + tile_id];
//      decoding the discovered slot should reproduce that tile_id).
//   6. Emits a Markdown report and a layouts JSON patch.
//
// Usage:
//   node tools/find-discard-offset.mjs <memdump-dir> [inputs-dir]
//
// The dirs are local mirrors (one install / one date is enough). Inputs
// dir is optional but tightens the recommendation.
//
// Layout assumptions (Emj variant):
//   - Self / Shimocha / Toimen / Kamicha discard-count bytes at addon offsets
//     0x04FE / 0x07DE / 0x0ABE / 0x0D9E
//   - Each seat block is 0x2E0 bytes (the stride between seats)
//   - tileTextureBase = 76041 (from data/layouts/emj.json)
//
// If those constants drift, edit SEAT_BLOCKS / TILE_TEXTURE_BASE below or
// pass --layout=path/to/emj.json on the command line (TODO).

import { readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { gunzipSync } from "node:zlib";

const memdumpDir = process.argv[2];
const inputsDir = process.argv[3];
if (!memdumpDir) {
  console.error("Usage: node tools/find-discard-offset.mjs <memdump-dir> [inputs-dir]");
  process.exit(2);
}

const SEAT_BLOCKS = [
  { name: "self",     countByte: 0x04FE, blockEnd: 0x07DD },
  { name: "shimocha", countByte: 0x07DE, blockEnd: 0x0ABD },
  { name: "toimen",   countByte: 0x0ABE, blockEnd: 0x0D9D },
  { name: "kamicha",  countByte: 0x0D9E, blockEnd: 0x107D },
];
const TILE_TEXTURE_BASE = 76041;
const SEAT_BLOCK_SIZE = 0x2E0;

async function readNdjson(path) {
  const raw = await readFile(path);
  const body = (raw[0] === 0x1f && raw[1] === 0x8b) ? gunzipSync(raw) : raw;
  return body.toString("utf8")
    .split("\n").filter(Boolean)
    .map((l) => { try { return JSON.parse(l); } catch { return null; } })
    .filter(Boolean);
}

async function loadAllMemdumps(dir) {
  const all = [];
  const stack = [dir];
  while (stack.length) {
    const d = stack.pop();
    for (const e of await readdir(d, { withFileTypes: true })) {
      const p = join(d, e.name);
      if (e.isDirectory()) stack.push(p);
      else if (e.isFile() && e.name.endsWith(".gz")) {
        for (const r of await readNdjson(p)) all.push(r);
      }
    }
  }
  return all;
}

console.log(`[find-discard-offset] scanning ${memdumpDir}`);
const records = await loadAllMemdumps(memdumpDir);
console.log(`[find-discard-offset] ${records.length} records loaded`);

// Decode addon_b64 once per record; skip records lacking the blob.
for (const r of records) {
  if (typeof r.addon_b64 === "string") {
    try { r.__addon = Buffer.from(r.addon_b64, "base64"); }
    catch { r.__addon = null; }
  }
}
const usable = records
  .filter((r) => r.__addon && r.__addon.length >= 0x107E && typeof r.seq === "number")
  .sort((a, b) => a.seq - b.seq);
console.log(`[find-discard-offset] ${usable.length} usable records (have addon ≥ 0x107E and seq)`);

if (usable.length < 2) {
  console.error("[find-discard-offset] need ≥2 records — record a session first");
  process.exit(1);
}

// Read per-seat discard counts from a record's addon blob.
const seatCounts = (rec) => SEAT_BLOCKS.map((b) => rec.__addon[b.countByte]);

// Walk consecutive pairs and accumulate transitions where exactly one seat
// went +1. Pairs across reason boundaries are fine — we want every +1
// transition we can observe, not just (input-pre, input-post) brackets.
// state-change → state-change still works, just less temporally tight.
//
// We strongly prefer adjacent (input-pre → input-post) brackets when they
// exist: between those two captures, the only memory mutation is the
// click's effect (no UI tick, no animation frame), so the diff is far
// cleaner. Tag each transition with `tight` so we can score those higher.
const transitions = [];   // {prev, next, seatIndex, oldCount, newCount, tight}
let pairs = 0;
for (let i = 1; i < usable.length; i++) {
  const a = usable[i - 1];
  const b = usable[i];
  if (a.__addon.length !== b.__addon.length) continue;
  pairs++;
  const ac = seatCounts(a);
  const bc = seatCounts(b);
  let diffSeat = -1;
  let multiSeat = false;
  for (let s = 0; s < 4; s++) {
    if (bc[s] === ac[s] + 1) {
      if (diffSeat >= 0) { multiSeat = true; break; }
      diffSeat = s;
    } else if (bc[s] !== ac[s]) {
      // Anything other than 0 or +1 is noise (hand reset, simultaneous
      // increments, or a torn read). Drop the pair.
      diffSeat = -1;
      multiSeat = true;
      break;
    }
  }
  if (multiSeat || diffSeat < 0) continue;
  transitions.push({
    prev: a, next: b,
    seatIndex: diffSeat,
    oldCount: ac[diffSeat],
    newCount: bc[diffSeat],
    tight: a.reason === "input-pre" && b.reason === "input-post",
  });
}
console.log(`[find-discard-offset] ${pairs} adjacent pairs, ${transitions.length} clean +1 transitions`);

if (transitions.length === 0) {
  console.error("[find-discard-offset] no clean +1 transitions — capture a longer / fresher session");
  process.exit(1);
}

// For each transition, list byte offsets that changed inside that seat's
// block. We exclude the count byte itself (offset 0 within the block, by
// definition changed) and the score field (offset 2..5 — this also drifts
// every hand and would generate noise).
const SEAT_RELATIVE_NOISE_OFFSETS = new Set([0, 2, 3, 4, 5]);

// Noise floor: count how often each absolute addon byte changed across ALL
// adjacent record pairs (not just +1 dc transitions). Bytes that mutate on
// every state-change tick (UI animations, scroll positions, frame counters)
// will score high here and we discount them when ranking discard-array
// candidates. Without this, untagged state-change-only data is too noisy
// to find the offset.
const BACKGROUND_LEN = usable[0].__addon.length;
const backgroundChanges = new Uint32Array(BACKGROUND_LEN);
for (let i = 1; i < usable.length; i++) {
  const A = usable[i - 1].__addon;
  const B = usable[i].__addon;
  if (A.length !== B.length) continue;
  for (let off = 0; off < A.length; off++)
    if (A[off] !== B[off]) backgroundChanges[off]++;
}
const backgroundPairs = pairs;

const perSeat = SEAT_BLOCKS.map(() => ({
  transitions: [],   // [{ oldCount, deltaOffsets: number[], newByteAt: number[] }]
  offsetHits: new Map(),
}));

// Background noise threshold: any byte that mutated in ≥ 35% of all adjacent
// pairs is considered animation/UI noise and excluded from the discard-array
// search. The threshold is empirical — UI animations across 8000+ snapshots
// produce ~50%+ change rates, score/wall fields ~5-10%, true discard slots
// ~0.3% (one transition per ~300 snapshots).
const NOISE_RATE_THRESHOLD = 0.35;
const isNoisy = (off) =>
  backgroundPairs > 0 && backgroundChanges[off] / backgroundPairs >= NOISE_RATE_THRESHOLD;

for (const tr of transitions) {
  const seat = SEAT_BLOCKS[tr.seatIndex];
  const A = tr.prev.__addon;
  const B = tr.next.__addon;
  const deltaOffsets = [];
  const blockEnd = Math.min(seat.countByte + SEAT_BLOCK_SIZE, A.length, B.length);
  for (let off = seat.countByte; off < blockEnd; off++) {
    const rel = off - seat.countByte;
    if (SEAT_RELATIVE_NOISE_OFFSETS.has(rel)) continue;
    if (isNoisy(off)) continue;
    if (A[off] !== B[off]) deltaOffsets.push(off);
  }
  perSeat[tr.seatIndex].transitions.push({
    oldCount: tr.oldCount,
    newCount: tr.newCount,
    tight: tr.tight,
    deltaOffsets,
  });
  for (const off of deltaOffsets) {
    const bucket = perSeat[tr.seatIndex].offsetHits;
    bucket.set(off, (bucket.get(off) ?? 0) + 1);
  }
}

// For each seat with ≥3 transitions, fit a (start, stride) line. The new-tile
// slot is the byte position that changes in the i-th transition specifically
// (not every transition). So we pick, per transition, the byte offset closest
// to the predicted slot for that count. The trick: we don't know stride yet,
// but a small number of candidate strides (1, 2, 4) covers all plausible
// encodings.
function fitStrideAndStart(transitionsForSeat, seatCountByte) {
  if (transitionsForSeat.length === 0) return [];
  // Aggregate every distinct deltaOffset seen across ALL transitions as a
  // possible "start" candidate (not just the smallest-oldCount one — the
  // first +1 in our data may have empty Δ if it lands at a hand-roll
  // boundary). This is small in practice because the noise filter has
  // already discarded UI animation bytes.
  const startCandidates = new Set();
  for (const tr of transitionsForSeat)
    for (const off of tr.deltaOffsets) startCandidates.add(off);
  if (startCandidates.size === 0) return [];

  const candidates = [];
  // The "start" we infer is the slot for tile index 0 — but a transition
  // with oldCount=N writes at start + N*stride, so the start must satisfy
  // (off - start) === oldCount * stride for some transition. We work
  // backwards from each candidate startAbs and stride.
  for (const stride of [1, 2, 4]) {
    for (const startAbs of startCandidates) {
      // Reject candidates that would walk outside the seat block on the
      // largest observed oldCount.
      const maxOldCount = Math.max(...transitionsForSeat.map((t) => t.oldCount));
      if (startAbs + maxOldCount * stride + stride > seatCountByte + SEAT_BLOCK_SIZE) continue;

      let hits = 0;
      let strictHits = 0;
      let tightStrictHits = 0;
      let tightCount = 0;
      for (const tr of transitionsForSeat) {
        const slotStart = startAbs + tr.oldCount * stride;
        const slotEnd = slotStart + stride;
        let inWindow = 0;
        for (const off of tr.deltaOffsets) {
          if (off >= slotStart && off < slotEnd) inWindow++;
        }
        if (inWindow > 0) hits++;
        if (inWindow === stride) strictHits++;
        if (tr.tight) {
          tightCount++;
          if (inWindow === stride) tightStrictHits++;
        }
      }
      candidates.push({
        startAbs,
        stride,
        hits,
        strictHits,
        tightStrictHits,
        tightCount,
        n: transitionsForSeat.length,
        hitRate: hits / transitionsForSeat.length,
        strictRate: strictHits / transitionsForSeat.length,
        tightStrictRate: tightCount > 0 ? tightStrictHits / tightCount : null,
      });
    }
  }
  return candidates.sort((a, b) => {
    // Prefer tight bracket strict-match when we have those samples; fall
    // back to overall strict, then hit rate, then sample size, then
    // smaller stride (Occam's razor).
    const aT = a.tightStrictRate ?? -1;
    const bT = b.tightStrictRate ?? -1;
    if (aT !== bT) return bT - aT;
    if (a.strictRate !== b.strictRate) return b.strictRate - a.strictRate;
    if (a.hitRate !== b.hitRate) return b.hitRate - a.hitRate;
    if (a.n !== b.n) return b.n - a.n;
    return a.stride - b.stride;
  });
}

// Decode candidate value at (offset, stride) from a buffer; tries both raw
// byte (stride=1) and texture-relative int32 (stride=4) interpretations.
function decodeTile(buf, offset, stride) {
  if (stride === 1) {
    const v = buf[offset];
    return { raw: v, asTileId: v < 34 ? v : null, asTextureRelative: null };
  }
  if (stride === 4) {
    if (offset + 4 > buf.length) return null;
    const v = buf.readInt32LE(offset);
    const rel = v - TILE_TEXTURE_BASE;
    return { raw: v, asTileId: null, asTextureRelative: rel >= 0 && rel < 256 ? rel : null };
  }
  if (stride === 2) {
    const v = buf.readUInt16LE(offset);
    return { raw: v, asTileId: v < 34 ? v : null, asTextureRelative: null };
  }
  return null;
}

const seatNames = SEAT_BLOCKS.map((s) => s.name);
const verdicts = [];
for (let s = 0; s < 4; s++) {
  const seat = SEAT_BLOCKS[s];
  const trs = perSeat[s].transitions;
  if (trs.length < 2) {
    verdicts.push({ seat: seatNames[s], note: `only ${trs.length} transitions — skipped` });
    continue;
  }
  const candidates = fitStrideAndStart(trs, seat.countByte);
  const top = candidates.slice(0, 5);
  // Decode the i-th tile under the top candidate to surface a sanity-check
  // sample. We use the latest record we have for that seat.
  const latest = trs[trs.length - 1].oldCount + 1; // dc after the last transition
  const lastB = trs[trs.length - 1];
  const verdict = {
    seat: seatNames[s],
    transitions: trs.length,
    top,
    sampleDecodes: [],
    seatBlockBase: seat.countByte,
  };
  if (top.length > 0) {
    const best = top[0];
    // Decode every slot from 0..latest under best (start, stride)
    // pulled from the LAST transition's "next" record (most data).
    const buf = trs[trs.length - 1].deltaOffsets.length > 0
      ? null  // placeholder — we need access to the actual buf
      : null;
  }
  verdicts.push(verdict);
}

// Sample decode: take the most recent record overall and decode its full
// per-seat discard list under each seat's top candidate.
const last = usable[usable.length - 1];
const lastBuf = last.__addon;

const fmt = (n, w = 4) => "0x" + n.toString(16).padStart(w, "0");
const md = [];
md.push(`# Discard-Array Offset Discovery — empirical fit`);
md.push(``);
md.push(`Generated ${new Date().toISOString()} from \`${memdumpDir}\``);
md.push(``);
md.push(`- Records loaded: ${records.length}`);
md.push(`- Usable (addon ≥ 0x107E, has seq): ${usable.length}`);
md.push(`- Adjacent pairs scanned: ${pairs}`);
md.push(`- Clean single-seat +1 transitions: ${transitions.length}`);
md.push(`- ...of which tight (input-pre → input-post) brackets: ${transitions.filter((t) => t.tight).length}`);
md.push(`- Background noise threshold: ${(NOISE_RATE_THRESHOLD * 100).toFixed(0)}% (bytes mutating in ≥ that fraction of adjacent pairs are excluded from search)`);
md.push(``);
md.push(`## Per-seat fit`);
md.push(``);
for (let s = 0; s < 4; s++) {
  const v = verdicts[s];
  const seat = SEAT_BLOCKS[s];
  md.push(`### ${v.seat}  (block @ ${fmt(seat.countByte)})`);
  if (v.note) {
    md.push(`- ${v.note}`);
    md.push(``);
    continue;
  }
  md.push(`- transitions observed: ${v.transitions}`);
  if (v.top.length === 0) {
    md.push(`- no candidate (start,stride) fit the data — see raw transitions below.`);
  } else {
    md.push(``);
    md.push(`| rank | start | stride | strict% | loose% | tight strict% | n (tight n) |`);
    md.push(`|---|---|---|---|---|---|---|`);
    for (let i = 0; i < v.top.length; i++) {
      const c = v.top[i];
      const tightStrict = c.tightStrictRate === null ? "—" : (c.tightStrictRate * 100).toFixed(0) + "%";
      md.push(`| ${i + 1} | \`${fmt(c.startAbs)}\` | ${c.stride} | ${(c.strictRate * 100).toFixed(0)}% | ${(c.hitRate * 100).toFixed(0)}% | ${tightStrict} | ${c.n} (${c.tightCount}) |`);
    }
    md.push(``);
    // Show top candidate's slot-by-slot decode from the most recent record.
    const best = v.top[0];
    const dcNow = lastBuf[seat.countByte];
    md.push(`Sample decode under best fit (start=\`${fmt(best.startAbs)}\`, stride=${best.stride}, dc=${dcNow}):`);
    md.push("```");
    if (dcNow > 0 && dcNow <= 30) {
      for (let i = 0; i < dcNow; i++) {
        const off = best.startAbs + i * best.stride;
        const dec = decodeTile(lastBuf, off, best.stride);
        if (!dec) continue;
        const tileId = dec.asTileId ?? dec.asTextureRelative;
        md.push(`  i=${i.toString().padStart(2)}  off=${fmt(off)}  raw=${dec.raw}  tile_id=${tileId ?? "?"}`);
      }
    } else {
      md.push(`  dc=${dcNow} — no decode (out of range)`);
    }
    md.push("```");
  }
  md.push(``);
  md.push(`Raw +1 transitions for this seat (oldCount → newCount, changed offsets):`);
  md.push("```");
  for (const tr of perSeat[s].transitions.slice(0, 12)) {
    md.push(`  ${tr.oldCount} → ${tr.newCount}  Δ=[${tr.deltaOffsets.slice(0, 16).map((o) => fmt(o)).join(",")}${tr.deltaOffsets.length > 16 ? `,...+${tr.deltaOffsets.length - 16}` : ""}]`);
  }
  if (perSeat[s].transitions.length > 12) md.push(`  ... +${perSeat[s].transitions.length - 12} more`);
  md.push("```");
  md.push(``);
}

// Cross-check with inputs if provided. A "self discard" appears in the
// inputs stream as values=[15, tileTextureBase + tile_id]. We can use these
// as ground truth for self-seat decodes only.
let inputsCheck = null;
if (inputsDir) {
  try {
    const inputs = await loadAllMemdumps(inputsDir);
    const tileClicks = inputs
      .filter((r) => Array.isArray(r.values) && r.values.length === 2 && r.values[0] === 15
        && typeof r.values[1] === "number" && r.values[1] >= TILE_TEXTURE_BASE
        && r.values[1] < TILE_TEXTURE_BASE + 256)
      .map((r) => ({ t: r.t, tileId: r.values[1] - TILE_TEXTURE_BASE }));
    inputsCheck = {
      totalClicks: tileClicks.length,
      sample: tileClicks.slice(0, 20),
    };
    md.push(`## Cross-check vs. inputs stream`);
    md.push(``);
    md.push(`- Tile-click events parsed (\`[15, tileTextureBase+id]\`): ${inputsCheck.totalClicks}`);
    if (inputsCheck.sample.length > 0) {
      md.push(``);
      md.push(`Sample (first 20):`);
      md.push("```");
      for (const c of inputsCheck.sample) md.push(`  ${c.t}  tile_id=${c.tileId}`);
      md.push("```");
    }
    md.push(``);
    md.push(`To verify: decode the self-seat slot for each click's adjacent post-input dump`);
    md.push(`and confirm tile_id matches. (Manual cross-walk for now; can automate once`);
    md.push(`time-aligned.)`);
    md.push(``);
  } catch (e) {
    md.push(`## inputs cross-check failed: ${e.message}`);
  }
}

// Suggested layouts patch — emit only when self has a confident fit.
md.push(`## Suggested \`data/layouts/emj.json\` patch`);
md.push(``);
const haveAllSeats = verdicts.every((v) => v.top && v.top.length > 0 && v.top[0].strictRate >= 0.7);
if (!haveAllSeats) {
  md.push(`> Insufficient confidence on at least one seat (need strict% ≥ 70 across all four).`);
  md.push(`> Capture more transitions and re-run.`);
} else {
  const stride = verdicts[0].top[0].stride;
  const allSameStride = verdicts.every((v) => v.top[0].stride === stride);
  md.push("```jsonc");
  md.push(`{`);
  md.push(`  // ... existing fields ...`);
  md.push(`  "offsets": {`);
  md.push(`    // ... existing offsets ...`);
  for (let s = 0; s < 4; s++) {
    const v = verdicts[s];
    const fieldName = `${v.seat}DiscardArrayStart`;
    md.push(`    "${fieldName}": "${fmt(v.top[0].startAbs)}",`);
  }
  md.push(`  },`);
  md.push(`  "discardEncoding": {`);
  md.push(`    "tileStride": ${stride},`);
  md.push(`    "textureOffsetEncoded": ${stride === 4}`);
  md.push(`  }${allSameStride ? "" : "  // STRIDES DIFFER — investigate"}`);
  md.push(`}`);
  md.push("```");
}
md.push(``);
md.push(`## Raw verdict JSON`);
md.push(``);
md.push("```json");
md.push(JSON.stringify(verdicts, null, 2));
md.push("```");

const outMd = join(memdumpDir, "_discard-offset-fit.md");
const outJson = join(memdumpDir, "_discard-offset-fit.json");
await writeFile(outMd, md.join("\n"), "utf8");
await writeFile(outJson, JSON.stringify({
  memdumpDir,
  records: records.length,
  usable: usable.length,
  pairs,
  cleanTransitions: transitions.length,
  verdicts,
  inputsCheck,
}, null, 2), "utf8");

console.log(`[find-discard-offset] wrote ${outMd}`);
console.log(`[find-discard-offset] wrote ${outJson}`);
console.log("");
console.log("=== Summary ===");
for (let s = 0; s < 4; s++) {
  const v = verdicts[s];
  if (v.note) { console.log(`  ${v.seat.padEnd(9)}  ${v.note}`); continue; }
  if (v.top.length === 0) { console.log(`  ${v.seat.padEnd(9)}  no fit (n=${v.transitions})`); continue; }
  const c = v.top[0];
  console.log(`  ${v.seat.padEnd(9)}  start=${fmt(c.startAbs)}  stride=${c.stride}  strict=${(c.strictRate * 100).toFixed(0)}%  loose=${(c.hitRate * 100).toFixed(0)}%  n=${c.n}`);
}
