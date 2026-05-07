using System;
using System.IO;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>
/// Subscribes to <see cref="IDiscardCapture.DiscardObserved"/> and appends
/// one line per captured discard to a log file in the plugin's config
/// directory. Diagnostic only — useful for reverse-engineering sessions
/// and for verifying that a fallback strategy is picking up live events.
///
/// <para>IO errors are swallowed: a failed log write must never break the
/// capture pipeline.</para>
/// </summary>
public sealed class DiscardCaptureLogger : IDisposable
{
    private readonly IDiscardCapture capture;
    private readonly string logPath;
    private bool disposed;

    public string LogPath => logPath;

    public DiscardCaptureLogger(IDiscardCapture capture, string pluginConfigDirectory)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDirectory);
        this.capture = capture;

        Directory.CreateDirectory(pluginConfigDirectory);
        logPath = Path.Combine(pluginConfigDirectory, "emj-discards.log");

        capture.DiscardObserved += OnDiscard;
    }

    private void OnDiscard(DiscardEvent evt)
    {
        if (disposed)
            return;
        try
        {
            using var w = new StreamWriter(new FileStream(
                logPath, FileMode.Append, FileAccess.Write, FileShare.Read));
            string seat = evt.Seat >= 0 ? evt.Seat.ToString() : "?";
            w.WriteLine(
                $"{evt.ObservedAtUtc:o}  seq={evt.SequenceNumber}  " +
                $"strategy={capture.StrategyName}  seat={seat}  " +
                $"tile_id={evt.Tile.Id} ({evt.Tile})");
        }
        catch { }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        capture.DiscardObserved -= OnDiscard;
    }
}
