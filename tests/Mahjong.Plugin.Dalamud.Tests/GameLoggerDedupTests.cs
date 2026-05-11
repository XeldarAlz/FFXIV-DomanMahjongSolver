using Mahjong.Core;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Regression test for the StateAggregator → GameLogger dedup path. Pre-fix
/// (cc2b07c on 2026-05-08), <see cref="StateAggregator"/> fired one
/// <c>Changed</c> per framework tick the addon read cleanly, and GameLogger
/// emitted one NDJSON line for each — producing 17,361 identical state lines
/// for a single hand in the field corpus. The fix added content-hash
/// dedup at <c>OnStateChanged</c>; this test pins it.
/// </summary>
public class GameLoggerDedupTests
{
    private static readonly int[] StartScores = [25000, 25000, 25000, 25000];

    private static StateSnapshot SampleSnap(int wallRemaining, int handCount = 0, int[]? scores = null)
    {
        var seats = new SeatView[4];
        for (int i = 0; i < 4; i++)
            seats[i] = new SeatView(
                Discards: Array.Empty<Tile>(),
                DiscardIsTedashi: Array.Empty<bool>(),
                Melds: Array.Empty<Meld>(),
                Riichi: false,
                RiichiDiscardIndex: -1,
                Ippatsu: false,
                IsTenpaiCalled: false);
        var hand = new Tile[handCount];
        for (int i = 0; i < handCount; i++)
            hand[i] = Tile.FromId(i % Tile.Count34);
        return StateSnapshot.Empty with
        {
            WallRemaining = wallRemaining,
            Seats = seats,
            Hand = hand,
            Scores = scores ?? StartScores,
        };
    }

    [Fact]
    public void Identical_snapshots_collapse_to_a_single_state_line()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        var snap = SampleSnap(70);
        for (int i = 0; i < 1000; i++)
            logger.OnStateChanged(snap);

