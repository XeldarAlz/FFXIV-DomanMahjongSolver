using Mahjong.Plugin.Dalamud.Telemetry;

namespace Mahjong.Plugin.Dalamud.Tests;

public class SeatPoolRegistryTests
{
    [Fact]
    public void Starts_empty()
    {
        var reg = new SeatPoolRegistry();
        Assert.Empty(reg.Bases);
    }

    [Fact]
    public void Observe_adds_a_pool_address()
    {
        var reg = new SeatPoolRegistry();
        reg.Observe(0x1234);
        Assert.Single(reg.Bases);
        Assert.Contains((nint)0x1234, reg.Bases);
    }

    [Fact]
    public void Observe_dedupes_identical_addresses()
    {
        var reg = new SeatPoolRegistry();
        reg.Observe(0x1234);
        reg.Observe(0x1234);
        reg.Observe(0x1234);
        Assert.Single(reg.Bases);
    }

    [Fact]
    public void Observe_collects_distinct_addresses()
    {
        var reg = new SeatPoolRegistry();
        reg.Observe(0x1000);
        reg.Observe(0x2000);
        reg.Observe(0x3000);
        reg.Observe(0x4000);
        Assert.Equal(4, reg.Bases.Count);
    }

    [Fact]
    public void Observe_drops_zero_pointer()
    {
        var reg = new SeatPoolRegistry();
        reg.Observe((nint)0);
        Assert.Empty(reg.Bases);
    }

    [Fact]
    public void Observe_drops_negative_one_sentinel()
    {
        var reg = new SeatPoolRegistry();
        reg.Observe((nint)(-1));
        Assert.Empty(reg.Bases);
    }

    [Fact]
    public void Observe_keeps_valid_addresses_when_garbage_is_interleaved()
    {
        var reg = new SeatPoolRegistry();
        reg.Observe(0x1000);
        reg.Observe(0);
        reg.Observe(0x2000);
        reg.Observe(-1);
        reg.Observe(0x3000);

        Assert.Equal(3, reg.Bases.Count);
    }

    [Fact]
    public void Bases_is_a_snapshot_of_keys_at_call_time()
    {
        // ConcurrentDictionary.Keys returns a moment-in-time copy. Pin the
        // semantics so consumers know they need to re-read on every dump,
        // not cache the collection across ticks.
        var reg = new SeatPoolRegistry();
        var snapshot = reg.Bases;
        reg.Observe(0x1000);
        Assert.Empty(snapshot);
        Assert.Single(reg.Bases);
    }
}
