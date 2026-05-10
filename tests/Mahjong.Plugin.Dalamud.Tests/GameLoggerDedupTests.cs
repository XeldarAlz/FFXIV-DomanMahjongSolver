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
    private static StateSnapshot SampleSnap(int wallRemaining)
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
        return StateSnapshot.Empty with
        {
            WallRemaining = wallRemaining,
            Seats = seats,
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
