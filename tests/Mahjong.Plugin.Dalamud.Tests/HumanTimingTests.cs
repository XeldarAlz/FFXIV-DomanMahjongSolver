using Mahjong.Plugin.Dalamud.Actions;

namespace Mahjong.Plugin.Dalamud.Tests;

public class HumanTimingTests
{
    [Fact]
    public void Default_delay_falls_within_floor_and_cap()
    {
        for (int i = 0; i < 200; i++)
        {
            var delay = HumanTiming.RandomDelay();
            Assert.InRange(delay.TotalMilliseconds, 400.0, 2500.0);
        }
    }

    [Fact]
    public void Custom_floor_and_cap_are_respected()
    {
        for (int i = 0; i < 200; i++)
        {
            var delay = HumanTiming.RandomDelay(medianMs: 1000.0, floorMs: 800.0, capMs: 1200.0);
            Assert.InRange(delay.TotalMilliseconds, 800.0, 1200.0);
        }
    }

    [Fact]
    public void Median_drives_distribution_center()
    {
        // Sample N draws and compare the empirical median against the
        // requested one. Log-normal with σ=0.45 has a sample median that
        // converges to the parameter; 1000 samples gives ample precision.
        const int n = 1000;
        var samples = new double[n];
        for (int i = 0; i < n; i++)
            samples[i] = HumanTiming.RandomDelay(medianMs: 600.0).TotalMilliseconds;
        Array.Sort(samples);
        double empiricalMedian = samples[n / 2];

        // Wide tolerance — clipping to floor/cap can pull the median around.
        Assert.InRange(empiricalMedian, 500.0, 700.0);
    }

    [Fact]
    public void Returns_a_non_negative_timespan()
    {
        for (int i = 0; i < 100; i++)
        {
            var delay = HumanTiming.RandomDelay();
            Assert.True(delay.TotalMilliseconds >= 0);
        }
    }
}
