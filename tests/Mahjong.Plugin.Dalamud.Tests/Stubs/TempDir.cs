using System;
using System.IO;

namespace Mahjong.Plugin.Dalamud.Tests.Stubs;

/// <summary>
/// Disposable per-test temp directory. Used by tests that exercise file-IO
/// sinks (ErrorSink, FindingsLog, GameLogger, DiscardCaptureLogger) — each
/// one runs against a fresh path so concurrent test runs don't interfere.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Mahjong.Plugin.Dalamud.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(Path, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
