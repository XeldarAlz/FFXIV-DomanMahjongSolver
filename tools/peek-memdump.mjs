// Decompress one memdump file and print the first few records' shapes/keys.
import { readFile, readdir } from "node:fs/promises";
import { gunzipSync } from "node:zlib";
import { join } from "node:path";

const dir = process.argv[2];
if (!dir) { console.error("Usage: node tools/peek-memdump.mjs <dir>"); process.exit(2); }

const files = (await readdir(dir)).filter(f => f.endsWith(".gz")).sort();
console.log(`${files.length} file(s) in ${dir}`);
if (files.length === 0) process.exit(0);

const sample = files[0];
const raw = await readFile(join(dir, sample));
// undici auto-decompresses when B2 returns Content-Encoding: gzip; if the
// first byte is the gzip magic 0x1f, decompress, else read as text.
const data = (raw[0] === 0x1f && raw[1] === 0x8b) ? gunzipSync(raw) : raw;
const lines = data.toString("utf8").split("\n").filter(Boolean);
console.log(`\n=== ${sample} ===`);
console.log(`compressed -> uncompressed: ${data.byteLength} bytes, ${lines.length} records`);

const summarizeKeys = (obj, prefix = "") => {
  const out = [];
  for (const [k, v] of Object.entries(obj)) {
    const path = prefix ? `${prefix}.${k}` : k;
    if (v === null) out.push(`${path}: null`);
    else if (Array.isArray(v)) out.push(`${path}[${v.length}]: ${typeof v[0]}`);
    else if (typeof v === "object") out.push(`${path}: object (${Object.keys(v).length} keys)`);
    else if (typeof v === "string") out.push(`${path}: string(${v.length}${v.length > 60 ? `, e.g. ${JSON.stringify(v.slice(0,60))}…` : `, ${JSON.stringify(v)}`})`);
    else out.push(`${path}: ${typeof v}(${v})`);
  }
  return out;
};

for (let i = 0; i < Math.min(3, lines.length); i++) {
  const rec = JSON.parse(lines[i]);
  console.log(`\n-- record ${i} top-level keys (${Object.keys(rec).length}) --`);
  console.log(summarizeKeys(rec).join("\n"));
}

// Distribution: types of records (kind / event / type discriminator)
const counts = {};
for (const line of lines) {
  try {
    const rec = JSON.parse(line);
    const disc = rec.type ?? rec.kind ?? rec.event ?? rec.label ?? "<no-disc>";
    counts[disc] = (counts[disc] ?? 0) + 1;
  } catch (e) { counts["<parse-error>"] = (counts["<parse-error>"] ?? 0) + 1; }
}
console.log("\n=== record type distribution (this file) ===");
for (const [k, n] of Object.entries(counts).sort((a,b) => b[1]-a[1])) {
  console.log(`  ${n}\t${k}`);
}
