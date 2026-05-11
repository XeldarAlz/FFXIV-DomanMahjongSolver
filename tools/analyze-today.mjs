// One-shot summary of today's downloaded telemetry. Reads .local/by-date/<date>/
// and prints aggregate counts, error/failure flags, and per-install gameplay
// shape. Not committed to a regular pipeline — strictly for ad-hoc inspection.

import { createReadStream } from "node:fs";
import { readdir } from "node:fs/promises";
import { join } from "node:path";
import { createGunzip } from "node:zlib";
import readline from "node:readline";

const ROOT = process.argv[2] ?? ".local/by-date/2026-05-11";

async function* readGzLines(path) {
  const gz = createReadStream(path).pipe(createGunzip());
  const rl = readline.createInterface({ input: gz, crlfDelay: Infinity });
  for await (const line of rl) if (line.trim()) yield line;
}

async function listInstalls(stream) {
  try { return await readdir(join(ROOT, stream)); } catch { return []; }
}
async function listFiles(stream, install) {
  try { return await readdir(join(ROOT, stream, install)); } catch { return []; }
}

// ── sigprobes
const sigprobesByInstall = {};
let sigprobeFailures = 0;
for (const install of await listInstalls("sigprobes")) {
  const probes = {};
  for (const f of await listFiles("sigprobes", install)) {
    for await (const line of readGzLines(join(ROOT, "sigprobes", install, f))) {
      const j = JSON.parse(line);
      probes[j.name] = j.success;
      if (j.success === false) sigprobeFailures++;
    }
  }
  sigprobesByInstall[install] = probes;
}
const probeNames = new Set();
for (const p of Object.values(sigprobesByInstall)) for (const n of Object.keys(p)) probeNames.add(n);
const probeFailCounts = {};
for (const n of probeNames) {
  let fail = 0, ok = 0, miss = 0;
  for (const p of Object.values(sigprobesByInstall)) {
    if (!(n in p)) miss++; else if (p[n]) ok++; else fail++;
  }
  probeFailCounts[n] = { ok, fail, miss };
}

// ── findings
const findingKinds = {};
const findingByInstall = {};
let totalFindingLines = 0;
for (const install of await listInstalls("findings")) {
  findingByInstall[install] = 0;
  for (const f of await listFiles("findings", install)) {
    for await (const line of readGzLines(join(ROOT, "findings", install, f))) {
      const j = JSON.parse(line);
      findingKinds[j.kind] = (findingKinds[j.kind] ?? 0) + 1;
      findingByInstall[install]++;
      totalFindingLines++;
    }
  }
}

// ── games
const gameStats = {};
const eventTypes = {};
let totalGameLines = 0;
const handsPerInstall = {};
let dealtSeats = { 0: 0, 1: 0, 2: 0, 3: 0 };
let suggestedActions = {};
for (const install of await listInstalls("games")) {
  gameStats[install] = { hands: 0, lines: 0, sessions: new Set(), wins: 0, losses: 0 };
  for (const f of await listFiles("games", install)) {
    gameStats[install].hands++;
    let handSeat = null;
    for await (const line of readGzLines(join(ROOT, "games", install, f))) {
      const j = JSON.parse(line);
      gameStats[install].lines++;
      totalGameLines++;
      const ev = j.e ?? "?";
      eventTypes[ev] = (eventTypes[ev] ?? 0) + 1;
      if (ev === "hand-start") {
        handSeat = j.seat;
        dealtSeats[j.seat] = (dealtSeats[j.seat] ?? 0) + 1;
      }
      if (ev === "suggest" && j.action) {
        suggestedActions[j.action] = (suggestedActions[j.action] ?? 0) + 1;
      }
      if (ev === "hand-end") {
        // any payment to our seat?
        if (j.deltas && Array.isArray(j.deltas) && handSeat != null) {
          const d = j.deltas[handSeat];
          if (typeof d === "number") {
            if (d > 0) gameStats[install].wins++;
            else if (d < 0) gameStats[install].losses++;
          }
        }
      }
    }
  }
  handsPerInstall[install] = gameStats[install].hands;
}

// ── inputs
const inputsByInstall = {};
let totalInputLines = 0;
const inputAddons = {};
for (const install of await listInstalls("inputs")) {
  inputsByInstall[install] = 0;
  for (const f of await listFiles("inputs", install)) {
    for await (const line of readGzLines(join(ROOT, "inputs", install, f))) {
      const j = JSON.parse(line);
      inputsByInstall[install]++;
      totalInputLines++;
      const addon = j.addon ?? "?";
      inputAddons[addon] = (inputAddons[addon] ?? 0) + 1;
    }
  }
}

