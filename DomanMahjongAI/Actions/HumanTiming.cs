using System;

namespace DomanMahjongAI.Actions;

/// <summary>
/// Log-normal humanized delays for input dispatch. Plan §10.4 targets
/// ~900ms median, 400ms floor, 2500ms tail cap.
/// </summary>
public static class HumanTiming
{
    private static readonly Random rng = new();

    /// <summary>A random log-normal delay clipped to [floor, cap] milliseconds.</summary>
    public static TimeSpan RandomDelay(
        double medianMs = 900.0,
        double sigma = 0.45,
        double floorMs = 400.0,
        double capMs = 2500.0)
    {
        // Log-normal: exp(N(μ, σ²)) where μ = ln(median).
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        double stdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        double ms = Math.Exp(Math.Log(medianMs) + sigma * stdNormal);
        ms = Math.Clamp(ms, floorMs, capMs);
        return TimeSpan.FromMilliseconds(ms);
    }
}
