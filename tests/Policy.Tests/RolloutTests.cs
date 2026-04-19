using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Mcts;
using DomanMahjongAI.Policy.Opponents;
using System;
using Xunit;

namespace DomanMahjongAI.Policy.Tests;

public class RolloutTests
{
    [Fact]
    public void Rollout_completes_and_returns_finite_leaf_value()
    {
        var snap = Snapshots.Closed14("123456789m1234p5p");   // 14 tiles
        // Drop one → 13-tile post-discard state
        var hand13 = new Tile[13];
        for (int i = 0; i < 13; i++) hand13[i] = snap.Hand[i];
        var afterDiscard = snap with { Hand = hand13 };

        var rollout = new Rollout(new Random(42), maxDepth: 3, simulateOpponents: true);
        var model = new OpponentModel();
        model.Update(afterDiscard);
        double value = rollout.Run(afterDiscard, model);

        Assert.True(value > double.NegativeInfinity);
    }

    [Fact]
    public void Rollout_without_opponent_simulation_runs_too()
    {
        var snap = Snapshots.Closed14("123456789m1234p5p");
        var hand13 = new Tile[13];
        for (int i = 0; i < 13; i++) hand13[i] = snap.Hand[i];
        var afterDiscard = snap with { Hand = hand13 };

        var rollout = new Rollout(new Random(1), maxDepth: 3, simulateOpponents: false);
        var model = new OpponentModel();
        double value = rollout.Run(afterDiscard, model);

        Assert.True(value > double.NegativeInfinity);
    }
}
