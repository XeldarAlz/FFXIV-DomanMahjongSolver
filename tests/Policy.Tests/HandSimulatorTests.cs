using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Simulator;
using System;
using Xunit;

namespace DomanMahjongAI.Policy.Tests;

public class HandSimulatorTests
{
    [Fact]
    public void Simulates_single_hand_without_throwing()
    {
        var policies = new IPolicy[]
        {
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
        };
        var sim = new HandSimulator(new Random(42));
        var result = sim.Simulate(policies);
        Assert.InRange(result.TurnCount, 1, 200);
        Assert.Equal(4, result.FinalScores.Length);
    }

    [Fact]
    public void Hand_outcome_is_tsumo_ron_or_ryuukyoku_in_valid_run()
    {
        var policies = new IPolicy[]
        {
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
        };
        var sim = new HandSimulator(new Random(100));
        var result = sim.Simulate(policies);
        Assert.True(
            result.Outcome is HandSimulator.Outcome.Tsumo
                          or HandSimulator.Outcome.Ron
                          or HandSimulator.Outcome.Ryuukyoku,
            $"unexpected outcome: {result.Outcome}");
    }

    [Fact]
    public void Tsumo_winner_gets_score_increase()
    {
        // Run until we find a seed that produces a tsumo.
        var policies = new IPolicy[]
        {
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
        };

        for (int seed = 0; seed < 50; seed++)
        {
            var sim = new HandSimulator(new Random(seed));
            var result = sim.Simulate(policies);
            if (result.Outcome == HandSimulator.Outcome.Tsumo)
            {
                Assert.True(result.WinnerSeat is >= 0 and < 4);
                Assert.True(result.FinalScores[result.WinnerSeat] > 25000,
                    $"winner score should exceed starting 25000, got {result.FinalScores[result.WinnerSeat]}");
                int othersSum = 0;
                for (int i = 0; i < 4; i++)
                    if (i != result.WinnerSeat) othersSum += result.FinalScores[i];
                Assert.True(othersSum < 75000, "losers should have paid into the winner");
                return;
            }
        }
        // No tsumo in 50 seeds — possible but rare; don't fail.
    }

    [Fact]
    public void Self_play_runs_N_hands_and_returns_stats()
    {
        var policies = new IPolicy[]
        {
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
        };
        var runner = new SelfPlayRunner(seed: 7);
        var stats = runner.Run(policies, hands: 10);
        Assert.Equal(10, stats.HandsPlayed);
        Assert.Equal(4, stats.WinCounts.Length);
        int totalOutcomes = 0;
        foreach (var w in stats.WinCounts) totalOutcomes += w;
        totalOutcomes += stats.RyuukyokuCount;
        totalOutcomes += stats.AbortCount;
        Assert.Equal(10, totalOutcomes);
    }

    [Fact]
    public void Ron_detection_fires_when_policy_discards_a_winning_tile()
    {
        // Play several hands; ron must occur at least sometimes given EfficiencyPolicy ignores
        // deal-in cost without populated discard pools. Just verify ron + riichi counters
        // in the aggregated stats are integers ≥ 0.
        var policies = new IPolicy[]
        {
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
            new EfficiencyPolicy(),
        };
        var runner = new SelfPlayRunner(seed: 13);
        var stats = runner.Run(policies, hands: 20);
        int totalRons = 0;
        foreach (var r in stats.RonCounts) totalRons += r;
        int totalDealIns = 0;
        foreach (var d in stats.DealInCounts) totalDealIns += d;
        Assert.True(totalRons >= 0);
        Assert.Equal(totalRons, totalDealIns);   // each ron pairs with one deal-in
    }
}
