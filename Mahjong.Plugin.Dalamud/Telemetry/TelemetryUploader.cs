using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Telemetry;

/// <summary>
/// Background uploader. Watches the plugin config directory for files in
/// known telemetry stream subdirs (games/, errors/, findings/, memdumps/,
/// discards/, inputs/, sigprobes/), gzips and POSTs each one to the resolved
/// endpoint, then drops a <c>.shipped</c> sidecar so it never re-uploads.
///
/// <para><b>Lifecycle:</b> a single long-running task pulls jobs off a
/// <see cref="Channel{T}"/> that's fed by a periodic scan AND by direct
/// <see cref="Enqueue(string, string)"/> calls (so newly-rolled hand files
/// can be shipped the moment they close, without waiting for the next
/// scan tick). On <see cref="Dispose"/> the channel is completed and we
/// drain in-flight uploads under a hard 10s timeout — anything still
/// pending stays on disk and ships on next launch.</para>
///
/// <para><b>Failure isolation:</b> exceptions inside the worker loop are
/// logged once at Warning and swallowed. The pipeline never throws into
/// game code; a broken endpoint just means files pile up locally until the
/// network or server recovers.</para>
/// </summary>
public sealed class TelemetryUploader : IDisposable
{
    /// <summary>Subdirectories under the plugin config dir that the uploader
    /// scans. Order is stable so corpus arrival order is deterministic per
    /// install.</summary>
    public static readonly string[] StreamDirs =
        { "games", "errors", "findings", "memdumps", "discards", "inputs", "sigprobes" };

    private const string ShippedMarkerSuffix = ".shipped";
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpTelemetryClient http;
    private readonly EndpointHolder endpoint;
    private readonly IConfigService<Configuration> configService;
    private readonly IPluginLog log;
    private readonly string configDir;
    private readonly CancellationTokenSource cts = new();
    private readonly Channel<UploadJob> queue;
    private readonly Task workerTask;
    private bool disposed;

    public TelemetryUploader(
        HttpTelemetryClient http,
        EndpointHolder endpoint,
        IConfigService<Configuration> configService,
        IPluginLog log,
        string configDir)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(configDir);

        this.http = http;
        this.endpoint = endpoint;
        this.configService = configService;
        this.log = log;
        this.configDir = configDir;

        // Unbounded so a long offline streak doesn't drop signal — every
        // unshipped file is on disk anyway, so worst case the channel just
        // holds path strings.
        queue = Channel.CreateUnbounded<UploadJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        EnsureStreamDirs();
        workerTask = Task.Run(() => RunAsync(cts.Token));
    }

    /// <summary>
    /// Manually enqueue a finished file for upload. Called by GameLogger on
    /// hand close, by ErrorSink on error roll-over, etc. — anywhere we know
    /// a file just became immutable and shouldn't wait for the next scan.
    /// </summary>
    public void Enqueue(string stream, string filePath)
    {
        if (disposed)
            return;
        if (string.IsNullOrEmpty(stream) || string.IsNullOrEmpty(filePath))
            return;
        queue.Writer.TryWrite(new UploadJob(stream, filePath));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        { queue.Writer.TryComplete(); }
        catch { }
        try
        {
            // Hard timeout on shutdown — Dalamud unloads can't block forever.
            // Anything still in flight will retry on next launch via the scan.
            if (!workerTask.Wait(DisposeDrainTimeout))
                cts.Cancel();
        }
        catch { }
        cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var lastScan = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastScan >= ScanInterval)
                {
                    EnqueuePendingFiles();
                    lastScan = DateTime.UtcNow;
                }

                // Wait for either a new job or the next scan deadline.
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(ScanInterval);
                UploadJob job;
                try
                {
                    job = await queue.Reader.ReadAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    continue; // scan-tick wakeup
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                await ProcessJobAsync(job, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.Warning($"[Telemetry] uploader loop error: {ex.Message}");
                // Avoid a tight failure loop if the worker itself is broken.
                try
                { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private async Task ProcessJobAsync(UploadJob job, CancellationToken ct)
    {
        if (!File.Exists(job.FilePath) || IsShipped(job.FilePath))
            return;

        var url = endpoint.Current.UploadUrl;
        if (string.IsNullOrWhiteSpace(url) || !endpoint.Current.Enabled)
            return; // disabled by remote config — leave file on disk

        // Exponential backoff: 1, 2, 4, 8, 16s. Bail after that and let the
        // periodic scan re-enqueue this file later — better than blocking
        // the worker loop on one stuck upload.
        var delays = new[] { 1, 2, 4, 8, 16 };
        for (int attempt = 0; attempt < delays.Length; attempt++)
        {
            if (ct.IsCancellationRequested)
                return;

            var ok = await http.UploadAsync(url, job.Stream, job.FilePath, ct).ConfigureAwait(false);
            if (ok)
            {
                MarkShipped(job.FilePath);
                return;
            }

            try
            { await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), ct).ConfigureAwait(false); }
            catch { return; }
        }
    }

    /// <summary>Walk every stream subdir and queue any file lacking a
    /// .shipped sidecar. Cheap — just directory enumeration with a name
    /// filter.</summary>
    private void EnqueuePendingFiles()
    {
        foreach (var stream in StreamDirs)
        {
            var dir = Path.Combine(configDir, stream);
            if (!Directory.Exists(dir))
                continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir))
                {
                    if (path.EndsWith(ShippedMarkerSuffix, StringComparison.Ordinal))
                        continue;
                    if (IsShipped(path))
                        continue;
                    queue.Writer.TryWrite(new UploadJob(stream, path));
                }
            }
            catch (Exception ex)
            {
                log.Warning($"[Telemetry] scan failed for {stream}: {ex.Message}");
            }
        }
    }

    private static bool IsShipped(string filePath) =>
        File.Exists(filePath + ShippedMarkerSuffix);

    private static void MarkShipped(string filePath)
    {
        try
        { File.WriteAllText(filePath + ShippedMarkerSuffix, "1"); }
        catch { }
    }

    private void EnsureStreamDirs()
    {
        foreach (var s in StreamDirs)
        {
            try
            { Directory.CreateDirectory(Path.Combine(configDir, s)); }
            catch { }
        }
    }

    private readonly record struct UploadJob(string Stream, string FilePath);
}

/// <summary>
/// Mutable holder for the resolved <see cref="TelemetryEndpoint"/> so the
/// uploader picks up runtime updates (e.g. if we add a refresh-from-GitHub
/// path on a long uptime) without needing a restart.
/// </summary>
public sealed class EndpointHolder
{
    public TelemetryEndpoint Current { get; private set; }

    public EndpointHolder(TelemetryEndpoint initial)
    {
        Current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    public void Set(TelemetryEndpoint next) => Current = next ?? Current;
}