// ── memdumps (count only, don't parse bodies)
const memdumpsByInstall = {};
const memdumpReasons = {};
let totalMemdumpLines = 0;
for (const install of await listInstalls("memdumps")) {
  memdumpsByInstall[install] = 0;
  for (const f of await listFiles("memdumps", install)) {
    for await (const line of readGzLines(join(ROOT, "memdumps", install, f))) {
      // Parse only the small reason field; b64 body is enormous.
      const m = line.match(/"reason":"([^"]+)"/);
      const reason = m ? m[1] : "?";
      memdumpReasons[reason] = (memdumpReasons[reason] ?? 0) + 1;
      memdumpsByInstall[install]++;
      totalMemdumpLines++;
    }
  }
}

// ── output
const out = [];
out.push(`# Telemetry summary — ${ROOT}`);
out.push("");
out.push("## Coverage");
out.push(`- Installs with sigprobes:  ${Object.keys(sigprobesByInstall).length}`);
out.push(`- Installs with findings:   ${Object.keys(findingByInstall).length}`);
out.push(`- Installs with gameplay:   ${Object.keys(gameStats).length}`);
out.push(`- Installs with inputs:     ${Object.keys(inputsByInstall).length}`);
out.push(`- Installs with memdumps:   ${Object.keys(memdumpsByInstall).length}`);
out.push("");
out.push("## Sigprobes");
out.push(`- Total failures across all installs: ${sigprobeFailures}`);
out.push(`- Probe name           ok / fail / missing`);
for (const [n, c] of Object.entries(probeFailCounts)) {
  out.push(`  - ${n.padEnd(28)} ${String(c.ok).padStart(3)} / ${String(c.fail).padStart(2)} / ${String(c.miss).padStart(2)}`);
}
out.push("");
out.push("## Findings (kind histogram)");
for (const [k, c] of Object.entries(findingKinds).sort((a, b) => b[1] - a[1])) {
  out.push(`  - ${k.padEnd(30)} ${c}`);
}
out.push(`Total finding lines: ${totalFindingLines}`);
out.push("");
out.push("## Games — per-install");
out.push(`Hand files | win-ended | loss-ended | event-lines | install`);
for (const [i, s] of Object.entries(gameStats).sort((a, b) => b[1].hands - a[1].hands)) {
  out.push(`  ${String(s.hands).padStart(4)} ${String(s.wins).padStart(7)} ${String(s.losses).padStart(8)} ${String(s.lines).padStart(8)}    ${i}`);
}
out.push("");
out.push("## Game event-type histogram");
for (const [k, c] of Object.entries(eventTypes).sort((a, b) => b[1] - a[1])) {
  out.push(`  - ${k.padEnd(20)} ${c}`);
}
out.push(`Total game lines: ${totalGameLines}`);
out.push("");
out.push("## Our-seat assignment counts (hand-start.seat) — bias check");
for (const [seat, c] of Object.entries(dealtSeats)) out.push(`  - seat ${seat}: ${c}`);
out.push("");
out.push("## Suggested actions histogram");
for (const [k, c] of Object.entries(suggestedActions).sort((a, b) => b[1] - a[1])) {
  out.push(`  - ${k.padEnd(15)} ${c}`);
}
out.push("");
out.push("## Inputs");
out.push(`Total input events: ${totalInputLines}`);
for (const [addon, c] of Object.entries(inputAddons)) out.push(`  - addon ${addon}: ${c}`);
for (const [i, c] of Object.entries(inputsByInstall).sort((a, b) => b[1] - a[1])) {
  out.push(`  - ${i}  ${c}`);
}
out.push("");
out.push("## Memdumps (reason histogram)");
for (const [r, c] of Object.entries(memdumpReasons).sort((a, b) => b[1] - a[1])) {
  out.push(`  - ${r.padEnd(20)} ${c}`);
}
out.push(`Total memdump lines: ${totalMemdumpLines}`);
out.push("");
out.push("## Memdumps per install");
for (const [i, c] of Object.entries(memdumpsByInstall).sort((a, b) => b[1] - a[1])) {
  out.push(`  - ${i}  ${c}`);
}

console.log(out.join("\n"));
