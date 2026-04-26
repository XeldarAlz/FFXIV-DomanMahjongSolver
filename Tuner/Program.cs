using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Tuning;

namespace DomanMahjongAI.Tuner;

/// <summary>
/// Offline runner for <see cref="EvolutionaryTuner"/>. Self-plays N generations
/// of head-to-head matches against the incumbent mean, prints per-generation
/// progress, and writes the final tuned <see cref="DiscardScorer.Weights"/> to
/// a file ready to drop into <c>Weights.Default</c>.
///
/// Usage:
///   dotnet run --project Tuner -c Release -- [pop=8] [generations=10] [hands=50] [seed=42]
///
/// All args optional; defaults match <see cref="EvolutionaryTuner.Settings.Default"/>.
/// Output goes to stdout and <c>Tuner/output/tuned-weights-{timestamp}.txt</c>.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // Pin formatting to invariant — the EmitWeightsRecord output below has to be
        // valid C# source, and on a machine with a comma decimal separator (e.g. de-DE)
        // "100,00" is a parse error.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

        // Sub-command dispatch.
        if (args.Length > 0 && args[0] == "verify")
            return Verify.RunVerify(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "coord")
            return RunCoord(args.Skip(1).ToArray());

        int population = ParseArg(args, 0, 8);
        int generations = ParseArg(args, 1, 10);
        int hands = ParseArg(args, 2, 50);
        int seed = ParseArg(args, 3, 42);
        // sigma is a percentage (×100) so we can pass an int. Default 30 = 0.30 (the
        // tuner default). Lower it (e.g. 15) when hands/eval is high so the search
        // doesn't drift wildly when noise is already low.
        int sigmaPct = ParseArg(args, 4, 30);

        Console.WriteLine($"Doman Mahjong weight tuner");
        Console.WriteLine($"  population={population}  generations={generations}  hands/eval={hands}  seed={seed}  sigma={sigmaPct / 100.0:F2}");
        Console.WriteLine($"  start = {DiscardScorer.Weights.Default}");
        Console.WriteLine();

        var settings = new EvolutionaryTuner.Settings(
            Population: population,
            Survivors: Math.Max(2, population / 2),
            Generations: generations,
            HandsPerEvaluation: hands,
            InitialSigma: sigmaPct / 100.0,
            Seed: seed);

        var tuner = new EvolutionaryTuner();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = tuner.Tune(DiscardScorer.Weights.Default, settings);
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"== complete in {sw.Elapsed.TotalSeconds:F1}s ==");
        Console.WriteLine();
        Console.WriteLine("per-generation incumbent mean:");
        foreach (var gen in run.Generations)
        {
            int beat = 0;
            long bestDelta = long.MinValue;
            foreach (var c in gen.Population)
            {
                if (c.NetDelta > 0) beat++;
                if (c.NetDelta > bestDelta) bestDelta = c.NetDelta;
            }
            Console.WriteLine(
                $"  gen {gen.Index,2}: best Δ={bestDelta,+8}  beat-baseline={beat}/{gen.Population.Length}  " +
                $"mean={FormatWeights(gen.IncumbentMean)}");
        }

        Console.WriteLine();
        Console.WriteLine($"FINAL weights:");
        Console.WriteLine($"  {FormatWeights(run.FinalMean)}");
        Console.WriteLine();
        Console.WriteLine($"DROP-IN replacement for DiscardScorer.Weights.Default:");
        Console.WriteLine();
        Console.WriteLine(EmitWeightsRecord(run.FinalMean));

        // Write to file for later reference.
        var outputDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "output");
        Directory.CreateDirectory(outputDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outPath = Path.Combine(outputDir, $"tuned-weights-{stamp}.txt");
        using (var w = new StreamWriter(outPath))
        {
            w.WriteLine($"# Doman Mahjong tuner run  utc={DateTime.UtcNow:o}");
            w.WriteLine($"# pop={population} gens={generations} hands={hands} seed={seed} elapsed={sw.Elapsed.TotalSeconds:F1}s");
            w.WriteLine($"# start = {FormatWeights(run.StartingMean)}");
            w.WriteLine($"# final = {FormatWeights(run.FinalMean)}");
            w.WriteLine();
            w.WriteLine(EmitWeightsRecord(run.FinalMean));
        }
        Console.WriteLine($"wrote {outPath}");

        return 0;
    }

    /// <summary>
    /// Coordinate-descent tuner: only one weight perturbed per iteration, can't
    /// drift into the degenerate "Dora=10⁹" regime that the ES tuner found.
    /// Slower per-iteration than ES (2 evaluations × N hands) but each step is
    /// monotone improvement against the previous incumbent.
    /// </summary>
    private static int RunCoord(string[] args)
    {
        int iterations = ParseArg(args, 0, 30);
        int hands = ParseArg(args, 1, 200);
        int seed = ParseArg(args, 2, 4242);
        // PerturbFactor as int×100 (e.g. 13 = 1.3) so it's CLI-friendly.
        int perturbPct = ParseArg(args, 3, 30);
        double perturbFactor = 1.0 + perturbPct / 100.0;

        Console.WriteLine("Doman Mahjong coordinate-descent tuner");
        Console.WriteLine($"  iterations={iterations}  hands/eval={hands}  seed={seed}  perturb={perturbFactor:F2}");
        Console.WriteLine($"  start = {FormatWeights(DiscardScorer.Weights.Default)}");
        Console.WriteLine();

        var settings = new WeightTuner.Settings(
            HandsPerEvaluation: hands,
            Iterations: iterations,
            PerturbFactor: perturbFactor,
            Seed: seed);

        var tuner = new WeightTuner();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = tuner.Tune(DiscardScorer.Weights.Default, settings);
        sw.Stop();

        Console.WriteLine($"== complete in {sw.Elapsed.TotalSeconds:F1}s ==");
        Console.WriteLine();
        Console.WriteLine($"accepted steps ({run.Steps.Count}/{iterations}):");
        foreach (var step in run.Steps)
        {
            Console.WriteLine(
                $"  iter {step.Iteration,2}: {step.Field,-18} {step.OldValue:F4} → {step.NewValue:F4}  Δ={step.ScoreDelta,+8}");
        }

        Console.WriteLine();
        Console.WriteLine($"FINAL weights:");
        Console.WriteLine($"  {FormatWeights(run.FinalWeights)}");
        Console.WriteLine();
        Console.WriteLine($"DROP-IN replacement for DiscardScorer.Weights.Default:");
        Console.WriteLine();
        Console.WriteLine(EmitWeightsRecord(run.FinalWeights));

        var outputDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "output");
        Directory.CreateDirectory(outputDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outPath = Path.Combine(outputDir, $"coord-weights-{stamp}.txt");
        using (var w = new StreamWriter(outPath))
        {
            w.WriteLine($"# coord-descent tuner  utc={DateTime.UtcNow:o}");
            w.WriteLine($"# iters={iterations} hands={hands} seed={seed} perturb={perturbFactor:F2} elapsed={sw.Elapsed.TotalSeconds:F1}s");
            w.WriteLine($"# start = {FormatWeights(run.StartingWeights)}");
            w.WriteLine($"# final = {FormatWeights(run.FinalWeights)}");
            w.WriteLine();
            w.WriteLine(EmitWeightsRecord(run.FinalWeights));
        }
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    private static int ParseArg(string[] args, int index, int defaultValue)
    {
        if (index >= args.Length) return defaultValue;
        return int.TryParse(args[index], out var v) ? v : defaultValue;
    }

    private static string FormatWeights(DiscardScorer.Weights w) =>
        $"Sh={w.Shanten:F2} Uk={w.UkeireKinds:F2}/{w.UkeireWeighted:F2} " +
        $"Do={w.Dora:F2} Ya={w.Yakuhai:F2} Iso={w.IsolatedTerminal:F2} Di={w.DealInCost:F4}";

    private static string EmitWeightsRecord(DiscardScorer.Weights w) =>
        $$"""
        public static Weights Default => new(
            Shanten:          {{w.Shanten:F4}},
            UkeireKinds:      {{w.UkeireKinds:F4}},
            UkeireWeighted:   {{w.UkeireWeighted:F4}},
            Dora:             {{w.Dora:F4}},
            Yakuhai:          {{w.Yakuhai:F4}},
            IsolatedTerminal: {{w.IsolatedTerminal:F4}},
            DealInCost:       {{w.DealInCost:F6}});
        """;
}
