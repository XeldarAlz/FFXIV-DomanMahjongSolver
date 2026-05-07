using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Dalamud.Game;

namespace Mahjong.Plugin.Dalamud.Telemetry;

/// <summary>
/// Per-install metadata stamped onto every upload as HTTP headers. Built
/// once at plugin start (values don't change for the life of the process)
/// and reused by every <see cref="HttpTelemetryClient"/> POST.
///
/// <para>Carries everything the server needs to slice the corpus by client
/// build / region / plugin version without ever seeing PII. <see cref="InstallId"/>
/// is the only stable handle to a single install; everything else is derived
/// from the running process and assembly.</para>
/// </summary>
public sealed record TelemetryEnvelope(
    Guid InstallId,
    string PluginVersion,
    string PluginHash,
    string GameVersion,
    string ClientRegion,
    string OsPlatform,
    int SchemaVersion)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Build the envelope from runtime state. Each derivation is wrapped
    /// in a try — telemetry must never fail the plugin's startup, so any
    /// missing piece falls back to <c>"unknown"</c> and the upload still
    /// happens. The server can flag/ignore "unknown" buckets later.
    /// </summary>
    public static TelemetryEnvelope Build(Guid installId, ClientLanguage clientLanguage)
    {
        return new TelemetryEnvelope(
            InstallId: installId,
            PluginVersion: SafeGet(GetPluginVersion, "0.0.0"),
            PluginHash: SafeGet(GetPluginAssemblyHash, "unknown"),
            GameVersion: SafeGet(GetGameVersion, "unknown"),
            ClientRegion: clientLanguage.ToString(),
            OsPlatform: Environment.OSVersion.Platform.ToString(),
            SchemaVersion: CurrentSchemaVersion);
    }

    private static string SafeGet(Func<string> get, string fallback)
    {
        try
        { return get() ?? fallback; }
        catch { return fallback; }
    }

    private static string GetPluginVersion() =>
        typeof(TelemetryEnvelope).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(TelemetryEnvelope).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>
    /// SHA-256 prefix of the plugin assembly file. Lets the server tell
    /// which build produced an upload — critical when chasing "this user
    /// has stale offsets" bugs across the corpus.
    /// </summary>
    private static string GetPluginAssemblyHash()
    {
        var path = typeof(TelemetryEnvelope).Assembly.Location;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return "unknown";
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).Substring(0, 16);
    }

    private static string GetGameVersion()
    {
        var module = Process.GetCurrentProcess().MainModule;
        return module?.FileVersionInfo.FileVersion
            ?? module?.FileVersionInfo.ProductVersion
            ?? "unknown";
    }
}
