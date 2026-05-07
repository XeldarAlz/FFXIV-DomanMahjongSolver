using System;

namespace Mahjong.Plugin.Game.Tests;

public class InertDiscardCaptureTests
{
    [Fact]
    public void Reports_offline_health_and_inert_strategy_name()
    {
        using var capture = new InertDiscardCapture();
        Assert.Equal(HookHealth.Offline, capture.Health);
        Assert.Equal(InertDiscardCapture.Name, capture.StrategyName);
        Assert.Equal("inert", capture.StrategyName);
    }

    [Fact]
    public void Diagnostics_stay_at_their_initial_values()
    {
        using var capture = new InertDiscardCapture();
        Assert.Equal(0UL, capture.TotalCaptured);
        Assert.Equal(-1, capture.LastTileId);
    }

    [Fact]
    public void Subscribing_to_event_does_not_throw_and_event_never_fires()
    {
        using var capture = new InertDiscardCapture();
        int fired = 0;
        Action<DiscardEvent> handler = _ => fired++;
        capture.DiscardObserved += handler;
        capture.DiscardObserved -= handler;
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var capture = new InertDiscardCapture();
        capture.Dispose();
        capture.Dispose();
    }
}

public class DiscardEventTests
{
    [Fact]
    public void Records_full_payload_including_seat_and_sequence()
    {
        var t = Tile.FromId(7);
        var when = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc);
        var evt = new DiscardEvent(Seat: 2, Tile: t, ObservedAtUtc: when, SequenceNumber: 42);

        Assert.Equal(2, evt.Seat);
        Assert.Equal(t, evt.Tile);
        Assert.Equal(when, evt.ObservedAtUtc);
        Assert.Equal(42UL, evt.SequenceNumber);
    }

    [Fact]
    public void Unknown_seat_is_negative_one()
    {
        var evt = new DiscardEvent(-1, Tile.FromId(0), DateTime.UtcNow, 1);
        Assert.Equal(-1, evt.Seat);
    }

    [Fact]
    public void Two_events_with_same_payload_are_equal()
    {
        var t = Tile.FromId(5);
        var when = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc);
        var a = new DiscardEvent(1, t, when, 99);
        var b = new DiscardEvent(1, t, when, 99);
        Assert.Equal(a, b);
    }
}
