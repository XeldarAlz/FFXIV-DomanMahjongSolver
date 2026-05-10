// Lists every object in the mahjong-telemetry bucket, paginated, and writes
// a flat NDJSON manifest to stdout (or to the file passed as arg 1).
//
// Usage:
//   $env:B2_KEY_ID = "..."; $env:B2_APPLICATION_KEY = "..."
//   node tools/b2-list-all.mjs [out.ndjson]

import { AwsClient } from "../server/node_modules/aws4fetch/dist/aws4fetch.esm.mjs";
import { createWriteStream } from "node:fs";
import { mkdir } from "node:fs/promises";
import { dirname } from "node:path";

const REGION = "eu-central-003";
const ENDPOINT = "https://s3.eu-central-003.backblazeb2.com";
const BUCKET = "mahjong-telemetry";

const outPath = process.argv[2];
const keyId = process.env.B2_KEY_ID;
const appKey = process.env.B2_APPLICATION_KEY;
if (!keyId || !appKey) {
  console.error("B2_KEY_ID / B2_APPLICATION_KEY must be set");
  process.exit(2);
}

const aws = new AwsClient({ accessKeyId: keyId, secretAccessKey: appKey, service: "s3", region: REGION });
let out = process.stdout;
if (outPath) {
  await mkdir(dirname(outPath), { recursive: true });
  out = createWriteStream(outPath);
}

let token = null;
let total = 0;
let totalBytes = 0;
const streamCounts = new Map();
const streamBytes = new Map();
const installs = new Set();

while (true) {
  const params = new URLSearchParams({ "list-type": "2", "max-keys": "1000" });
  if (token) params.set("continuation-token", token);
  const r = await aws.fetch(`${ENDPOINT}/${BUCKET}?${params}`, { method: "GET" });
  if (!r.ok) {
    const t = await r.text();
    console.error(`LIST failed ${r.status}: ${t.slice(0, 500)}`);
    process.exit(1);
  }
  const xml = await r.text();
  const blockRe = /<Contents>([\s\S]*?)<\/Contents>/g;
  const tag = (b, n) => {
    const m = b.match(new RegExp(`<${n}>([^<]*)</${n}>`));
    return m ? m[1] : null;
  };
  let m;
  while ((m = blockRe.exec(xml))) {
    const b = m[1];
    const key = tag(b, "Key");
    if (!key) continue;
    const size = parseInt(tag(b, "Size") ?? "0", 10);
    const lm = tag(b, "LastModified") ?? "";
    const etag = (tag(b, "ETag") ?? "").replace(/"/g, "");
    // key shape: {stream}/{install}/{date}/{filename}
    const parts = key.split("/");
    const stream = parts[0] ?? "";
    const install = parts[1] ?? "";
    const date = parts[2] ?? "";
    const filename = parts.slice(3).join("/");
    out.write(JSON.stringify({ key, stream, install, date, filename, size, last_modified: lm, etag }) + "\n");
    total++;
    totalBytes += size;
    streamCounts.set(stream, (streamCounts.get(stream) ?? 0) + 1);
    streamBytes.set(stream, (streamBytes.get(stream) ?? 0) + size);
    installs.add(install);
  }
  const truncated = /<IsTruncated>true<\/IsTruncated>/.test(xml);
  if (!truncated) break;
  const tm = xml.match(/<NextContinuationToken>([^<]+)<\/NextContinuationToken>/);
  if (!tm) break;
  token = tm[1];
}

if (out !== process.stdout) {
  await new Promise((res) => out.end(res));
}

console.error(`[b2-list-all] objects: ${total}`);
console.error(`[b2-list-all] bytes:   ${totalBytes} (${(totalBytes / 1024 / 1024).toFixed(2)} MB)`);
console.error(`[b2-list-all] installs: ${installs.size}`);
console.error("[b2-list-all] by stream:");
for (const [s, c] of [...streamCounts.entries()].sort((a, b) => b[1] - a[1])) {
  const bytes = streamBytes.get(s) ?? 0;
  console.error(`  ${s.padEnd(10)} ${String(c).padStart(6)}  ${(bytes / 1024 / 1024).toFixed(2)} MB`);
}
