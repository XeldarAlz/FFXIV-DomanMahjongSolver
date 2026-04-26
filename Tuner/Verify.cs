using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Tuning;

namespace DomanMahjongAI.Tuner;

/// <summary>
/// Sanity check: head-to-head match between two weight sets at high hand counts
/// to confirm whether a tuner result is real or noise.
/// </summary>
public static class Verify
{
    public static int RunVerify(string[] args)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;

        // Optional baseline-mode flag: `verify orig 2000 9999` compares the coord
        // weights against the original hand-picked defaults (pre-tuning) instead
        // of the current Weights.Default.
        bool vsOriginal = args.Length > 0 && args[0] == "orig";
        if (vsOriginal) args = args.Skip(1).ToArray();

        var coordTuned = new DiscardScorer.Weights(
            Shanten: 100.0,
            UkeireKinds: 0.1954,
            UkeireWeighted: 0.5027,
            Dora: 36.9499,
            Yakuhai: 19.0784,
            IsolatedTerminal: 54.5092,
            DealInCost: 0.019662);

        var originalHandPicked = new DiscardScorer.Weights(
            Shanten: 100.0, UkeireKinds: 2.0, UkeireWeighted: 1.0,
            Dora: 4.0, Yakuhai: 2.0, IsolatedTerminal: 0.5, DealInCost: 0.001);

        var tuned = coordTuned;
        var baseline = vsOriginal ? originalHandPicked : DiscardScorer.Weights.Default;
        int hands = args.Length > 0 && int.TryParse(args[0], out var h) ? h : 500;
        int seed = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 1234;

        var vs = vsOriginal ? "original-handpicked" : "current-default";
        Console.WriteLine($"verify: coord-tuned vs {vs}, {hands} hands, seed={seed}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = WeightTuner.Evaluate(tuned, baseline, hands, seed);
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"  candidate (tuned) net delta:  {result.CandidateScoreDelta,+10}  wins={result.CandidateWins}");
        Console.WriteLine($"  baseline (default) net delta: {result.BaselineScoreDelta,+10}  wins={result.BaselineWins}");
        Console.WriteLine($"  ryuukyoku={result.Ryuukyoku}  aborts={result.Aborts}");
        Console.WriteLine();
        long net = result.CandidateScoreDelta - result.BaselineScoreDelta;
        if (net > 0) Console.WriteLine($"  TUNED WINS by {net} pts ({net / (double)hands:F1}/hand)");
        else if (net < 0) Console.WriteLine($"  DEFAULT WINS by {-net} pts ({-net / (double)hands:F1}/hand)");
        else Console.WriteLine($"  TIE");
        Console.WriteLine($"  ({sw.Elapsed.TotalSeconds:F1}s)");
        return 0;
    }
}
