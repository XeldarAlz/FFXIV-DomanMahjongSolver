// Cloudflare Worker — telemetry ingest for the Mahjong plugin.
//
// Validates incoming uploads, enforces per-install rate limits, and writes
// the gzipped payload to R2 keyed as {stream}/{install_id}/{date}/{filename}.gz.
//
// Deployment:
//   wrangler deploy
//   (see wrangler.toml for R2 + KV bindings the Worker expects)
//
// The plugin sends:
//   X-Install-Id        — anonymous GUID, one per install
//   X-Plugin-Version    — semver of the plugin build
//   X-Plugin-Hash       — first 16 hex of SHA-256(plugin.dll)
//   X-Game-Version      — FFXIV client build hash
//   X-Client-Region     — Dalamud ClientLanguage (English/Japanese/...)
//   X-Os-Platform       — Win32NT / Unix / etc.
//   X-Schema-Version    — envelope schema version (currently 1)
//   X-Stream            — one of: games, errors, findings, memdumps, discards, inputs, sigprobes
//   X-Filename          — original filename on the client (already-shipped files
//                         drop a .shipped sidecar locally so we won't see them twice)
//   Content-Encoding    — gzip
//   Body                — gzipped NDJSON or binary blob

export interface Env {
  TELEMETRY_BUCKET: R2Bucket;
  RATE_LIMIT_KV: KVNamespace;
}

const ALLOWED_STREAMS = new Set([
  "games", "errors", "findings", "memdumps", "discards", "inputs", "sigprobes",
]);

// Per-install rolling 24-hour upload cap. Memdumps dominate; everything else
// is small. 200 MB/day/install is generous enough that no honest user hits
// it, low enough that a runaway client can't bankrupt the bucket.
const MAX_BYTES_PER_DAY = 200 * 1024 * 1024;

// Hard per-request size cap. Pre-gzip the plugin keeps individual files
// well under 1 MB; allow 10 MB to leave headroom for memdump rolls.
const MAX_REQUEST_BYTES = 10 * 1024 * 1024;

const GUID_RE = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

export default {
  async fetch(req: Request, env: Env, _ctx: ExecutionContext): Promise<Response> {
    if (req.method === "GET" && new URL(req.url).pathname === "/health")
      return new Response("ok", { status: 200 });

    if (req.method !== "POST")
      return json({ error: "method_not_allowed" }, 405);

    const url = new URL(req.url);
    if (url.pathname !== "/v1/upload")
      return json({ error: "not_found" }, 404);

    // ---- Header validation ----
    const installId = req.headers.get("X-Install-Id") ?? "";
    if (!GUID_RE.test(installId))
      return json({ error: "invalid_install_id" }, 400);

    const stream = req.headers.get("X-Stream") ?? "";
    if (!ALLOWED_STREAMS.has(stream))
      return json({ error: "invalid_stream" }, 400);

    const filename = (req.headers.get("X-Filename") ?? "").replace(/[^a-zA-Z0-9._-]/g, "_");
    if (!filename || filename.length > 200)
      return json({ error: "invalid_filename" }, 400);

    const schemaVersion = parseInt(req.headers.get("X-Schema-Version") ?? "0", 10);
    if (!Number.isFinite(schemaVersion) || schemaVersion < 1 || schemaVersion > 10)
      return json({ error: "invalid_schema_version" }, 400);

    // ---- Size cap (cheap pre-check on Content-Length) ----
    const declaredLen = parseInt(req.headers.get("Content-Length") ?? "0", 10);
    if (declaredLen > MAX_REQUEST_BYTES)
      return json({ error: "payload_too_large", limit: MAX_REQUEST_BYTES }, 413);

    // ---- Per-install daily rate limit (KV) ----
    const today = new Date().toISOString().substring(0, 10);
    const rateKey = `bytes:${installId}:${today}`;
    const usedRaw = await env.RATE_LIMIT_KV.get(rateKey);
    const usedBytes = usedRaw ? parseInt(usedRaw, 10) : 0;
    if (usedBytes + declaredLen > MAX_BYTES_PER_DAY)
      return json({ error: "rate_limited", limit: MAX_BYTES_PER_DAY }, 429);

    // ---- Read body (Cloudflare auto-decompresses on Content-Encoding: gzip
    // for *response* paths but NOT request bodies — we keep the gzip on the
    // wire and store as-is to save bytes and let analyzers decompress on
    // demand). ----
    const body = await req.arrayBuffer();
    if (body.byteLength > MAX_REQUEST_BYTES)
      return json({ error: "payload_too_large", limit: MAX_REQUEST_BYTES }, 413);

    // ---- Write to R2 ----
    const key = `${stream}/${installId}/${today}/${filename}.gz`;
    await env.TELEMETRY_BUCKET.put(key, body, {
      httpMetadata: {
        contentType: "application/octet-stream",
        contentEncoding: "gzip",
      },
      customMetadata: {
        install_id: installId,
        plugin_version: req.headers.get("X-Plugin-Version") ?? "",
        plugin_hash: req.headers.get("X-Plugin-Hash") ?? "",
        game_version: req.headers.get("X-Game-Version") ?? "",
        client_region: req.headers.get("X-Client-Region") ?? "",
        os_platform: req.headers.get("X-Os-Platform") ?? "",
        schema_version: schemaVersion.toString(),
        received_at: new Date().toISOString(),
      },
    });

    // Update rate-limit counter. 25-hour TTL so the day boundary is never
    // missed if KV propagation lags.
    await env.RATE_LIMIT_KV.put(
      rateKey,
      (usedBytes + body.byteLength).toString(),
      { expirationTtl: 25 * 60 * 60 });

    return json({ ok: true, key, bytes: body.byteLength }, 200);
  },
};

function json(payload: unknown, status: number): Response {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { "content-type": "application/json" },
  });
}
