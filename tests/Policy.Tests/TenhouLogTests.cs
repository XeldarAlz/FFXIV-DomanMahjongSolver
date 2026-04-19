using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Tuning;
using System.Text.Json;
using Xunit;

namespace DomanMahjongAI.Policy.Tests;

public class TenhouLogTests
{
    [Fact]
    public void From136_maps_each_quad_to_same_34_id()
    {
        // man copies: 0,1,2,3 → all 1m (id 0)
        Assert.Equal(0, TenhouLog.From136(0).Id);
        Assert.Equal(0, TenhouLog.From136(3).Id);

        // 36..39 → 1p (id 9)
        Assert.Equal(9, TenhouLog.From136(36).Id);
        Assert.Equal(9, TenhouLog.From136(39).Id);

        // 72..75 → 1s (id 18)
        Assert.Equal(18, TenhouLog.From136(72).Id);

        // 108..111 → East wind (id 27)
        Assert.Equal(27, TenhouLog.From136(108).Id);

        // 132..135 → chun (id 33)
        Assert.Equal(33, TenhouLog.From136(135).Id);
    }

    [Fact]
    public void IsRed5_detects_aka_dora_slots()
    {
        Assert.True(TenhouLog.IsRed5(16));   // 5m copy 0
        Assert.True(TenhouLog.IsRed5(52));   // 5p copy 0
        Assert.True(TenhouLog.IsRed5(88));   // 5s copy 0
        Assert.False(TenhouLog.IsRed5(17));  // 5m copy 1
    }

    [Fact]
    public void ParseEventTag_extracts_kind_and_tile_id()
    {
        var riichi = typeof(TenhouLog).GetMethod("ParseEventTag",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var resR = ((TenhouLog.EventKind kind, int tileId))riichi.Invoke(null, new object[] { "r60" })!;
        Assert.Equal(TenhouLog.EventKind.Riichi, resR.kind);
        Assert.Equal(15, resR.tileId);  // 60/4 = 15 (7p)

        var resC = ((TenhouLog.EventKind kind, int tileId))riichi.Invoke(null, new object[] { "c12" })!;
        Assert.Equal(TenhouLog.EventKind.Chi, resC.kind);

        var resEmpty = ((TenhouLog.EventKind kind, int tileId))riichi.Invoke(null, new object[] { "" })!;
        Assert.Equal(-1, resEmpty.tileId);
    }

    [Fact]
    public void ParseKyoku_reads_starting_state_from_minimal_log()
    {
        // Minimal synthetic kyoku: round 0 (E1), dealer=0, honba=0, no sticks.
        // Starting hands: seat 0 gets 13 1m-variants; seats 1-3 get dummy honors.
        string json = """
{
  "log": [
    [
      [0, 0, 0],
      [25000, 25000, 25000, 25000],
      [16],
      [],
      [0,1,2,3,4,5,6,7,8,9,10,11,12],
      [],
      [],
      [36,37,38,39,40,41,42,43,44,45,46,47,48],
      [],
      [],
      [72,73,74,75,76,77,78,79,80,81,82,83,84],
      [],
      [],
      [108,109,110,111,112,113,114,115,116,117,118,119,120],
      [],
      []
    ]
  ]
}
""";
        var kyokus = TenhouLog.ParseDocument(json);
        Assert.Single(kyokus);
        var k = kyokus[0];
        Assert.Equal(0, k.Round);
        Assert.Equal(0, k.Dealer);
        Assert.Equal(0, k.Honba);
        Assert.Equal(new[] { 25000, 25000, 25000, 25000 }, k.StartScores);
        Assert.Single(k.DoraIndicators);
        Assert.Equal(4, k.StartingHands.Length);
        Assert.Equal(13, k.StartingHands[0].Length);

        // Seat 0 held ids 0..12 → 4 copies of 1m..3m, 1 of 4m = 13 tiles.
        Assert.Equal(0, k.StartingHands[0][0].Id);
        Assert.Equal(3, k.StartingHands[0][12].Id);   // id 12 → 4m (12/4 = 3)
    }
}
