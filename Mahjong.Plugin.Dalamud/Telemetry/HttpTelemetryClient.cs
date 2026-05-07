using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Mahjong.Plugin.Dalamud.Telemetry;

/// <summary>
/// Thin wrapper around <see cref="HttpClient"/> that gzips a payload and
/// POSTs it to the resolved telemetry endpoint with envelope headers.
/// Encoding-only — has no concept of files, retries, or scheduling; that
/// lives in <see cref="TelemetryUploader"/>.
///
/// <para>Errors are reported to the supplied <see cref="IPluginLog"/> at
/// Warning level (one line per failure) so the upload pipeline stays
/// visible without flooding the log.</para>
/// </summary>
public sealed class HttpTelemetryClient
{
    private readonly HttpClient http;
    private readonly TelemetryEnvelope envelope;
    private readonly IPluginLog log;

    public HttpTelemetryClient(HttpClient http, TelemetryEnvelope envelope, IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(log);
        this.http = http;
        this.envelope = envelope;
        this.log = log;
    }

    /// <summary>
    /// POST <paramref name="payloadPath"/>'s contents (gzipped) to
    /// <paramref name="endpoint"/> tagged as <paramref name="stream"/>.
    /// Returns true if the server returned 2xx; false (and logs) on any
    /// network / status / serialization failure.
    /// </summary>
    public async Task<bool> UploadAsync(
        string endpoint, string stream, string payloadPath, CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint);
            msg.Headers.Add("X-Install-Id", envelope.InstallId.ToString("D"));
            msg.Headers.Add("X-Plugin-Version", envelope.PluginVersion);
            msg.Headers.Add("X-Plugin-Hash", envelope.PluginHash);
            msg.Headers.Add("X-Game-Version", envelope.GameVersion);
            msg.Headers.Add("X-Client-Region", envelope.ClientRegion);
            msg.Headers.Add("X-Os-Platform", envelope.OsPlatform);
            msg.Headers.Add("X-Schema-Version", envelope.SchemaVersion.ToString());
            msg.Headers.Add("X-Stream", stream);
            msg.Headers.Add("X-Filename", Path.GetFileName(payloadPath));

            // Pre-gzip into a memory stream so Content-Length is set and the
            // server can reject oversize uploads before reading the body.
            var compressed = await ReadAndCompressAsync(payloadPath, ct).ConfigureAwait(false);
            var content = new ByteArrayContent(compressed);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentEncoding.Add("gzip");
            msg.Content = content;

            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return true;

            log.Warning(
                $"[Telemetry] upload failed: stream={stream} file={Path.GetFileName(payloadPath)} " +
                $"status={(int)resp.StatusCode}");
            return false;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is normal at shutdown — surface only at debug.
            log.Debug($"[Telemetry] upload canceled: {Path.GetFileName(payloadPath)}");
            return false;
        }
        catch (Exception ex)
        {
            log.Warning($"[Telemetry] upload exception: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static async Task<byte[]> ReadAndCompressAsync(string path, CancellationToken ct)
    {
        await using var src = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        using var ms = new MemoryStream();
        await using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            await src.CopyToAsync(gz, ct).ConfigureAwait(false);
        }
        return ms.ToArray();
    }
}
