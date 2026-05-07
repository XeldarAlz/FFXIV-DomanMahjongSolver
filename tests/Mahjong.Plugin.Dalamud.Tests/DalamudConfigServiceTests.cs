using Mahjong.Plugin.Dalamud.Composition;

namespace Mahjong.Plugin.Dalamud.Tests;

public class DalamudConfigServiceTests
{
    [Fact]
    public void Throws_when_save_callback_is_null()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DalamudConfigService(null!, new Configuration()));
    }

    [Fact]
    public void Throws_when_initial_config_is_null()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DalamudConfigService(_ => { }, null!));
    }

    [Fact]
    public void Current_starts_at_initial_value()
    {
        var initial = new Configuration { TosAccepted = true };
        var svc = new DalamudConfigService(_ => { }, initial);
        Assert.Same(initial, svc.Current);
    }

    [Fact]
    public void Update_swaps_current_to_the_mutated_instance()
    {
        var saved = new List<Configuration>();
        var svc = new DalamudConfigService(saved.Add, new Configuration());

        svc.Update(c => c with { TosAccepted = true });

        Assert.True(svc.Current.TosAccepted);
    }

    [Fact]
    public void Update_persists_before_swapping_the_live_reference()
    {
        Configuration? observedDuringSave = null;
        Configuration? observedAfterSwap = null;
        DalamudConfigService? svc = null;

        // The save callback fires while the lock is still held — Current
        // should still point at the OLD instance at that moment, so the
        // contract "persist first, swap second" is observable from inside
        // the persist hook.
        svc = new DalamudConfigService(
            c =>
            {
                observedDuringSave = svc!.Current;
            },
            new Configuration { Version = 99 });

        svc.Update(c => c with { Version = 100 });
        observedAfterSwap = svc.Current;

        Assert.NotNull(observedDuringSave);
        Assert.Equal(99, observedDuringSave!.Version);
        Assert.Equal(100, observedAfterSwap!.Version);
    }

    [Fact]
    public void Update_does_not_swap_if_save_throws()
    {
        var initial = new Configuration { TosAccepted = false };
        var svc = new DalamudConfigService(_ => throw new InvalidOperationException("disk full"), initial);

        Assert.Throws<InvalidOperationException>(() =>
            svc.Update(c => c with { TosAccepted = true }));

        // The live reference stays at the last good value when persistence fails.
        Assert.Same(initial, svc.Current);
        Assert.False(svc.Current.TosAccepted);
    }

    [Fact]
    public void Update_throws_when_mutator_returns_null()
    {
        var svc = new DalamudConfigService(_ => { }, new Configuration());
        Assert.Throws<InvalidOperationException>(() =>
            svc.Update(_ => null!));
    }

    [Fact]
    public void Update_throws_on_null_mutator()
    {
        var svc = new DalamudConfigService(_ => { }, new Configuration());
        Assert.Throws<ArgumentNullException>(() => svc.Update(null!));
    }

    [Fact]
    public void Changed_fires_with_the_post_mutate_instance()
    {
        var svc = new DalamudConfigService(_ => { }, new Configuration());
        Configuration? observed = null;
        svc.Changed += c => observed = c;

        svc.Update(c => c with { PolicyTier = "mcts" });

        Assert.NotNull(observed);
        Assert.Equal("mcts", observed!.PolicyTier);
    }

    [Fact]
    public void Changed_does_not_fire_when_save_throws()
    {
        var svc = new DalamudConfigService(_ => throw new IOException(), new Configuration());
        int fired = 0;
        svc.Changed += _ => fired++;

        Assert.Throws<IOException>(() =>
            svc.Update(c => c with { TosAccepted = true }));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Multiple_updates_chain_through_the_current_value()
    {
        var saved = new List<Configuration>();
        var svc = new DalamudConfigService(saved.Add, new Configuration());

        svc.Update(c => c with { HumanizedDelayMs = 500 });
        svc.Update(c => c with { HumanizedDelayMs = c.HumanizedDelayMs + 100 });

        Assert.Equal(600, svc.Current.HumanizedDelayMs);
        Assert.Equal(2, saved.Count);
        Assert.Equal(500, saved[0].HumanizedDelayMs);
        Assert.Equal(600, saved[1].HumanizedDelayMs);
    }

    [Fact]
    public void Each_update_persists_a_distinct_record_instance()
    {
        var saved = new List<Configuration>();
        var initial = new Configuration();
        var svc = new DalamudConfigService(saved.Add, initial);

        svc.Update(c => c with { TosAccepted = true });

        Assert.NotSame(initial, saved[0]);
        Assert.Same(svc.Current, saved[0]);
    }
}
