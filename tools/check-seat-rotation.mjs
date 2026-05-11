// For a single install, sample the first memdump of each hand (matched via
// hand-start timestamps in the games stream) and read the candidate seat
// offsets +0x1248 and +0x12BC. A real seat field rotates 0→1→2→3 across hands
// in normal play.

import { readFile } from "node:fs/promises";
import { readdir } from "node:fs/promises";
import { join } from "node:path";
import { gunzipSync } from "node:zlib";

const root = process.argv[2] ?? ".local/by-date/2026-05-11";
const install = process.argv[3] ?? "6a4a0a70-c7ca-4b8d-a6b6-8de497111383";

function lines(buf) {
  const u = (buf[0] === 0x1f && buf[1] === 0x8b) ? gunzipSync(buf) : buf;
  return u.toString("utf8").split(/\r?\n/).filter(Boolean);
}

// 1. Collect (hand#, t_start) tuples from games/ — first hand-start per file.
const gameFiles = (await readdir(join(root, "games", install))).filter((n) => n.endsWith(".gz")).sort();
const handStarts = [];
for (const f of gameFiles) {
  const m = f.match(/-hand(\d+)\./);
  const handNo = m ? parseInt(m[1], 10) : -1;
  for (const line of lines(await readFile(join(root, "games", install, f)))) {
    const j = JSON.parse(line);
    if (j.e === "hand-start") {
      handStarts.push({ handNo, t: j.t, file: f, lineCount: lines(await readFile(join(root, "games", install, f))).length });
      break;
    }
  }
}
console.log(`[check-seat] ${handStarts.length} hand-starts in ${install}`);

// 2. Stream through memdumps and collect (t, addon_b64) records, then for each
// hand-start pick the first memdump with t >= handStart and decode +0x1248,
// +0x12BC, plus a couple of nearby candidates.
const memFiles = (await readdir(join(root, "memdumps", install))).filter((n) => n.endsWith(".gz")).sort();
const dumps = [];
for (const f of memFiles) {
  for (const line of lines(await readFile(join(root, "memdumps", install, f)))) {
    const j = JSON.parse(line);
    if (typeof j.addon_b64 === "string") dumps.push({ t: j.t, seq: j.seq, reason: j.reason, file: f, j });
  }
}
dumps.sort((a, b) => a.t.localeCompare(b.t));
console.log(`[check-seat] ${dumps.length} memdump records`);

console.log("");
console.log("hand# | lines | t_start                       | +0x1248 | +0x12BC | +0x12C0 | +0x0130 |  dc=[s,sh,t,k]");
for (const hs of handStarts) {
  const d = dumps.find((m) => m.t >= hs.t);
  if (!d) { console.log(`${String(hs.handNo).padStart(4)}  | ${String(hs.lineCount).padStart(5)} | (no dump after t)`); continue; }
  const buf = Buffer.from(d.j.addon_b64, "base64");
  if (buf.length < 0x12C4) continue;
  const s1248 = buf.readInt32LE(0x1248);
  const s12bc = buf.readInt32LE(0x12BC);
  const s12c0 = buf.readInt32LE(0x12C0);
  const s0130 = buf.readInt32LE(0x0130);
  const dc = [buf[0x04FE], buf[0x07DE], buf[0x0ABE], buf[0x0D9E]].join(",");
  console.log(`${String(hs.handNo).padStart(4)}  | ${String(hs.lineCount).padStart(5)} | ${hs.t} | ${String(s1248).padStart(6)}  | ${String(s12bc).padStart(6)}  | ${String(s12c0).padStart(6)}  | ${String(s0130).padStart(6)}  | [${dc}]`);
}
