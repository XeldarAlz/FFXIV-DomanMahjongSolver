using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Test-double for <see cref="IDiscardCapture"/> that lets tests fire
/// arbitrary <see cref="DiscardEvent"/>s and assert that subscribers — like
/// <see cref="DiscardCaptureLogger"/> — react correctly.
/// </summary>
internal sealed class FakeDiscardCapture : IDiscardCapture
{
    public HookHealth Health { get; set; } = HookHealth.Active;
    public string StrategyName { get; set; } = "fake";
    public ulong TotalCaptured { get; set; }
    public int LastTileId { get; set; } = -1;
    public event Action<DiscardEvent>? DiscardObserved;

    public void Fire(DiscardEvent evt) => DiscardObserved?.Invoke(evt);
    public void Dispose() { }
}

public class DiscardCaptureLoggerTests
{
    [Fact]
    public void Throws_on_null_capture()
    {
        using var tmp = new TempDir();
        Assert.Throws<ArgumentNullException>(() =>
            new DiscardCaptureLogger(null!, tmp.Path));
    }

    [Fact]
    public void Throws_on_null_or_empty_directory()
    {
        var capture = new FakeDiscardCapture();
        Assert.Throws<ArgumentNullException>(() => new DiscardCaptureLogger(capture, null!));
        Assert.Throws<ArgumentException>(() => new DiscardCaptureLogger(capture, ""));
    }

    [Fact]
    public void LogPath_is_emj_discards_log_under_supplied_directory()
    {
        using var tmp = new TempDir();
        var capture = new FakeDiscardCapture();
        using var logger = new DiscardCaptureLogger(capture, tmp.Path);

        Assert.Equal(Path.Combine(tmp.Path, "emj-discards.log"), logger.LogPath);
    }

    [Fact]
    public void Constructor_creates_target_directory_if_missing()
    {
        using var tmp = new TempDir();
        var nested = Path.Combine(tmp.Path, "nested", "deeper");
        var capture = new FakeDiscardCapture();
        using var logger = new DiscardCaptureLogger(capture, nested);

        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public void Captured_event_writes_a_log_line()
    {
        using var tmp = new TempDir();
        var capture = new FakeDiscardCapture { StrategyName = "native-asm" };
        using var logger = new DiscardCaptureLogger(capture, tmp.Path);

        capture.Fire(new DiscardEvent(
            Seat: 2,
            Tile: Tile.FromId(15),
            ObservedAtUtc: new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc),
            SequenceNumber: 7));

        var contents = File.ReadAllText(logger.LogPath);
        Assert.Contains("seq=7", contents);
        Assert.Contains("strategy=native-asm", contents);
        Assert.Contains("seat=2", contents);
        Assert.Contains("tile_id=15", contents);
    }

    [Fact]
    public void Unknown_seat_is_rendered_as_question_mark()
    {
        using var tmp = new TempDir();
        var capture = new FakeDiscardCapture();
        using var logger = new DiscardCaptureLogger(capture, tmp.Path);

        capture.Fire(new DiscardEvent(-1, Tile.FromId(5), DateTime.UtcNow, 1));

        var contents = File.ReadAllText(logger.LogPath);
        Assert.Contains("seat=?", contents);
    }

    [Fact]
    public void Multiple_events_append_one_line_each()
    {
        using var tmp = new TempDir();
        var capture = new FakeDiscardCapture();
        using var logger = new DiscardCaptureLogger(capture, tmp.Path);

        capture.Fire(new DiscardEvent(0, Tile.FromId(1), DateTime.UtcNow, 1));
        capture.Fire(new DiscardEvent(1, Tile.FromId(2), DateTime.UtcNow, 2));
        capture.Fire(new DiscardEvent(2, Tile.FromId(3), DateTime.UtcNow, 3));

        var lines = File.ReadAllLines(logger.LogPath);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Disposed_logger_does_not_write_subsequent_events()
    {
        using var tmp = new TempDir();
        var capture = new FakeDiscardCapture();
        var logger = new DiscardCaptureLogger(capture, tmp.Path);

        capture.Fire(new DiscardEvent(0, Tile.FromId(1), DateTime.UtcNow, 1));
        logger.Dispose();
        capture.Fire(new DiscardEvent(0, Tile.FromId(2), DateTime.UtcNow, 2));

        var lines = File.ReadAllLines(logger.LogPath);
        Assert.Single(lines);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        using var tmp = new TempDir();
        var logger = new DiscardCaptureLogger(new FakeDiscardCapture(), tmp.Path);
        logger.Dispose();
        logger.Dispose();
    }
}