        var files = Directory.GetFiles(logger.GamesDir, "game-*.ndjson");
        Assert.Single(files);
        var lines = File.ReadAllLines(files[0]);
        // First line is the hand-start emitted by MaybeRollHand on the first
        // call; the second is the one deduped state event. Subsequent 999
        // calls hit the content-hash early-return and write nothing.
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"e\":\"hand-start\"", lines[0]);
        Assert.Contains("\"e\":\"state\"", lines[1]);
    }

    [Fact]
    public void Distinct_snapshots_emit_distinct_state_lines()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        // Five different walls within the same hand (no +5 jump → no roll).
        // Walls go DOWN inside a hand; only an upward jump rolls a new file.
        for (int w = 70; w >= 66; w--)
            logger.OnStateChanged(SampleSnap(w));

        var files = Directory.GetFiles(logger.GamesDir, "game-*.ndjson");
        Assert.Single(files);
        var lines = File.ReadAllLines(files[0]);
        // 1 hand-start + 5 state lines.
        Assert.Equal(6, lines.Length);
    }

    /// <summary>
    /// Regression for the 2026-05-11 restart-loop cluster. With a mid-hand
    /// closed hand (here: 6 tiles, the post-pon shape), an upward wall jump is
    /// a transient — not a genuine deal. The hand-count guard in MaybeRollHand
    /// must reject these so a single user session doesn't produce dozens of
    /// truncated hand files (66/100 in the original incident).
    /// </summary>
    [Fact]
    public void Wall_jump_with_mid_hand_count_does_not_roll_new_file()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        // 1. First snapshot rolls a file (currentPath null bypasses the guard).
        //    Hand is fresh (14 tiles, dealer shape) — realistic and allowed.
        logger.OnStateChanged(SampleSnap(70, handCount: 14));
        // 2. Wall ticks down naturally as the hand progresses. No roll.
        logger.OnStateChanged(SampleSnap(40, handCount: 14));
        // 3. The 2026-05-11 cluster: an upward jump of +6 (34 → 40 violates
        //    the +5 tolerance) arrives while the closed hand is at 6 tiles —
        //    a post-pon shape. Pre-fix this rolled a fresh hand file; the
        //    guard must reject it.
        logger.OnStateChanged(SampleSnap(34, handCount: 6));
        logger.OnStateChanged(SampleSnap(40, handCount: 6));
        // 4. A genuine new deal arrives with a fresh 14-tile hand. This must
        //    roll a second file.
        logger.OnStateChanged(SampleSnap(70, handCount: 14));

        var files = Directory.GetFiles(logger.GamesDir, "game-*.ndjson");
        Assert.Equal(2, files.Length);
    }

    /// <summary>
    /// Each closed hand file ends with a hand-end carrying the cumulative score
    /// delta from that file's hand-start. Without this the corpus has decisions
    /// but no reward signal; policy training has nothing to score against.
    /// </summary>
    [Fact]
    public void Hand_roll_emits_hand_end_with_score_delta_to_previous_file()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        // Hand 1: tsumo by seat 0. Player gains 8000 from 3 losers paying.
        logger.OnStateChanged(SampleSnap(70, handCount: 14, scores: [25000, 25000, 25000, 25000]));
        logger.OnStateChanged(SampleSnap(40, handCount: 14, scores: [25000, 25000, 25000, 25000]));
        // Hand 2 deal — the prior hand's resolution is reflected in the new scores.
        logger.OnStateChanged(SampleSnap(70, handCount: 14, scores: [33000, 23000, 21000, 23000]));

        var files = Directory.GetFiles(logger.GamesDir, "game-*.ndjson").OrderBy(p => p).ToArray();
        Assert.Equal(2, files.Length);

        var hand1 = File.ReadAllLines(files[0]);
        // The closing event of hand 1 is the hand-end we emitted.
        Assert.Contains("\"e\":\"hand-end\"", hand1[^1]);
        Assert.Contains("\"kind\":\"tsumo\"", hand1[^1]);
        Assert.Contains("\"winner\":0", hand1[^1]);
        Assert.Contains("\"deltas\":[8000,-2000,-4000,-2000]", hand1[^1]);
        Assert.Contains("\"scores_after\":[33000,23000,21000,23000]", hand1[^1]);

        // Hand 2 file starts cleanly with hand-start; no leaked hand-end.
        var hand2 = File.ReadAllLines(files[1]);
        Assert.Contains("\"e\":\"hand-start\"", hand2[0]);
        Assert.DoesNotContain(hand2, l => l.Contains("\"e\":\"hand-end\""));
    }

    [Theory]
    // tsumo: 1 winner, 3 losers.
    [InlineData(new[] { 8000, -2000, -4000, -2000 }, "tsumo", 0,    (object?)null)]
    // ron: 1 winner takes from a single discarder; two seats untouched.
    [InlineData(new[] { 0, 5200, 0, -5200 },        "ron",   1,    3)]
    // exhaustive draw, 2 tenpai vs 2 noten: 1500 each way.
    [InlineData(new[] { 1500, -1500, 1500, -1500 }, "draw",  (object?)null, (object?)null)]
    // no-change draw (abortive / chombo edge cases): all zero.
    [InlineData(new[] { 0, 0, 0, 0 },                "draw",  (object?)null, (object?)null)]
    public void InferResultKind_classifies_delta_shapes(int[] deltas, string expectedKind, object? expectedWinner, object? expectedLoser)
    {
        var (kind, winner, loser) = GameLogger.InferResultKind(deltas);
        Assert.Equal(expectedKind, kind);
        Assert.Equal((int?)expectedWinner, winner);
        Assert.Equal((int?)expectedLoser, loser);
    }

    [Fact]
    public void EnableGameLogging_off_drops_all_writes()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration { EnableGameLogging = false });
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        for (int i = 0; i < 50; i++)
            logger.OnStateChanged(SampleSnap(70 - i));

        var files = Directory.GetFiles(logger.GamesDir, "game-*.ndjson");
        Assert.Empty(files);
    }
}
