// Pulls B2 objects for a given install/date/stream prefix to local disk.
// Usage:
//   $env:B2_KEY_ID = "..."
//   $env:B2_APPLICATION_KEY = "..."
//   node tools/b2-pull.mjs <stream> <install-id> <yyyy-mm-dd> [out-dir]
//
// Defaults: out-dir = .local/<stream>/<install-id>/<date>/

import { AwsClient } from "../server/node_modules/aws4fetch/dist/aws4fetch.esm.mjs";
import { mkdir, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";

const REGION = "eu-central-003";
const ENDPOINT = "https://s3.eu-central-003.backblazeb2.com";
const BUCKET = "mahjong-telemetry";

const [stream, install, date, outDirArg] = process.argv.slice(2);
if (!stream || !install || !date) {
  console.error("Usage: node tools/b2-pull.mjs <stream> <install-id> <yyyy-mm-dd> [out-dir]");
  process.exit(2);
}
const keyId = process.env.B2_KEY_ID;
const appKey = process.env.B2_APPLICATION_KEY;
if (!keyId || !appKey) {
  console.error("B2_KEY_ID and B2_APPLICATION_KEY must be set");
  process.exit(2);
}

const outDir = outDirArg ?? join(".local", stream, install, date);
await mkdir(outDir, { recursive: true });

const aws = new AwsClient({
  accessKeyId: keyId,
  secretAccessKey: appKey,
  service: "s3",
  region: REGION,
});

const prefix = `${stream}/${install}/${date}/`;
console.log(`[b2-pull] Listing s3://${BUCKET}/${prefix}`);

let totalKeys = 0;
let totalBytes = 0;
let continuationToken = null;
const allKeys = [];

while (true) {
  const params = new URLSearchParams({
    "list-type": "2",
    prefix,
    "max-keys": "1000",
  });
  if (continuationToken) params.set("continuation-token", continuationToken);
  const url = `${ENDPOINT}/${BUCKET}?${params.toString()}`;
  const r = await aws.fetch(url, { method: "GET" });
  if (!r.ok) {
    const txt = await r.text();
    console.error(`LIST failed: ${r.status} ${txt.slice(0, 500)}`);
    process.exit(1);
  }
  const xml = await r.text();
  // B2 returns Contents blocks with ETag/Key/LastModified/Size in any order;
  // parse each block and pick fields by tag name.
  const blockRe = /<Contents>([\s\S]*?)<\/Contents>/g;
  const tag = (block, name) => {
    const m = block.match(new RegExp(`<${name}>([^<]*)</${name}>`));
    return m ? m[1] : null;
  };
  let bm;
  while ((bm = blockRe.exec(xml))) {
    const b = bm[1];
    const key = tag(b, "Key");
    if (!key) continue;
    allKeys.push({
      key,
      lastModified: tag(b, "LastModified") ?? "",
      size: parseInt(tag(b, "Size") ?? "0", 10),
    });
  }
  const truncated = /<IsTruncated>true<\/IsTruncated>/.test(xml);
  if (!truncated) break;
  const tokMatch = xml.match(/<NextContinuationToken>([^<]+)<\/NextContinuationToken>/);
  if (!tokMatch) break;
  continuationToken = tokMatch[1];
}

console.log(`[b2-pull] Found ${allKeys.length} object(s)`);
for (const k of allKeys) {
  totalBytes += k.size;
}
console.log(`[b2-pull] Total bytes (compressed): ${totalBytes} (${(totalBytes / 1024 / 1024).toFixed(2)} MB)`);

// Download
let i = 0;
for (const obj of allKeys) {
  i++;
  const localPath = join(outDir, obj.key.slice(prefix.length));
  await mkdir(dirname(localPath), { recursive: true });
  const url = `${ENDPOINT}/${BUCKET}/${obj.key}`;
  const r = await aws.fetch(url, { method: "GET" });
  if (!r.ok) {
    console.error(`[${i}/${allKeys.length}] GET ${obj.key} -> ${r.status}`);
    continue;
  }
  const buf = Buffer.from(await r.arrayBuffer());
  await writeFile(localPath, buf);
  if (i % 25 === 0 || i === allKeys.length) {
    console.log(`[${i}/${allKeys.length}] saved (${(buf.byteLength / 1024).toFixed(1)} KB)`);
  }
  totalKeys++;
}
console.log(`[b2-pull] Done. Wrote ${totalKeys} file(s) to ${outDir}`);
