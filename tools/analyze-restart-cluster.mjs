// analyze-restart-cluster.mjs
//
// Diagnose the 33→34→35→36→37→38 hand-restart cluster on install
// 6a4a0a70-c7ca-4b8d-a6b6-8de497111383 (2026-05-11). Six "new hands" opened
// in 30s; per game-file dumps the scores, dora, and closed hand never change,
// so each "new hand" is a false-positive from GameLogger.MaybeRollHand.
//
// MaybeRollHand fires when `snap.WallRemaining > lastWall + 5`. Wall is
// derived in BaseEmjVariant.ResolveWallRemaining:
//   - if stateCode == PostDrawIdle (5) and atkValues[1] is plausible → use it
//   - else fallback to (70 - sum(per-seat discardCounts))
//
// Hypothesis: between two adjacent snapshots taken at very close timestamps
// (a single "tick" of the addon callback), the four per-seat discard-count
// bytes (0x04FE / 0x07DE / 0x0ABE / 0x0D9E) flip from a populated late-game
// state back to the prior-hand state for a frame (or vice-versa), making the
// fallback wall jump +N tiles. This is consistent with an addon detach +
// reattach across hands where the discard piles haven't yet been zeroed by
// the game, OR a torn read where some seats see a fresh value and others
// the stale one.
//
// We pull the few memdumps that bracket 00:45:00–00:46:30 UTC, decode the
// addon, compute both wall-from-atk (if we can find it) and wall-from-
// discard-counts, and report what specifically moves between adjacent dumps
// at the moment GameLogger would trip.

import { readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { gunzipSync } from "node:zlib";

const installDir = process.argv[2] ?? "C:/Users/xelda/Desktop/ffxiv-MahjongAI/.local/by-date/2026-05-11/memdumps/6a4a0a70-c7ca-4b8d-a6b6-8de497111383";
const outDir     = process.argv[3] ?? installDir;
// Timeline raw-output goes to a separate file so the human-written
// RESTART-CLUSTER-ANALYSIS.md sitting alongside is preserved on rerun.
const reportPath = join(outDir, "RESTART-CLUSTER-TIMELINE.md");

// Cluster window. Records with t in [start, end) are considered "in cluster".
const WINDOW_START = "2026-05-11T00:45:00.000Z";
const WINDOW_END   = "2026-05-11T00:46:30.000Z";

// Layout (matches data/layouts/emj.json).
const OFF = {
  selfScore:    0x0500,
  shimochaScore:0x07E0,
  toimenScore:  0x0AC0,
  kamichaScore: 0x0DA0,
  selfDc:       0x04FE,
  shimochaDc:   0x07DE,
  toimenDc:     0x0ABE,
  kamichaDc:    0x0D9E,
  handStart:    0x0DB8,
  doraIndicator:0x0FD8,
};
const WALL_INITIAL = 70;

async function readNdjson(path) {
  const raw = await readFile(path);
  const body = (raw[0] === 0x1f && raw[1] === 0x8b) ? gunzipSync(raw) : raw;
  return body.toString("utf8")
    .split("\n").filter(Boolean)
    .map((l) => { try { return JSON.parse(l); } catch { return null; } })
    .filter(Boolean);
}

async function loadAllMemdumps(dir) {
  const entries = await readdir(dir, { withFileTypes: true });
  const all = [];
  for (const e of entries) {
    if (!e.isFile() || !e.name.endsWith(".gz")) continue;
    for (const r of await readNdjson(join(dir, e.name))) {
      r.__src = e.name;
      all.push(r);
    }
  }
  return all;
}

console.log(`[analyze-restart-cluster] scanning ${installDir}`);
const records = await loadAllMemdumps(installDir);
console.log(`[analyze-restart-cluster] ${records.length} records loaded`);

// AtkValue is 16 bytes: { Type:byte (offset 0), pad[7], Int:int32 (offset 8), pad[4] }
// (FFXIVClientStructs lays it out as struct {Type, ...union{Int, ...}})
// Reference: any captured atk_b64 with atk_count entries.
const ATK_STRIDE = 16;
const ATK_TYPE_OFF = 0;
const ATK_INT_OFF = 8;
const ATK_TYPE_INT = 3;     // FFXIVClientStructs AtkValueType.Int

function readAtkInt(atkBuf, idx) {
  const off = idx * ATK_STRIDE;
  if (atkBuf == null || off + ATK_STRIDE > atkBuf.length) return null;
  const type = atkBuf[off + ATK_TYPE_OFF];
  if (type !== ATK_TYPE_INT) return { type, int: null };
  return { type, int: atkBuf.readInt32LE(off + ATK_INT_OFF) };
}

// Plugin-side state codes for EmjL (same as Emj).
const POST_DRAW_IDLE = 5;

// Decode addon blob and parse the fields we care about.
function parseRec(r) {
  if (typeof r.addon_b64 !== "string") return null;
  let buf;
  try { buf = Buffer.from(r.addon_b64, "base64"); } catch { return null; }
  if (buf.length < 0x107E) return null;

  const dc = [
    buf[OFF.selfDc],
    buf[OFF.shimochaDc],
    buf[OFF.toimenDc],
    buf[OFF.kamichaDc],
  ];
  const sumDc = dc.reduce((a, b) => a + b, 0);
  const wallDerived = WALL_INITIAL - sumDc;

  const scores = [
    buf.readInt32LE(OFF.selfScore),
    buf.readInt32LE(OFF.shimochaScore),
    buf.readInt32LE(OFF.toimenScore),
    buf.readInt32LE(OFF.kamichaScore),
  ];

  // Sample the hand-array first slot (raw int32) — if zero, hand is empty.
  const handFirst = buf.readInt32LE(OFF.handStart);

  // Parse atk_b64 to recover stateCode (atk[0]) and wallCount (atk[1]).
  // ResolveWallRemaining uses atk[1] when stateCode == PostDrawIdle (5),
  // otherwise falls back to 70 - sum(dc). This is the exact decision our
  // plugin makes online, so we reproduce it here.
  let stateCode = null;
  let atkWall = null;
  let atkBuf = null;
  if (typeof r.atk_b64 === "string") {
    try { atkBuf = Buffer.from(r.atk_b64, "base64"); } catch {}
  }
  if (atkBuf) {
    const a0 = readAtkInt(atkBuf, 0);
    const a1 = readAtkInt(atkBuf, 1);
    stateCode = a0?.int ?? null;
    atkWall = a1?.int ?? null;
  }
  // Reproduce ResolveWallRemaining (BaseEmjVariant.cs:242-262).
  let wallResolved;
  if (stateCode === POST_DRAW_IDLE && atkWall != null && atkWall > 0 && atkWall <= 136) {
    wallResolved = atkWall;
  } else {
    wallResolved = wallDerived >= 0 && wallDerived <= WALL_INITIAL ? wallDerived : WALL_INITIAL;
  }

  return {
    t: r.t,
    seq: r.seq,
    reason: r.reason,
    addonAddr: r.addon_addr,
    atkAddr: r.atk_addr,
    atkCount: r.atk_count,
    addonLen: buf.length,
    dc,
    sumDc,
    wallDerived,
    stateCode,
    atkWall,
    wallResolved,
    scores,
    handFirst,
    src: r.__src,
    buf,
  };
}

const parsed = records.map(parseRec).filter(Boolean);
parsed.sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0));
console.log(`[analyze-restart-cluster] ${parsed.length} parseable records`);

