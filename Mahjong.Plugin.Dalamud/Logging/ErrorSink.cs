using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>
/// Buffered, append-only NDJSON sink for plugin errors and warnings. Files
/// land in <c>pluginConfigs/&lt;plugin&gt;/errors/errors-yyyyMMdd.ndjson</c>;
/// <see cref="Telemetry.TelemetryUploader"/> picks them up and ships them.
///
/// <para>Two ways data lands here:
/// <list type="bullet">
///   <item>Explicit <see cref="RecordException"/> / <see cref="RecordWarning"/>
///   calls from inside the plugin (existing <c>Plugin.Log.Error</c> sites are
///   migrated incrementally).</item>
///   <item>An <see cref="AppDomain.UnhandledException"/> hook that captures
///   any unhandled exception in plugin code as a last-resort crash report.</item>
/// </list></para>
///
/// <para>All file writes go through a process-private lock so concurrent
/// callers from different threads don't interleave NDJSON lines. Errors
/// inside the sink itself are silently swallowed — a broken error sink
/// must never throw into game code.</para>
/// </summary>
public sealed class ErrorSink : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string errorsDir;
    private readonly object writerLock = new();
    private bool disposed;
    private long sequence;

    public string ErrorsDir => errorsDir;

    public ErrorSink(string pluginConfigDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDirectory);
        errorsDir = Path.Combine(pluginConfigDirectory, "errors");
        try
        { Directory.CreateDirectory(errorsDir); }
        catch { }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        { AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException; }
        catch { }
    }

    /// <summary>Record a caught exception with optional context tag (e.g.
    /// "GameLogger.OnStateChanged"). Stack trace is captured automatically.</summary>
    public void RecordException(string context, Exception ex)
    {
        if (disposed || ex is null)
            return;
        Write(new ErrorEntry(
            T: NowIso(),
            Seq: Interlocked.Increment(ref sequence),
            Severity: "error",
            Context: context ?? "(none)",
            Message: ex.Message ?? "",
            ExceptionType: ex.GetType().FullName ?? "",
            Stack: ex.StackTrace ?? "",
            Inner: ex.InnerException?.Message,
            Source: "explicit"));
    }

    /// <summary>Record a non-throwing warning (e.g. "sigscan returned 0 hits",
    /// "addon field offset returned out-of-range tile id"). No stack.</summary>
    public void RecordWarning(string context, string message)
    {
        if (disposed)
            return;
        Write(new ErrorEntry(
            T: NowIso(),
            Seq: Interlocked.Increment(ref sequence),
            Severity: "warn",
            Context: context ?? "(none)",
            Message: message ?? "",
            ExceptionType: null,
            Stack: null,
            Inner: null,
            Source: "explicit"));
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex)
            return;
        Write(new ErrorEntry(
            T: NowIso(),
            Seq: Interlocked.Increment(ref sequence),
            Severity: e.IsTerminating ? "fatal" : "error",
            Context: "AppDomain.UnhandledException",
            Message: ex.Message ?? "",
            ExceptionType: ex.GetType().FullName ?? "",
            Stack: ex.StackTrace ?? "",
            Inner: ex.InnerException?.Message,
            Source: "appdomain"));
    }

    private void Write(ErrorEntry entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, JsonOpts);
            var path = Path.Combine(errorsDir, $"errors-{DateTime.UtcNow:yyyyMMdd}.ndjson");
            lock (writerLock)
            {
                using var w = new StreamWriter(new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.Read));
                w.WriteLine(line);
            }
        }
        catch { /* never throw */ }
    }

    private static string NowIso() =>
        DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private sealed record ErrorEntry(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("seq")] long Seq,
        [property: JsonPropertyName("sev")] string Severity,
        [property: JsonPropertyName("ctx")] string Context,
        [property: JsonPropertyName("msg")] string Message,
        [property: JsonPropertyName("ex")] string? ExceptionType,
        [property: JsonPropertyName("stack")] string? Stack,
        [property: JsonPropertyName("inner")] string? Inner,
        [property: JsonPropertyName("src")] string Source);
}
