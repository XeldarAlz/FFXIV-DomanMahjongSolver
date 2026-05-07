using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.Plugin.Dalamud.Telemetry;

/// <summary>
/// Resolves the telemetry ingest URL at plugin startup by fetching a small
/// JSON config from the project's GitHub repo. This indirection lets the
/// maintainer rotate endpoints (e.g. swap Cloudflare Worker URLs, fail over
/// to a backup) without shipping a plugin release — every install picks up
/// the new URL on next launch.
///
/// <para>Network failure is non-fatal: falls back to the embedded default
/// so a user with no internet at plugin-load still has a target to retry
/// against later. The fallback URL is the only string that ships in the
/// binary; everything else flows through the GitHub-hosted config.</para>
/// </summary>
public sealed class EndpointResolver
{
    /// <summary>Default endpoint baked into the build. Only used when the
    /// GitHub-hosted config is unreachable. Update this when you first
    /// deploy the Cloudflare Worker so even offline-at-startup users have
    /// a real target.</summary>
    public const string EmbeddedFallbackUrl =
        "https://mahjong-telemetry.example.workers.dev/v1/upload";

    /// <summary>Where the live config lives. Plain HTTPS GET, no auth — the
    /// file is public on the repo and contains nothing secret.</summary>
    public const string ConfigUrl =
        "https://raw.githubusercontent.com/XeldarAlz/FFXIV-MahjongAI/main/server/telemetry-endpoint.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Fetch the live endpoint config. Times out after 5 seconds — plugin
    /// startup must not block on a slow GitHub. On any failure, returns a
    /// config pointing at <see cref="EmbeddedFallbackUrl"/> with telemetry
    /// enabled, so the uploader still has somewhere to ship.
    /// </summary>
    public static async Task<TelemetryEndpoint> ResolveAsync(
        HttpClient http, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var resp = await http.GetAsync(ConfigUrl, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var parsed = await JsonSerializer.DeserializeAsync<TelemetryEndpoint>(
                stream, JsonOpts, cts.Token).ConfigureAwait(false);

            if (parsed is null || string.IsNullOrWhiteSpace(parsed.UploadUrl))
                return Fallback();
            return parsed;
        }
        catch
        {
            return Fallback();
        }
    }

    private static TelemetryEndpoint Fallback() =>
        new(UploadUrl: EmbeddedFallbackUrl, Enabled: true, MinPluginVersion: null);
}

/// <summary>
/// Schema for <c>server/telemetry-endpoint.json</c>. Kept tiny so the file
/// is readable at a glance and easy to hand-edit during deployment.
/// </summary>
public sealed record TelemetryEndpoint(
    [property: JsonPropertyName("upload_url")] string UploadUrl,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("min_plugin_version")] string? MinPluginVersion);
