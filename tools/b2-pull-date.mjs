// Pull every object whose key path includes a given date segment, across all
// streams and installs. Uses raw undici (no auto-decompression) like
// b2-pull-all.mjs. Reads keys from an existing NDJSON manifest produced by
// tools/b2-list-all.mjs.
//
// Usage:
//   node tools/b2-pull-date.mjs <manifest.ndjson> <yyyy-mm-dd> [out-root]
// Defaults: out-root = .local/by-date/<yyyy-mm-dd>/<stream>/<install>/<filename>

import { AwsClient } from "../server/node_modules/aws4fetch/dist/aws4fetch.esm.mjs";
import { request as undiciRequest } from "../server/node_modules/undici/index.js";
import { mkdir, readFile, stat, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";

const REGION = "eu-central-003";
const ENDPOINT = "https://s3.eu-central-003.backblazeb2.com";
const BUCKET = "mahjong-telemetry";

const [manifestPath, date, outRootArg] = process.argv.slice(2);
if (!manifestPath || !date) {
  console.error("Usage: node tools/b2-pull-date.mjs <manifest.ndjson> <yyyy-mm-dd> [out-root]");
  process.exit(2);
}
const outRoot = outRootArg ?? join(".local", "by-date", date);

const keyId = process.env.B2_KEY_ID;
const appKey = process.env.B2_APPLICATION_KEY;
if (!keyId || !appKey) { console.error("B2_KEY_ID / B2_APPLICATION_KEY must be set"); process.exit(2); }

const aws = new AwsClient({ accessKeyId: keyId, secretAccessKey: appKey, service: "s3", region: REGION });

async function rawFetch(url, init = {}) {
  const signed = await aws.sign(url, init);
  const headers = {};
  for (const [k, v] of signed.headers.entries()) headers[k] = v;
  const res = await undiciRequest(signed.url, { method: signed.method, headers, body: init.body });
  const chunks = [];
  for await (const c of res.body) chunks.push(c);
  return { status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks) };
}

const text = await readFile(manifestPath, "utf8");
const matched = [];
for (const line of text.split(/\r?\n/)) {
  if (!line.trim()) continue;
  let obj;
  try { obj = JSON.parse(line); } catch { continue; }
  if (obj.date !== date) continue;
  matched.push(obj);
}
console.log(`[pull-date] ${matched.length} object(s) match date=${date}`);

let downloaded = 0, skipped = 0, failed = 0;
for (let i = 0; i < matched.length; i++) {
  const { key, size, stream, install, filename } = matched[i];
  const localPath = join(outRoot, stream, install, filename || key.split("/").slice(3).join("/"));
  await mkdir(dirname(localPath), { recursive: true });
  let exists = false;
  try { const s = await stat(localPath); if (s.size === size) exists = true; } catch {}
  if (exists) { skipped++; continue; }
  try {
    const r = await rawFetch(`${ENDPOINT}/${BUCKET}/${key}`);
    if (r.status >= 300) { console.error(`GET ${key} -> ${r.status}`); failed++; continue; }
    await writeFile(localPath, r.body);
    downloaded++;
  } catch (e) {
    console.error(`GET ${key} threw: ${e.message}`);
    failed++;
  }
  if ((downloaded + failed) % 50 === 0) console.log(`[pull-date] ${downloaded} dl, ${skipped} skip, ${failed} fail, ${i+1}/${matched.length}`);
}
console.log(`[pull-date] Done. downloaded=${downloaded} skipped=${skipped} failed=${failed} total=${matched.length}`);
