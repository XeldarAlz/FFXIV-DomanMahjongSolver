using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Simulator;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DomanMahjongAI.Policy.Tuning;

/// <summary>
/// Coordinate-descent hill climbing for DiscardScorer weights. Each generation
/// perturbs one weight at a time (×factor, ÷factor) and keeps the variant with
/// the best net score delta against the incumbent. Intended for offline runs —
/// not fast, not provably optimal. CMA-ES or replay-based supervision is the
/// next step.
/// </summary>
public sealed class WeightTuner
{
    public sealed record Settings(
        int HandsPerEvaluation = 100,
        int Iterations = 20,
        double PerturbFactor = 1.3,
        int Seed = 1)
    {
        public static Settings Default => new();
    }

    public readonly record struct EvaluationResult(
        long CandidateScoreDelta,
        long BaselineScoreDelta,
        int CandidateWins,
        int BaselineWins,
        int Ryuukyoku,
        int Aborts);

    public readonly record struct TuningStep(
        string Field,
        double OldValue,
        double NewValue,
        long ScoreDelta,
        int Iteration);

    public readonly record struct TuningRun(
        DiscardScorer.Weights StartingWeights,
        DiscardScorer.Weights FinalWeights,
        List<TuningStep> Steps);

    /// <summary>
    /// Head-to-head match between two weight sets. Candidate plays seats 0/2,
    /// baseline plays seats 1/3. Returns net score delta for the candidates.
    /// </summary>
    public static EvaluationResult Evaluate(
        DiscardScorer.Weights candidate,
        DiscardScorer.Weights baseline,
        int hands,
        int seed)
    {
        var policies = new IPolicy[]
        {
            new EfficiencyPolicy(candidate),
            new EfficiencyPolicy(baseline),
            new EfficiencyPolicy(candidate),
            new EfficiencyPolicy(baseline),
        };
        var runner = new SelfPlayRunner(seed);
        var stats = runner.Run(policies, hands);

        long candDelta = stats.TotalScoreDelta[0] + stats.TotalScoreDelta[2];
        long baseDelta = stats.TotalScoreDelta[1] + stats.TotalScoreDelta[3];
        int candWins = stats.WinCounts[0] + stats.WinCounts[2];
        int baseWins = stats.WinCounts[1] + stats.WinCounts[3];
        return new EvaluationResult(
            candDelta, baseDelta, candWins, baseWins,
            stats.RyuukyokuCount, stats.AbortCount);
    }

    public TuningRun Tune(DiscardScorer.Weights start, Settings? settings = null)
    {
        var s = settings ?? Settings.Default;
        var rng = new Random(s.Seed);
        var current = start;
        var steps = new List<TuningStep>();

        string[] fields = { "UkeireKinds", "UkeireWeighted", "Dora", "Yakuhai", "IsolatedTerminal", "DealInCost" };

        for (int iter = 0; iter < s.Iterations; iter++)
        {
            // Pick one field to perturb this iteration.
            string field = fields[iter % fields.Length];
            double oldVal = GetField(current, field);

            // Try ×factor and ÷factor; keep the best (if either beats current).
            var up = SetField(current, field, oldVal * s.PerturbFactor);
            var down = SetField(current, field, oldVal / s.PerturbFactor);

            int evalSeed = rng.Next();
            var upEval = Evaluate(up, current, s.HandsPerEvaluation, evalSeed);
            var downEval = Evaluate(down, current, s.HandsPerEvaluation, evalSeed + 1);

            long upDelta = upEval.CandidateScoreDelta - upEval.BaselineScoreDelta;
            long downDelta = downEval.CandidateScoreDelta - downEval.BaselineScoreDelta;

            if (upDelta > 0 && upDelta >= downDelta)
            {
                current = up;
                steps.Add(new TuningStep(field, oldVal, oldVal * s.PerturbFactor, upDelta, iter));
            }
            else if (downDelta > 0)
            {
                current = down;
                steps.Add(new TuningStep(field, oldVal, oldVal / s.PerturbFactor, downDelta, iter));
            }
        }

        return new TuningRun(start, current, steps);
    }

    private static double GetField(DiscardScorer.Weights w, string field) => field switch
    {
        "Shanten" => w.Shanten,
        "UkeireKinds" => w.UkeireKinds,
        "UkeireWeighted" => w.UkeireWeighted,
        "Dora" => w.Dora,
        "Yakuhai" => w.Yakuhai,
        "IsolatedTerminal" => w.IsolatedTerminal,
        "DealInCost" => w.DealInCost,
        _ => throw new ArgumentException($"unknown field: {field}"),
    };

    private static DiscardScorer.Weights SetField(DiscardScorer.Weights w, string field, double value) => field switch
    {
        "Shanten" => w with { Shanten = value },
        "UkeireKinds" => w with { UkeireKinds = value },
        "UkeireWeighted" => w with { UkeireWeighted = value },
        "Dora" => w with { Dora = value },
        "Yakuhai" => w with { Yakuhai = value },
        "IsolatedTerminal" => w with { IsolatedTerminal = value },
        "DealInCost" => w with { DealInCost = value },
        _ => throw new ArgumentException($"unknown field: {field}"),
    };
}