// Window filter.
const inCluster = parsed.filter((r) => r.t >= WINDOW_START && r.t < WINDOW_END);
console.log(`[analyze-restart-cluster] ${inCluster.length} records in cluster window`);

// Print the per-record timeline.
const fmtHex = (n, w = 2) => "0x" + (n >>> 0).toString(16).padStart(w, "0").toUpperCase();
const lines = [];
function emit(s) { lines.push(s); console.log(s); }

emit("");
emit("=== Restart-cluster timeline (00:45:00–00:46:30 UTC) ===");
emit("");
emit("Columns: time | seq | reason | addonAddr | atkCount | state(atk0) | atkWall(atk1) | dc[s,sh,to,ka] sumDc | wallDerived | wallResolved | src");
emit("");

let prev = null;
const jumps = [];   // records where wallResolved jumped +6+ vs prev (plugin's actual decision)
for (const r of inCluster) {
  const tShort = r.t.replace("2026-05-11T", "").replace("Z", "");
  const dcStr = r.dc.join(",");
  const jumpFlag = prev && (r.wallResolved - prev.wallResolved) > 5
    ? `  *** WALL-RESOLVED JUMP +${r.wallResolved - prev.wallResolved}  (would trip MaybeRollHand)` : "";
  emit(`  ${tShort}  seq=${r.seq}  ${(r.reason ?? "?").padEnd(13)}  addon=${fmtHex(r.addonAddr ?? 0, 12)}  atkN=${r.atkCount}  state=${r.stateCode ?? "?"}  atkWall=${r.atkWall ?? "?"}  dc=[${dcStr}] sum=${r.sumDc}  wDer=${r.wallDerived}  wRes=${r.wallResolved}  ${r.src}${jumpFlag}`);
  if (prev && (r.wallResolved - prev.wallResolved) > 5) {
    jumps.push({ prev, curr: r });
  }
  prev = r;
}
emit("");
emit(`=== ${jumps.length} +5-or-more wall jumps in this window ===`);

