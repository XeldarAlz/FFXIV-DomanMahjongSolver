using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Tuning;
using Xunit;

namespace DomanMahjongAI.Policy.Tests;

public class WeightTunerTests
{
    [Fact]
    public void Evaluate_returns_balanced_deltas()
    {
        // Same weights on both sides → deltas should sum to approximately 0
        // (modulo ryuukyoku tenpai payments and seat luck).
        var baseline = DiscardScorer.Weights.Default;
        var result = WeightTuner.Evaluate(baseline, baseline, hands: 10, seed: 42);

        // Candidate + baseline deltas together equal the net score change, which
        // should be near 0 (riichi sticks may sit on the table → slight negative).
        long total = result.CandidateScoreDelta + result.BaselineScoreDelta;
        Assert.InRange(total, -30000, 30000);
    }

    [Fact]
    public void Tune_runs_without_throwing()
    {
        var start = DiscardScorer.Weights.Default;
        var tuner = new WeightTuner();
        var run = tuner.Tune(start, new WeightTuner.Settings(
            HandsPerEvaluation: 5,
            Iterations: 3,
            Seed: 1));

        Assert.Equal(start, run.StartingWeights);
        // Final weights exist (may or may not differ from start depending on luck).
        Assert.True(run.Steps.Count <= 3);
    }
}
