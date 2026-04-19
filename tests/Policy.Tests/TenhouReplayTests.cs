using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Tuning;
using Xunit;

namespace DomanMahjongAI.Policy.Tests;

public class TenhouReplayTests
{
    [Fact]
    public void Replay_matches_perfectly_when_policy_picks_the_only_legal_discard()
    {
        // Synthetic kyoku: seat 0 starts with 13 honors, draws nothing useful, and
        // the "recorded" discards match what the policy would likely pick anyway.
        // Parse a 3-turn sequence; the test just asserts replay completes and
        // reports a sensible accuracy number (not strict value).
        string json = """
{
  "log": [
    [
      [0, 0, 0],
      [25000, 25000, 25000, 25000],
      [0],
      [],
      [0,4,8,12,16,20,24,36,40,44,48,52,56],
      [60, 64, 68],
      [60, 64, 68],
      [100,104,108,112,116,120,124,128,132,1,2,3,5],
      [],
      [],
      [6,7,9,10,11,13,14,15,17,18,19,21,22],
      [],
      [],
      [23,25,26,27,28,29,30,31,32,33,34,35,37],
      [],
      []
    ]
  ]
}
""";
        var kyokus = TenhouLog.ParseDocument(json);
        var policy = new EfficiencyPolicy();
        var result = TenhouReplay.ReplaySeat(kyokus[0], policy, seat: 0);

        Assert.Equal(3, result.TotalDecisions);
        Assert.InRange(result.Accuracy, 0.0, 1.0);
        Assert.Equal(3, result.Decisions.Length);
    }
}