// For each detected jump, byte-diff the two addon blobs to see EXACTLY what moved.
emit("");
emit("=== Per-jump diff analysis ===");
for (const { prev, curr } of jumps) {
  emit("");
  emit(`-- jump  seq ${prev.seq} → ${curr.seq}  (Δt=${(new Date(curr.t) - new Date(prev.t))}ms)`);
  emit(`         prev: state=${prev.stateCode}  atkWall=${prev.atkWall}  dc=[${prev.dc.join(",")}] sum=${prev.sumDc}  wDer=${prev.wallDerived}  wRes=${prev.wallResolved}  addon=${fmtHex(prev.addonAddr ?? 0, 12)}  reason=${prev.reason}`);
  emit(`         curr: state=${curr.stateCode}  atkWall=${curr.atkWall}  dc=[${curr.dc.join(",")}] sum=${curr.sumDc}  wDer=${curr.wallDerived}  wRes=${curr.wallResolved}  addon=${fmtHex(curr.addonAddr ?? 0, 12)}  reason=${curr.reason}`);
  emit(`         addon-addr changed?  ${prev.addonAddr !== curr.addonAddr ? "YES → detach/reattach" : "no — same addon ptr"}`);
  emit(`         scores unchanged?    ${JSON.stringify(prev.scores) === JSON.stringify(curr.scores) ? "YES — same hand" : "NO — hand changed"}`);
  emit(`         hand[0] tile_id:     prev=${prev.handFirst}  curr=${curr.handFirst}`);

  if (prev.buf.length === curr.buf.length) {
    const A = prev.buf;
    const B = curr.buf;
    // Per-byte diff counts inside each seat block (0x2E0 wide).
    const seats = [
      { name: "self",     base: OFF.selfDc },
      { name: "shimocha", base: OFF.shimochaDc },
      { name: "toimen",   base: OFF.toimenDc },
      { name: "kamicha",  base: OFF.kamichaDc },
    ];
    const blockSize = 0x2E0;
    emit(`         per-seat block byte-diff counts (excludes score field bytes [+2..+5]):`);
    for (const s of seats) {
      let changed = 0;
      const sampleChanges = [];
      for (let off = s.base; off < Math.min(s.base + blockSize, A.length); off++) {
        const rel = off - s.base;
        if (rel >= 2 && rel <= 5) continue; // score noise
        if (A[off] !== B[off]) {
          changed++;
          if (sampleChanges.length < 6) sampleChanges.push(`${fmtHex(off, 4)}: ${A[off]}→${B[off]}`);
        }
      }
      emit(`           ${s.name.padEnd(9)}  ${changed} byte(s) changed.  e.g. [${sampleChanges.join(", ")}]`);
    }

    // Total addon byte differences.
    let totalDiff = 0;
    for (let i = 0; i < A.length; i++) if (A[i] !== B[i]) totalDiff++;
    emit(`         total addon bytes that differ: ${totalDiff} / ${A.length}`);
  } else {
    emit(`         addon-size changed (${prev.buf.length} → ${curr.buf.length}) — skipping byte diff`);
  }
}

// Heuristic verdict.
emit("");
emit("=== Verdict ===");
const allSameScores  = jumps.every((j) => JSON.stringify(j.prev.scores) === JSON.stringify(j.curr.scores));
const allSameAddon   = jumps.every((j) => j.prev.addonAddr === j.curr.addonAddr);
const dcMonotonic    = jumps.every((j) => j.curr.sumDc >= j.prev.sumDc);
const usedAtkPath    = jumps.every((j) => j.curr.stateCode === POST_DRAW_IDLE);
const atkDivergesFromDc = jumps.filter((j) => j.curr.atkWall != null && Math.abs(j.curr.atkWall - j.curr.wallDerived) >= 3).length;

emit(`  scores identical across every jump?           ${allSameScores}`);
emit(`  same addon address across jumps?              ${allSameAddon}`);
emit(`  discard-count sum monotonic across jumps?     ${dcMonotonic}`);
emit(`  every jump occurred with state=PostDrawIdle?  ${usedAtkPath}`);
emit(`  jumps where |atkWall - (70-sumDc)| >= 3:      ${atkDivergesFromDc} / ${jumps.length}`);

// Look specifically at "first 5 jumps" — the cluster-defining pattern.
const clusterJumps = jumps.slice(0, 5);
const allClusterPrevNotPDI = clusterJumps.every((j) => j.prev.stateCode !== POST_DRAW_IDLE);
const allClusterCurrPDI    = clusterJumps.every((j) => j.curr.stateCode === POST_DRAW_IDLE);
const allClusterDcSame     = clusterJumps.every((j) => JSON.stringify(j.prev.dc) === JSON.stringify(j.curr.dc));
const allClusterByteDiffZero = clusterJumps.every((j) => {
  if (j.prev.buf.length !== j.curr.buf.length) return false;
  for (let i = 0; i < j.prev.buf.length; i++) if (j.prev.buf[i] !== j.curr.buf[i]) return false;
  return true;
});
emit(`  cluster jumps (first 5): prev.state ≠ 5 every time?     ${allClusterPrevNotPDI}`);
emit(`  cluster jumps (first 5): curr.state == 5 every time?    ${allClusterCurrPDI}`);
emit(`  cluster jumps (first 5): dc identical prev vs curr?     ${allClusterDcSame}`);
emit(`  cluster jumps (first 5): addon BYTES identical prev/curr? ${allClusterByteDiffZero}`);

