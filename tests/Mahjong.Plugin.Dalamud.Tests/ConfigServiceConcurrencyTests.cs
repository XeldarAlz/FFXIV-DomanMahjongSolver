using System.Threading.Tasks;
using Mahjong.Plugin.Dalamud.Composition;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Concurrency-flavored tests for <see cref="DalamudConfigService"/>. The
/// service uses an internal lock so concurrent <c>Update</c> calls don't
/// interleave; these tests exercise that guarantee under contention.
/// </summary>
public class ConfigServiceConcurrencyTests
{
    [Fact]
    public void Concurrent_updates_do_not_lose_writes()
    {
        var saved = new List<Configuration>();
        var savedLock = new object();
        var svc = new DalamudConfigService(
            c =>
            {
                lock (savedLock)
                    saved.Add(c);
            },
            new Configuration { HumanizedDelayMs = 0 });

        const int updates = 200;
        Parallel.For(0, updates, _ =>
            svc.Update(c => c with { HumanizedDelayMs = c.HumanizedDelayMs + 1 }));

        Assert.Equal(updates, svc.Current.HumanizedDelayMs);
        Assert.Equal(updates, saved.Count);
    }

    [Fact]
    public async Task Concurrent_reads_and_writes_never_observe_a_torn_record()
    {
        var svc = new DalamudConfigService(
            _ => { },
            new Configuration { TosAccepted = true, AutomationArmed = true });

        // Writers flip between two consistent shapes; readers should always
        // see one or the other, never a mix.
        var done = new System.Threading.ManualResetEventSlim(false);
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                svc.Update(c => c with { TosAccepted = true, AutomationArmed = true });
                svc.Update(c => c with { TosAccepted = false, AutomationArmed = false });
            }
            done.Set();
        });

        int readChecks = 0;
        while (!done.IsSet)
        {
            var snap = svc.Current;
            // Both fields move together in every Update — they should never
            // be observed in a mismatched state.
            Assert.Equal(snap.TosAccepted, snap.AutomationArmed);
            readChecks++;
        }

        await writer;
        Assert.True(readChecks > 0, "reader thread didn't run");
    }

    [Fact]
    public void Changed_fires_once_per_successful_update_under_contention()
    {
        var svc = new DalamudConfigService(_ => { }, new Configuration());
        int changedCount = 0;
        svc.Changed += _ => System.Threading.Interlocked.Increment(ref changedCount);

        const int updates = 100;
        Parallel.For(0, updates, _ =>
            svc.Update(c => c with { HumanizedDelayMs = c.HumanizedDelayMs + 1 }));

        Assert.Equal(updates, changedCount);
    }
}
