// Download every object from the mahjong-telemetry bucket into a mirror tree
// WITHOUT auto-decompression. Node's global fetch decompresses Content-Encoding:
// gzip responses, which corrupts the on-disk file (and crashes when one
// object's content-encoding header lies). We sign the request via aws4fetch,
// then dispatch via raw undici to keep bytes verbatim.

import { AwsClient } from "../server/node_modules/aws4fetch/dist/aws4fetch.esm.mjs";
import { request as undiciRequest } from "../server/node_modules/undici/index.js";
import { mkdir, stat, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";

const REGION = "eu-central-003";
const ENDPOINT = "https://s3.eu-central-003.backblazeb2.com";
const BUCKET = "mahjong-telemetry";
const OUT_ROOT = process.argv[2] ?? ".local/b2-all";

const keyId = process.env.B2_KEY_ID;
const appKey = process.env.B2_APPLICATION_KEY;
if (!keyId || !appKey) { console.error("B2_KEY_ID / B2_APPLICATION_KEY must be set"); process.exit(2); }

const aws = new AwsClient({ accessKeyId: keyId, secretAccessKey: appKey, service: "s3", region: REGION });

async function rawFetch(url, init = {}) {
  const signed = await aws.sign(url, init);
  const headers = {};
  for (const [k, v] of signed.headers.entries()) headers[k] = v;
  const res = await undiciRequest(signed.url, {
    method: signed.method,
    headers,
    body: init.body,
  });
  const chunks = [];
  for await (const c of res.body) chunks.push(c);
  return { status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks) };
}

let token = null;
const all = [];
while (true) {
  const params = new URLSearchParams({ "list-type": "2", "max-keys": "1000" });
  if (token) params.set("continuation-token", token);
  const r = await rawFetch(`${ENDPOINT}/${BUCKET}?${params}`);
  if (r.status >= 300) { console.error(`LIST ${r.status}: ${r.body.toString("utf8").slice(0, 300)}`); process.exit(1); }
  const xml = r.body.toString("utf8");
  const blockRe = /<Contents>([\s\S]*?)<\/Contents>/g;
  const tag = (b, n) => { const m = b.match(new RegExp(`<${n}>([^<]*)</${n}>`)); return m ? m[1] : null; };
  let m;
  while ((m = blockRe.exec(xml))) {
    const b = m[1];
    const key = tag(b, "Key"); if (!key) continue;
    all.push({ key, size: parseInt(tag(b, "Size") ?? "0", 10) });
  }
  const truncated = /<IsTruncated>true<\/IsTruncated>/.test(xml);
  if (!truncated) break;
  const tm = xml.match(/<NextContinuationToken>([^<]+)<\/NextContinuationToken>/);
  if (!tm) break;
  token = tm[1];
}
console.log(`[pull-all] ${all.length} object(s)`);

let downloaded = 0, skipped = 0, failed = 0;
for (let i = 0; i < all.length; i++) {
  const { key, size } = all[i];
  const localPath = join(OUT_ROOT, key);
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
  if ((downloaded + failed) % 50 === 0) console.log(`[pull-all] ${downloaded} dl, ${skipped} skip, ${failed} fail, ${i+1}/${all.length}`);
}
console.log(`[pull-all] Done. downloaded=${downloaded} skipped=${skipped} failed=${failed} total=${all.length}`);