let verdict;
if (jumps.length === 0) {
  verdict = `No wallResolved jumps detected in the memdump record stream itself.`;
} else if (allClusterPrevNotPDI && allClusterCurrPDI && allClusterDcSame && allClusterByteDiffZero) {
  verdict =
`The addon ADDON BLOB IS BYTE-IDENTICAL across each "jump" — only atkValues changes.
Specifically: prev snapshot has stateCode != 5 (typically 15 = CallPrompt, or 9/12),
where ResolveWallRemaining falls through to the dc fallback (70 - sumDc).
curr snapshot has stateCode == 5 (PostDrawIdle), where ResolveWallRemaining
trusts atkValues[1] verbatim. atkValues[1] is reporting a wall count that is
~6 higher than the dc-derived value (38 vs 32, 37 vs 31, 36 vs 30, ...).

The "+6 offset" is consistent across all five cluster jumps and decreases by 1
each turn in lockstep with both atkWall and wallDerived. This is NOT a torn
read or addon detach — both numbers are valid, they just use different
accounting (atkValues[1] looks like wall_remaining as the game itself counts
it, which excludes some of what our 'sum of all four seats' discardCounts
treats as consumed).

The bug is in ResolveWallRemaining + MaybeRollHand:
ResolveWallRemaining switches accounting modes based on stateCode every time
the call-prompt modal closes. Each such close produces a "+6 wall jump" that
GameLogger's "+5 tolerance" cannot absorb, and a fresh hand file is rolled.`;
} else if (!allSameAddon) {
  verdict = `Addon address changed between jumps — addon detach/reattach.`;
} else {
  verdict = `Mixed pattern. See per-jump diffs above.`;
}
emit(``);
emit(`  → ${verdict}`);

emit("");
emit("=== Recommended fix ===");
emit(`  PRIMARY FIX (one-liner): in BaseEmjVariant.ResolveWallRemaining, do NOT switch`);
emit(`  between atkValues[1] and (70 - sumDc) based on stateCode. Pick ONE source of`);
emit(`  truth and stick with it. The dc-derived value (70 - sumDc) was monotonic and`);
emit(`  correct in EVERY cluster snapshot; atkValues[1] is the misbehaving signal.`);
emit(``);
emit(`     -if (stateCode == PostDrawIdle && atkValues[1].Int in (0, 136]) return atkValues[1].Int;`);
emit(`     -return 70 - sum(discardCounts);`);
emit(`     +return 70 - sum(discardCounts);  // dc-derived is monotonic; atkValues[1]`);
emit(`     +                                 // uses a different accounting and causes`);
emit(`     +                                 // +6 wall jumps when modals close.`);
emit(``);
emit(`  DEFENSE IN DEPTH (in GameLogger.MaybeRollHand):`);
emit(`  1. Require closed-hand emptiness as a co-signal: a real new deal goes through a`);
emit(`     few frames where hand is empty (the dealing animation). Refuse to roll unless`);
emit(`     hand.Count is in {0, 13, 14} (i.e. fresh deal or just-dealt). The cluster's`);
emit(`     "false-positive" frames all had hand=[1,3,5,6,6,8] — 6 tiles after a pon,`);
emit(`     which would have been rejected.`);
emit(`  2. Add a minimum-hand-duration guard: refuse to roll a new hand within 30s of`);
emit(`     the prior hand-start. (Cluster fired 6 hands in 30s — structurally impossible.)`);
emit(`  3. Require N=2 consecutive snapshots showing wall > lastWall + 5 before rolling.`);
emit(`     Single-tick state=5 frames between modal closes will never persist for two`);
emit(`     consecutive snapshots since the next observation flips back to state=15.`);

await writeFile(reportPath, "# Restart Cluster — Raw Timeline + Diff Output\n\n" +
  `Generated ${new Date().toISOString()} from \`${installDir}\`\n\n` +
  "Window: 2026-05-11T00:45:00Z – 2026-05-11T00:46:30Z (the 33→34→35→36→37→38 cluster).\n\n" +
  "See `RESTART-CLUSTER-ANALYSIS.md` (sibling file) for the prose write-up.\n\n" +
  "```\n" + lines.join("\n") + "\n```\n", "utf8");
console.log(`[analyze-restart-cluster] wrote ${reportPath}`);
