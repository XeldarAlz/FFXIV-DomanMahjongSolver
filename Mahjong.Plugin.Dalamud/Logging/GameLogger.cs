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
///   <item><c>hand-start</c>  — first event in a file; seat/wind/dealer context.</item>
///   <item><c>state</c>       — snapshot diff from the aggregator.</item>
///   <item><c>decision</c>    — policy.Choose() output paired with the state above.</item>
///   <item><c>action</c>      — our dispatch (discard/call/riichi/tsumo...).</item>
///   <item><c>call-prompt</c> — variant-side call-prompt entry: raw AtkValues
///                              window + decoded candidate tile-ids. Lets us
///                              diagnose variant-decode mismatches (e.g. the
///                              chi-claim slot in #34) from telemetry alone.</item>
///   <item><c>hand-end</c>    — final event in a file: cumulative score delta
///                              vs the file's hand-start, inferred result kind
///                              (tsumo/ron/draw), winner/loser seats. Provides
///                              the reward signal for policy training; without
///                              this the corpus has decisions but no outcomes.
///                              The very last hand of a session is not emitted
///                              (we only detect end-of-hand from the start of
///                              the next one).</item>
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
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly StateAggregator? aggregator;
    private readonly IConfigService<Configuration> configService;
    private readonly IPluginLog log;
    private readonly string gamesDir;
    private readonly object writerLock = new();
    private readonly Func<IPolicy>? policyAccessor;
    private readonly InputEventLogger? eventLogger;

    private string? currentPath;
    private int handSeq;
    private int lastWall = -1;
    private int? lastStateHash;
    private int[]? lastHandStartScores;
    private bool disposed;

    public string? CurrentPath => currentPath;
    public int HandSeq => handSeq;
    public string GamesDir => gamesDir;

    public GameLogger(
        StateAggregator aggregator,
        IConfigService<Configuration> configService,
        IPluginLog log,
        string pluginConfigDir,
        Func<IPolicy>? policyAccessor = null,
        InputEventLogger? eventLogger = null)
    {
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDir);
        this.aggregator = aggregator;
        this.configService = configService;
        this.log = log;
        this.policyAccessor = policyAccessor;
        this.eventLogger = eventLogger;
        gamesDir = Path.Combine(pluginConfigDir, "games");
        Directory.CreateDirectory(gamesDir);
        aggregator.Changed += OnStateChanged;
        if (eventLogger is not null)
            eventLogger.CallPromptObserved += OnCallPromptObserved;
    }

    /// <summary>
    /// Test-only constructor: skips the aggregator wiring so unit tests can
    /// drive <see cref="OnStateChanged"/> directly without standing up a real
    /// <see cref="StateAggregator"/> (which transitively needs an addon
    /// reader, framework, and addon-lifecycle service).
    /// </summary>
    internal GameLogger(
        IConfigService<Configuration> configService,
        IPluginLog log,
        string pluginConfigDir)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDir);
        this.aggregator = null;
        this.configService = configService;
        this.log = log;
        this.policyAccessor = null;
        this.eventLogger = null;
        gamesDir = Path.Combine(pluginConfigDir, "games");
        Directory.CreateDirectory(gamesDir);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (aggregator is not null)
            aggregator.Changed -= OnStateChanged;
        if (eventLogger is not null)
            eventLogger.CallPromptObserved -= OnCallPromptObserved;
    }

    internal void OnStateChanged(StateSnapshot snap)
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
    /// Record a call-prompt entry. Fires once per CallPrompt-state transition
    /// the variant detects (deduplicated upstream in BaseEmjVariant). Carries
    /// the raw AtkValues window the variant decoded from plus the candidate
    /// tile-ids it produced — divergence between the two is the signature of
    /// a variant-decode bug, and capturing both inline in the games stream
    /// means it's reproducible from telemetry alone.
    ///
    /// <para>If no hand file is open yet (call prompt before first deal), the
    /// write is silently dropped — there's no useful corpus context for an
    /// orphaned prompt frame, and rolling a new file just for one event would
    /// pollute the per-hand structure.</para>
    /// </summary>
    private void OnCallPromptObserved(CallPromptEvent evt)
    {
        if (disposed || !configService.Current.EnableGameLogging)
            return;
        if (currentPath is null)
            return;
        try
        {
            var dto = new CallPromptDto(
                T: evt.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                E: "call-prompt",
                Variant: evt.AddonName,
                StateCode: evt.StateCode,
                Flags: evt.Flags,
                Pon: evt.PonClaimedTileIds,
                Chi: evt.ChiClaimedTileIds,
                Kan: evt.KanClaimedTileIds,
                Av: evt.IntValues);
            WriteLine(JsonSerializer.Serialize(dto, JsonOpts));
        }
        catch (Exception ex)
        {
            log.Error($"GameLogger call-prompt-write error: {ex.Message}");
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
    ///
    /// <para>Hand-size guard: an upward wall jump that arrives while the closed
    /// hand is at a mid-hand count (e.g. 6 after a pon, 11 after a chi) is a
    /// transient read or a state-transition artifact, not a genuine deal. Roll
    /// only when the hand is at a deal-shape count: 0 (between hands), 13
    /// (non-dealer fresh), or 14 (dealer fresh / mid-turn). This catches the
    /// remaining wall-jump cases the <see cref="BaseEmjVariant.ResolveWallRemaining"/>
    /// fix could miss (e.g. addon detach/reattach with stale dc). First-roll
    /// (currentPath == null) bypasses the guard so the plugin can latch onto
    /// any addon state.</para>
    /// </summary>
    private void MaybeRollHand(StateSnapshot snap)
    {
        bool firstRoll = currentPath is null;
        bool wallJumpUp = !firstRoll && snap.WallRemaining > lastWall + 5;
        lastWall = snap.WallRemaining;
        if (!firstRoll && !wallJumpUp)
            return;
        if (wallJumpUp && snap.Hand.Count != 0 && snap.Hand.Count != 13 && snap.Hand.Count != 14)
            return;

        // Close out the previous file with a hand-end carrying the just-resolved
        // hand's cumulative score delta. Emitted to currentPath BEFORE RollWriter
        // advances it, so the event lands in the old file.
        if (!firstRoll && lastHandStartScores is not null)
            EmitHandEnd(lastHandStartScores, snap.Scores);

        RollWriter();
        var startScores = snap.Scores.ToArray();
        lastHandStartScores = startScores;
        var start = new HandStartEvent(
            T: Now(),
            E: "hand-start",
            V: SchemaVersion,
            Seat: snap.OurSeat,
            RoundWind: snap.RoundWind,
            Dealer: snap.DealerSeat,
            Honba: snap.Honba,
            RiichiSticks: snap.RiichiSticks,
            Scores: startScores);
        WriteLine(JsonSerializer.Serialize(start, JsonOpts));
    }

    private void EmitHandEnd(IReadOnlyList<int> scoresBefore, IReadOnlyList<int> scoresAfter)
    {
        int n = Math.Min(scoresBefore.Count, scoresAfter.Count);
        var deltas = new int[n];
        for (int i = 0; i < n; i++)
            deltas[i] = scoresAfter[i] - scoresBefore[i];
        var (kind, winner, loser) = InferResultKind(deltas);
        var evt = new HandEndEvent(
            T: Now(),
            E: "hand-end",
            Kind: kind,
            Winner: winner,
            Loser: loser,
            Deltas: deltas,
            ScoresAfter: scoresAfter.ToArray());
        try { WriteLine(JsonSerializer.Serialize(evt, JsonOpts)); }
        catch (Exception ex) { log.Error($"GameLogger hand-end-write error: {ex.Message}"); }
    }

    /// <summary>
    /// Best-effort classification from the per-seat delta shape. Three buckets:
    /// <list type="bullet">
    ///   <item><c>ron</c>   — exactly one positive Δ and exactly one negative Δ
    ///                        (winner takes from a single discarder).</item>
    ///   <item><c>tsumo</c> — exactly one positive Δ and three negative Δ
    ///                        (winner takes from all three losers).</item>
    ///   <item><c>draw</c>  — anything else: exhaustive-draw tenpai
    ///                        redistribution, abortive draws with no payout,
    ///                        zero-delta edge cases, double-ron (multiple
    ///                        winners), etc. Downstream analysis can sub-classify
    ///                        from the raw deltas.</item>
    /// </list>
    /// Riichi-stick movements within the hand are naturally absorbed: the delta
    /// is computed from one hand-start to the next, so the −1000 stick deposit
    /// and the sticks-to-winner payout cancel for whoever ultimately wins.
    /// </summary>
    internal static (string kind, int? winner, int? loser) InferResultKind(int[] deltas)
    {
        int pos = 0, neg = 0;
        int winnerIdx = -1, loserIdx = -1;
        int maxPos = 0, minNeg = 0;
        for (int i = 0; i < deltas.Length; i++)
        {
            if (deltas[i] > 0)
            {
                pos++;
                if (deltas[i] > maxPos) { maxPos = deltas[i]; winnerIdx = i; }
            }
            else if (deltas[i] < 0)
            {
                neg++;
                if (deltas[i] < minNeg) { minNeg = deltas[i]; loserIdx = i; }
            }
        }
        if (pos == 1 && neg == 1) return ("ron",   winnerIdx, loserIdx);
        if (pos == 1 && neg == 3) return ("tsumo", winnerIdx, null);
        return ("draw", null, null);
    }

    private void RollWriter()
    {
        lock (writerLock)
        {
            handSeq++;
            var fn = $"game-{DateTime.UtcNow:yyyyMMdd-HHmmss}-hand{handSeq:D2}.ndjson";
            currentPath = Path.Combine(gamesDir, fn);
        }
    }

    /// <summary>
    /// Open-write-close per line. Holding a persistent <see cref="StreamWriter"/>
    /// across the hand created an active writer handle that prevented
    /// <see cref="Telemetry.TelemetryUploader"/>'s scan tick from reading the
    /// file (the uploader's <c>FileShare.Read</c> request fails when an open
    /// handle has Write access). Per-line opens match the
    /// <see cref="ErrorSink"/> / <see cref="FindingsLog"/> pattern; volume is
    /// low enough (≤ a few writes per turn after the dedup fix) that the
    /// extra syscalls are immaterial.
    /// </summary>
    private void WriteLine(string line)
    {
        if (currentPath is null)
            return;
        lock (writerLock)
        {
            try
            {
                using var w = new StreamWriter(new FileStream(
                    currentPath, FileMode.Append, FileAccess.Write, FileShare.Read));
                w.WriteLine(line);
            }
            catch (Exception ex)
            {
                log.Error($"GameLogger write error: {ex.Message}");
            }
        }
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

    private sealed record HandEndEvent(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("winner")] int? Winner,
        [property: JsonPropertyName("loser")] int? Loser,
        [property: JsonPropertyName("deltas")] int[] Deltas,
        [property: JsonPropertyName("scores_after")] int[] ScoresAfter);

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

    private sealed record CallPromptDto(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("variant")] string Variant,
        [property: JsonPropertyName("sc")] int StateCode,
        [property: JsonPropertyName("flags")] int Flags,
        [property: JsonPropertyName("pon")] int[] Pon,
        [property: JsonPropertyName("chi")] int[] Chi,
        [property: JsonPropertyName("kan")] int[] Kan,
        [property: JsonPropertyName("av")] int?[] Av);
}
