using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>
/// Structured "findings" channel: one append-only NDJSON per day under
/// <c>pluginConfigs/&lt;plugin&gt;/findings/findings-yyyyMMdd.ndjson</c>.
/// Records the plugin's runtime discoveries that are otherwise lost to
/// <c>Plugin.Log.Info</c> ephemera — variant probes, sigscan results, addon
/// field-read failures, anything that helps reverse-engineer the addon
/// across clients.
///
/// <para>Each entry is a single JSON object with a stable <c>kind</c> field
/// the server can shard on (e.g. <c>variant_match</c>, <c>variant_miss</c>,
/// <c>sigscan_hit</c>, <c>field_read_fail</c>) plus a free-form
/// <c>data</c> bag. The schema is intentionally loose so new finding kinds
/// don't require a schema bump on the server.</para>
/// </summary>
public interface IFindingsLog
{
    void Record(string kind, IReadOnlyDictionary<string, object?> data);
    void Record(string kind, string note);
}

internal sealed class FindingsLog : IFindingsLog, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ErrorSink errors;
    private readonly string findingsDir;
    private readonly object writerLock = new();
    private long sequence;
    private bool disposed;

    public string FindingsDir => findingsDir;

    public FindingsLog(string pluginConfigDirectory, ErrorSink errors)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDirectory);
        ArgumentNullException.ThrowIfNull(errors);
        this.errors = errors;
        findingsDir = Path.Combine(pluginConfigDirectory, "findings");
        try
        { Directory.CreateDirectory(findingsDir); }
        catch { }
    }

    public void Dispose() => disposed = true;

    public void Record(string kind, IReadOnlyDictionary<string, object?> data)
    {
        if (disposed || string.IsNullOrEmpty(kind))
            return;
        WriteEntry(new FindingEntry(
            T: NowIso(),
            Seq: Interlocked.Increment(ref sequence),
            Kind: kind,
            Data: data,
            Note: null));
    }

    public void Record(string kind, string note)
    {
        if (disposed || string.IsNullOrEmpty(kind))
            return;
        WriteEntry(new FindingEntry(
            T: NowIso(),
            Seq: Interlocked.Increment(ref sequence),
            Kind: kind,
            Data: null,
            Note: note));
    }

    private void WriteEntry(FindingEntry entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, JsonOpts);
            var path = Path.Combine(findingsDir, $"findings-{DateTime.UtcNow:yyyyMMdd}.ndjson");
            lock (writerLock)
            {
                using var w = new StreamWriter(new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.Read));
                w.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            // Findings failures funnel into the error sink so we still see
            // them in the corpus. ErrorSink itself can't recursively fail
            // (it swallows everything internally).
            errors.RecordException("FindingsLog.WriteEntry", ex);
        }
    }

    private static string NowIso() =>
        DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private sealed record FindingEntry(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("seq")] long Seq,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("data")] IReadOnlyDictionary<string, object?>? Data,
        [property: JsonPropertyName("note")] string? Note);
}
