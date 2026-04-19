using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Tuning;
using Xunit;

namespace DomanMahjongAI.Policy.Tests;

public class EvolutionaryTunerTests
{
    [Fact]
    public void Tune_runs_without_throwing_and_returns_generations()
    {
        var start = DiscardScorer.Weights.Default;
        var tuner = new EvolutionaryTuner();
        var run = tuner.Tune(start, new EvolutionaryTuner.Settings(
            Population: 4,
            Survivors: 2,
            Generations: 2,
            HandsPerEvaluation: 3,
            Seed: 1));

        Assert.Equal(start, run.StartingMean);
        Assert.Equal(2, run.Generations.Length);
        Assert.Equal(4, run.Generations[0].Population.Length);
        // Final mean always has 7 non-NaN weight components.
        Assert.False(double.IsNaN(run.FinalMean.UkeireKinds));
    }
}
