using System;

namespace DomanMahjongAI.Policy.Simulator;

/// <summary>
/// Play N hands with the given 4 policies, collect aggregate metrics. Useful for
/// comparing EfficiencyPolicy vs IsmctsPolicy, or self-play tournaments for weight
/// tuning (M9).
/// </summary>
public sealed class SelfPlayRunner
{
    private readonly Random rng;
    public SelfPlayRunner(int? seed = null)
    {
        rng = seed is null ? new Random() : new Random(seed.Value);
    }

    public readonly record struct Stats(
        int HandsPlayed,
        int[] WinCounts,         // per seat (tsumo + ron)
        int[] TsumoCounts,       // per seat
        int[] RonCounts,         // per seat (wins by ron)
        int[] DealInCounts,      // per seat (times they were the ron target)
        int[] RiichiCounts,      // per seat (times declared riichi)
        int RyuukyokuCount,
        int AbortCount,
        long[] TotalScoreDelta,  // net score change per seat across all hands
        int AverageTurnCount);

    public Stats Run(IPolicy[] policies, int hands = 100, int dealer = 0)
    {
        if (policies.Length != 4) throw new ArgumentException("need 4 policies");

        var wins = new int[4];
        var tsumoWins = new int[4];
        var ronWins = new int[4];
        var dealIns = new int[4];
        var riichis = new int[4];
        var ryuu = 0;
        var aborted = 0;
        var totalDelta = new long[4];
        int totalTurns = 0;
        var baseScores = new int[] { 25000, 25000, 25000, 25000 };

        var sim = new HandSimulator(rng);
        for (int i = 0; i < hands; i++)
        {
            var result = sim.Simulate(policies, dealer: dealer);
            totalTurns += result.TurnCount;

            for (int s = 0; s < 4; s++) riichis[s] += result.RiichiDeclared[s];

            switch (result.Outcome)
            {
                case HandSimulator.Outcome.Tsumo:
                    wins[result.WinnerSeat]++;
                    tsumoWins[result.WinnerSeat]++;
                    for (int s = 0; s < 4; s++)
                        totalDelta[s] += result.FinalScores[s] - baseScores[s];
                    break;
                case HandSimulator.Outcome.Ron:
                    wins[result.WinnerSeat]++;
                    ronWins[result.WinnerSeat]++;
                    if (result.LoserSeat >= 0) dealIns[result.LoserSeat]++;
                    for (int s = 0; s < 4; s++)
                        totalDelta[s] += result.FinalScores[s] - baseScores[s];
                    break;
                case HandSimulator.Outcome.Ryuukyoku:
                    ryuu++;
                    break;
                default:
                    aborted++;
                    break;
            }
        }

        return new Stats(
            HandsPlayed: hands,
            WinCounts: wins,
            TsumoCounts: tsumoWins,
            RonCounts: ronWins,
            DealInCounts: dealIns,
            RiichiCounts: riichis,
            RyuukyokuCount: ryuu,
            AbortCount: aborted,
            TotalScoreDelta: totalDelta,
            AverageTurnCount: hands > 0 ? totalTurns / hands : 0);
    }
}
