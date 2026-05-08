using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Game;
using Mahjong.Policy;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>
/// Per-hand NDJSON game logger. Subscribes to <see cref="StateAggregator.Changed"/>
/// and emits one JSON line per *changed* state plus one per dispatched action.
/// Feeds the Doman-specific training corpus — every game played produces
/// labeled (state, decision, action) episodes that later back supervised
/// policy training.
///
/// Output: <c>%AppData%\XIVLauncher\pluginConfigs\Mahjong.Plugin.Dalamud\games\game-YYYYMMDD-HHMMSS-handNN.ndjson</c>.
///
/// Event types (<c>"e"</c> field):
/// <list type="bullet">
///   <item><c>hand-start</c> — first event in a file; seat/wind/dealer context.</item>
///   <item><c>state</c>      — snapshot diff from the aggregator.</item>
///   <item><c>decision</c>   — policy.Choose() output paired with the state above.</item>
///   <item><c>action</c>     — our dispatch (discard/call/riichi/tsumo...).</item>
/// </list>
///
/// <para><b>Hash-dedup:</b> StateAggregator.Changed fires every framework tick the
/// addon reads cleanly, even when nothing about the game state actually moved
/// — first observed in the 2026-05-08 corpus where one turn produced 1268
/// identical state lines. We compute a structural hash of every snapshot and
/// skip writes whose hash matches the previous frame. Only the timestamp
/// would have differed; nothing is lost.</para>
///
/// <para>Schema version bumps invalidate old parsers. Keep field names short —
/// at ~500 B/tick across many games, every byte counts.</para>
/// </summary>
public sealed class GameLogger : IDisposable
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly StateAggregator aggregator;
    private readonly IConfigService<Configuration> configService;
    private readonly IPluginLog log;
    private readonly string gamesDir;
    private readonly object writerLock = new();
    private readonly Func<IPolicy>? policyAccessor;

    private StreamWriter? writer;
    private string? currentPath;
    private int handSeq;
    private int lastWall = -1;
    private int? lastStateHash;
    private bool disposed;

    public string? CurrentPath => currentPath;
    public int HandSeq => handSeq;
    public string GamesDir => gamesDir;

    public GameLogger(
        StateAggregator aggregator,
        IConfigService<Configuration> configService,
        IPluginLog log,
        string pluginConfigDir,
        Func<IPolicy>? policyAccessor = null)
    {
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDir);
        this.aggregator = aggregator;
        this.configService = configService;
        this.log = log;
        this.policyAccessor = policyAccessor;
        gamesDir = Path.Combine(pluginConfigDir, "games");
        Directory.CreateDirectory(gamesDir);
        aggregator.Changed += OnStateChanged;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        aggregator.Changed -= OnStateChanged;
        CloseWriter();
    }

    private void OnStateChanged(StateSnapshot snap)
    {
        if (!configService.Current.EnableGameLogging)
            return;

        // Skip ticks where nothing semantically changed. StateAggregator.Changed
        // fires per-frame even when a re-read of the addon produces a byte-for-
        // byte identical snapshot; without this guard one turn produces ~1200
        // duplicate lines.
        int hash = ComputeContentHash(snap);
        if (lastStateHash == hash)
            return;
        lastStateHash = hash;

        try
        {
            MaybeRollHand(snap);
            WriteLine(JsonSerializer.Serialize(BuildStateEvent(snap), JsonOpts));
            MaybeRecordDecision(snap);
        }
        catch (Exception ex)
        {
            log.Error($"GameLogger state-write error: {ex.Message}");
        }
    }

    /// <summary>
    /// Pair each state with the policy's verdict for that exact state. The
    /// snap-hash dedup above ensures we only call Choose when the game state
    /// actually changed; this keeps decision logging cheap (≤ a few calls per
    /// turn) and the corpus contains one (state, decision) pair per moment
    /// that mattered. Without this, suggestion-vs-highlight bugs are invisible
    /// once a session ends — the policy's output isn't otherwise persisted.
    /// </summary>
    private void MaybeRecordDecision(StateSnapshot snap)
    {
        if (policyAccessor is null)
            return;
        // No actionable legal flag = nothing to decide (e.g. AI's turn). The
        // policy still returns Pass with a default reason; logging that for
        // every state diff would just be noise.
        if (snap.Legal.Flags == ActionFlags.None)
            return;
        ActionChoice choice;
        try
        { choice = policyAccessor().Choose(snap); }
        catch (Exception ex)
        {
            log.Error($"GameLogger decision-eval error: {ex.Message}");
            return;
        }
        try
        { WriteLine(JsonSerializer.Serialize(BuildDecisionEvent(choice), JsonOpts)); }
        catch (Exception ex)
        {
            log.Error($"GameLogger decision-write error: {ex.Message}");
        }
    }

    /// <summary>
    /// Record an action we dispatched. Called from <see cref="AutoPlayLoop"/>
    /// right after an <see cref="InputDispatcher"/> call returns. Fire-and-forget;
    /// errors are swallowed to the plugin log so a logging fault can never break
    /// gameplay.
    /// </summary>
    public void RecordAction(ActionKind kind, Tile? tile, int? slot, string result, string reasoning)
    {
        if (!configService.Current.EnableGameLogging || disposed)
            return;
        try
        {
            var evt = new ActionEvent(
                T: Now(),
                E: "action",
                Kind: kind.ToString(),
                Tile: tile?.Id,
                Slot: slot,
                Result: result,
                Why: string.IsNullOrEmpty(reasoning) ? null : reasoning);
            WriteLine(JsonSerializer.Serialize(evt, JsonOpts));
        }
        catch (Exception ex)
        {
            log.Error($"GameLogger action-write error: {ex.Message}");
        }
    }

    /// <summary>
    /// New-hand detection: inside a hand the wall only decreases. A sharp upward
    /// jump (+5 tolerance to ride over transient read glitches) means a fresh
    /// deal. First snapshot after construction also rolls a file.
    /// </summary>
    private void MaybeRollHand(StateSnapshot snap)
    {
        bool isNewHand = writer is null || snap.WallRemaining > lastWall + 5;
        lastWall = snap.WallRemaining;
        if (!isNewHand)
            return;

        RollWriter();
        var start = new HandStartEvent(
            T: Now(),
            E: "hand-start",
            V: SchemaVersion,
            Seat: snap.OurSeat,
            RoundWind: snap.RoundWind,
            Dealer: snap.DealerSeat,
            Honba: snap.Honba,
            RiichiSticks: snap.RiichiSticks,
            Scores: snap.Scores.ToArray());
        WriteLine(JsonSerializer.Serialize(start, JsonOpts));
    }

    private void RollWriter()
    {
        lock (writerLock)
        {
            CloseWriterLocked();
            handSeq++;
            var fn = $"game-{DateTime.UtcNow:yyyyMMdd-HHmmss}-hand{handSeq:D2}.ndjson";
            currentPath = Path.Combine(gamesDir, fn);
            writer = new StreamWriter(new FileStream(
                currentPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
        }
    }

    private void CloseWriter()
    {
        lock (writerLock)
            CloseWriterLocked();
    }

    private void CloseWriterLocked()
    {
        writer?.Flush();
        writer?.Dispose();
        writer = null;
    }

    private void WriteLine(string line)
    {
        lock (writerLock)
        { writer?.WriteLine(line); }
    }

    /// <summary>
    /// Structural hash over every snapshot field that the corpus cares about —
    /// turn, wall, our hand, melds, dora, riichi/ippatsu flags, legal action
    /// set, scores, and every seat's discard pile + meld set + riichi flags.
    /// Excludes the snapshot timestamp; only content drives equality.
    /// </summary>
    private static int ComputeContentHash(StateSnapshot snap)
    {
        var h = new HashCode();
        h.Add(snap.WallRemaining);
        h.Add(snap.TurnIndex);
        h.Add((int)snap.Legal.Flags);
        h.Add(snap.OurRiichi);
        h.Add(snap.OurIppatsu);
        h.Add(snap.OurSeat);
        h.Add(snap.RoundWind);
        h.Add(snap.DealerSeat);
        h.Add(snap.Honba);
        h.Add(snap.RiichiSticks);
        foreach (var t in snap.Hand)
            h.Add(t.Id);
        foreach (var m in snap.OurMelds)
        {
            h.Add((int)m.Kind);
            foreach (var t in m.Tiles)
                h.Add(t.Id);
        }
        foreach (var t in snap.DoraIndicators)
            h.Add(t.Id);
        foreach (var s in snap.Scores)
            h.Add(s);
        foreach (var s in snap.Seats)
        {
            h.Add(s.DiscardCount);
            foreach (var t in s.Discards)
                h.Add(t.Id);
            foreach (var m in s.Melds)
            {
                h.Add((int)m.Kind);
                foreach (var t in m.Tiles)
                    h.Add(t.Id);
            }
            h.Add(s.Riichi);
            h.Add(s.RiichiDiscardIndex);
            h.Add(s.Ippatsu);
        }
        return h.ToHashCode();
    }

    private static DecisionEvent BuildDecisionEvent(ActionChoice choice) => new(
        T: Now(),
        E: "decision",
        Kind: choice.Kind.ToString(),
        Tile: choice.DiscardTile?.Id,
        CallKind: choice.Call?.Kind.ToString(),
        Why: string.IsNullOrEmpty(choice.Reasoning) ? null : choice.Reasoning,
        Steps: choice.Steps is { Count: > 0 } steps
            ? steps.Select(r => new StepDto(K: r.Code, D: r.Display)).ToArray()
            : null);

    private static StateEvent BuildStateEvent(StateSnapshot snap) => new(
        T: Now(),
        E: "state",
        Wall: snap.WallRemaining,
        Turn: snap.TurnIndex,
        Hand: snap.Hand.Select(t => (int)t.Id).ToArray(),
        OurMelds: snap.OurMelds.Select(ToMeldDto).ToArray(),
        Dora: snap.DoraIndicators.Select(t => (int)t.Id).ToArray(),
        OurRiichi: snap.OurRiichi,
        OurIppatsu: snap.OurIppatsu,
        Legal: snap.Legal.Flags.ToString(),
        Scores: snap.Scores.ToArray(),
        Seats: snap.Seats.Select(ToSeatDto).ToArray());

    private static SeatDto ToSeatDto(SeatView s) => new(
        Dc: s.DiscardCount,
        D: s.Discards.Select(t => (int)t.Id).ToArray(),
        M: s.Melds.Select(ToMeldDto).ToArray(),
        R: s.Riichi,
        Ri: s.RiichiDiscardIndex,
        Ip: s.Ippatsu);

    private static MeldDto ToMeldDto(Meld m) => new(
        K: m.Kind.ToString(),
        T: m.Tiles.Select(t => (int)t.Id).ToArray(),
        C: m.ClaimedTile?.Id,
        Fs: m.ClaimedFromSeat);

    private static string Now() =>
        DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    // ---- DTO records ----

    private sealed record HandStartEvent(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("seat")] int Seat,
        [property: JsonPropertyName("round_wind")] int RoundWind,
        [property: JsonPropertyName("dealer")] int Dealer,
        [property: JsonPropertyName("honba")] int Honba,
        [property: JsonPropertyName("riichi_sticks")] int RiichiSticks,
        [property: JsonPropertyName("scores")] int[] Scores);

    private sealed record StateEvent(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("wall")] int Wall,
        [property: JsonPropertyName("turn")] int Turn,
        [property: JsonPropertyName("hand")] int[] Hand,
        [property: JsonPropertyName("our_melds")] MeldDto[] OurMelds,
        [property: JsonPropertyName("dora")] int[] Dora,
        [property: JsonPropertyName("our_riichi")] bool OurRiichi,
        [property: JsonPropertyName("our_ippatsu")] bool OurIppatsu,
        [property: JsonPropertyName("legal")] string Legal,
        [property: JsonPropertyName("scores")] int[] Scores,
        [property: JsonPropertyName("seats")] SeatDto[] Seats);

    private sealed record ActionEvent(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("tile")] int? Tile,
        [property: JsonPropertyName("slot")] int? Slot,
        [property: JsonPropertyName("result")] string Result,
        [property: JsonPropertyName("why")] string? Why);

    private sealed record DecisionEvent(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("tile")] int? Tile,
        [property: JsonPropertyName("call_kind")] string? CallKind,
        [property: JsonPropertyName("why")] string? Why,
        [property: JsonPropertyName("steps")] StepDto[]? Steps);

    private sealed record StepDto(
        [property: JsonPropertyName("k")] string K,
        [property: JsonPropertyName("d")] string D);

    private sealed record SeatDto(
        [property: JsonPropertyName("dc")] int Dc,
        [property: JsonPropertyName("d")] int[] D,
        [property: JsonPropertyName("m")] MeldDto[] M,
        [property: JsonPropertyName("r")] bool R,
        [property: JsonPropertyName("ri")] int Ri,
        [property: JsonPropertyName("ip")] bool Ip);

    private sealed record MeldDto(
        [property: JsonPropertyName("k")] string K,
        [property: JsonPropertyName("t")] int[] T,
        [property: JsonPropertyName("c")] int? C,
        [property: JsonPropertyName("fs")] int Fs);
}
