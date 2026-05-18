using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Engine;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace Mahjong.Plugin.Dalamud.GameState.Variants;

/// <summary>
/// Profile-driven reader for the Mahjong-addon family. One instance per
/// loaded <see cref="LayoutProfile"/> — adding a new client variant (JP, OC)
/// is a JSON file in <c>data/layouts/</c> plus a one-line registration, never
/// a code change here.
///
/// Pre-Phase-6: this class was abstract with two subclasses (Emj and EmjL)
/// supplying only a tile texture base each. The abstract hierarchy is gone;
/// every variant-specific constant comes from the injected profile.
/// </summary>
internal sealed class BaseEmjVariant : IEmjVariant
{
    private readonly LayoutProfile profile;
    private readonly IPluginLog log;
    private readonly string pluginConfigDir;

    public BaseEmjVariant(LayoutProfile profile, IPluginLog log, string pluginConfigDir)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDir);
        this.profile = profile;
        this.log = log;
        this.pluginConfigDir = pluginConfigDir;
    }

    public string Name => profile.Name;
    public string PreferredAddonName => profile.AddonName;
    public LayoutProfile Profile => profile;

    // Log de-dupe state for diagnostic dumps. Scoped per-variant so each
    // variant handles its own state-code convention without cross-talk.
    private int lastLoggedCallPromptState = -1;
    private int lastLoggedMeldHandCount = -1;

    /// <summary>
    /// Fingerprint check. Passes when:
    ///   1. self-score word is in the plausible mahjong range
    ///   2. call-modal host node exists
    ///   3. populated hand slots mostly decode to valid tile_ids under the
    ///      profile's <see cref="LayoutProfile.TileTextureBase"/> — tolerating
    ///      up to <see cref="LayoutSanityLimits.MaxAkadoraSlots"/> unknown slots.
    ///
    /// Check (3) is what distinguishes Emj from EmjL on a live hand: tile
    /// encodings use non-overlapping texture ranges, so a hand populated
    /// with one variant's textures fails the other variant's probe.
    /// </summary>
    public unsafe bool Probe(AtkUnitBase* unit)
    {
        if (unit == null || unit->RootNode == null)
            return false;

        int selfScore = *(int*)((byte*)unit + profile.Offsets.SelfScore);
        if (selfScore < 0 || selfScore > profile.Limits.ScoreSanityMax)
            return false;

        if (unit->GetNodeById(profile.NodeIds.CallModalHost) == null)
            return false;

        byte* basePtr = (byte*)unit;
        int valid = 0;
        int unknown = 0;
        for (int i = 0; i < profile.Limits.HandSize; i++)
        {
            int raw = *(int*)(basePtr + profile.Offsets.HandArrayStart + i * 4);
            if (raw == 0)
                break;
            int tileId = DecodeTileId(raw);
            if (tileId >= 0)
                valid++;
            else
                unknown++;
        }
        // Empty-hand frame (startup / between rounds): no tile evidence.
        // Pass so the selector can fall back to the name tiebreaker.
        if (valid == 0 && unknown == 0)
            return true;
        return valid >= 1 && unknown <= profile.Limits.MaxAkadoraSlots;
    }

    /// <inheritdoc />
    public unsafe StateSnapshot? TryBuildSnapshot(AtkUnitBase* unit, VariantReadContext ctx)
    {
        nint addr = (nint)unit;
        byte* basePtr = (byte*)addr;

        var hand = ReadHand(basePtr);

        var scores = ReadScores(basePtr);
        if (!ScoresPlausible(scores))
            return null;

        var doraIndicators = ReadDoraIndicators(basePtr);
        var discardCounts = ReadDiscardCounts(basePtr);

        var atkValues = unit->AtkValues;
        int atkCount = unit->AtkValuesCount;
        int stateCode = ReadStateCode(atkValues, atkCount);
        int wallRemaining = ResolveWallRemaining(discardCounts);

        var seats = BuildSeatViews(discardCounts);
        var legal = BuildLegalActions(unit, stateCode, hand, atkValues, atkCount);

        MaybeLogCallPromptTransition(ctx, addr, stateCode, atkValues, atkCount, hand, legal);
        MaybeLogMeldTransition(ctx, addr, stateCode, hand);

        // Resolve our own open melds. The addon's on-disk meld struct is still
        // un-mapped; instead the MeldTracker infers each meld from closed-hand
        // deltas observed tick-by-tick. ObserveSnapshot runs the inference
        // before we read Melds so a meld that just landed this tick is
        // already in the list. ObserveWall provides the hand-boundary reset.
        ctx.MeldTracker.ObserveWall(wallRemaining);
        ctx.MeldTracker.ObserveSnapshot(hand, discardCounts, ourSeat: 0);
        var ourMelds = ctx.MeldTracker.Melds.ToArray();

        return StateSnapshot.Empty with
        {
            Hand = hand,
            OurMelds = ourMelds,
            Scores = scores,
            Seats = seats,
            WallRemaining = wallRemaining,
            DoraIndicators = doraIndicators,
            Legal = legal,
            // Solo Doman is a tonpuusen — East-round-only, four hands. Round
            // wind = East always; player starts as East-seat dealer at the
            // first hand of any session. Both pin to 0 and we mark seat info
            // known so round-wind yakuhai detection (always East) and
            // hand-1-seat-wind yakuhai (also East) start firing in
            // CountYakuhai and YakuPotential. For hands 2-4 of a tonpuusen,
            // dealer rotation makes our true seat wind shift to N→W→S — the
            // round-wind term stays correct but the seat-wind term ends up
            // weighting East tiles when N/W/S would be right. Net still a
            // win vs the prior "everything off, both terms 0" baseline.
            // Tracking DealerSeat from the addon would let us derive OurSeat
            // mid-game; the candidate offsets identified by tools/find_seat_offsets.py
            // (+0x130, +0x1248, +0x12BC) need a multi-session sample to validate.
            OurSeat = 0,
            RoundWind = 0,
            SeatInfoKnown = true,
        };
    }

    private unsafe List<Tile> ReadHand(byte* basePtr)
    {
        var hand = new List<Tile>(profile.Limits.HandSize);
        for (int i = 0; i < profile.Limits.HandSize; i++)
        {
            int raw = *(int*)(basePtr + profile.Offsets.HandArrayStart + i * 4);
            if (raw == 0)
                break;
            int tileId = DecodeTileId(raw);
            if (tileId < 0)
                continue;
            hand.Add(Tile.FromId(tileId));
        }
        return hand;
    }

    // Doman tags akadora 5m/5p/5s as raw indices 34/35/36 past TileTextureBase.
    // Fold them into the plain tile id; without this the drawn akadora gets
    // dropped from the closed hand and auto-play freezes (BuildLegalActions
    // sees hand.Count % 3 != 2 and reports None even though it's our turn).
    private int DecodeTileId(int raw)
    {
        int idx = raw - profile.TileTextureBase;
        if (idx >= 0 && idx < Tile.Count34)
            return idx;
        return idx switch
        {
            34 => 4,
            35 => 13,
            36 => 22,
            _ => -1,
        };
    }

    private unsafe int[] ReadScores(byte* basePtr) =>
    [
        *(int*)(basePtr + profile.Offsets.SelfScore),
        *(int*)(basePtr + profile.Offsets.ShimochaScore),
        *(int*)(basePtr + profile.Offsets.ToimenScore),
        *(int*)(basePtr + profile.Offsets.KamichaScore),
    ];

    private bool ScoresPlausible(int[] scores)
    {
        int max = profile.Limits.ScoreSanityMax;
        foreach (var s in scores)
            if (s < 0 || s > max)
                return false;
        return true;
    }

    private unsafe List<Tile> ReadDoraIndicators(byte* basePtr)
    {
        var dora = new List<Tile>(1);
        int rawDora = *(int*)(basePtr + profile.Offsets.DoraIndicator);
        int doraTileId = DecodeTileId(rawDora);
        if (doraTileId >= 0)
            dora.Add(Tile.FromId(doraTileId));
        return dora;
    }

    private unsafe int[] ReadDiscardCounts(byte* basePtr)
    {
        var counts = new int[4]
        {
            basePtr[profile.Offsets.SelfDiscardCountByte],
            basePtr[profile.Offsets.ShimochaDiscardCountByte],
            basePtr[profile.Offsets.ToimenDiscardCountByte],
            basePtr[profile.Offsets.KamichaDiscardCountByte],
        };
        // Reject implausible per-seat values rather than zeroing the whole array.
        // Each seat discards up to ~24 times in a 70-wall round.
        int cap = profile.Limits.DiscardCountSanityMax;
        for (int i = 0; i < counts.Length; i++)
            if (counts[i] > cap)
                counts[i] = 0;
        return counts;
    }

    private unsafe int ReadStateCode(AtkValue* atkValues, int atkCount)
    {
        if (atkValues == null || atkCount <= profile.AtkValues.StateCode)
            return -1;
        var v = atkValues[profile.AtkValues.StateCode];
        return v.Type == ValueType.Int ? v.Int : -1;
    }

    // wall_remaining ≈ initial_live_wall − total_discards (each discard follows
    // a draw). Ignores kan draws from the dead wall, a minor under-estimate.
    //
    // Pre-2026-05-11 this also trusted atkValues[WallCount] when stateCode ==
    // PostDrawIdle. That value uses a different baseline — +6 higher than the
    // discard-derived count, consistent with kan-reserve accounting — and
    // flipped on every CallPrompt→PostDrawIdle transition. The +6 mode-switch
    // beat MaybeRollHand's +5 tolerance and rolled a fresh hand file on every
    // call-modal close. Confirmed in the 2026-05-11 telemetry, install
    // 6a4a0a70: 5 spurious hand rolls in 30 s with byte-identical addon dumps
    // (see .local/by-date/2026-05-11/.../RESTART-CLUSTER-ANALYSIS.md). The
    // discard-derived value was monotonic and correct in every cluster
    // snapshot, so we use it unconditionally.
    private int ResolveWallRemaining(int[] discardCounts)
    {
        int totalDiscards = 0;
        foreach (var c in discardCounts)
            totalDiscards += c;
        int derived = profile.Limits.WallInitial - totalDiscards;
        return derived >= 0 && derived <= profile.Limits.WallInitial ? derived : profile.Limits.WallInitial;
    }

    private static SeatView[] BuildSeatViews(int[] discardCounts)
    {
        var seats = new SeatView[4];
        for (int i = 0; i < 4; i++)
            seats[i] = new SeatView(
                Discards: [],
                DiscardIsTedashi: [],
                Melds: [],
                Riichi: false,
                RiichiDiscardIndex: -1,
                Ippatsu: false,
                IsTenpaiCalled: false,
                DiscardCount: discardCounts[i]);
        return seats;
    }

    /// <summary>
    /// Build the LegalActions record from the current state code. Three cases:
    /// <list type="bullet">
    ///   <item>Call-prompt state (CallPrompt / CallPromptList / SelfDeclareList): scan
    ///         AtkValues strings for button labels, then derive candidates.</item>
    ///   <item>Hand count satisfies <c>% 3 == 2</c> (14/11/8/5/2): plain discard.</item>
    ///   <item>Otherwise: no actions.</item>
    /// </list>
    ///
    /// <para><b>SelfDeclareList post-call gate:</b> state-6 (SelfDeclareList) is
    /// dual-use. At <c>hand.Count == 14</c> it's the genuine self-declare popup
    /// (Riichi/Tsumo/AnKan offered after a draw). At <c>hand.Count != 14</c> with
    /// <c>hand.Count % 3 == 2</c> (typically 11 after a pon, 8 after a second
    /// pon/chi, etc.) it's the post-call <i>discard-from-list</i> popup — the
    /// same UI shell, but the visible list items are the player's closed hand
    /// asking which tile to discard. Without this gate the variant decoder
    /// reports <c>legal=Pon, Pass</c> on that popup (residual "Pon" string in
    /// AtkValues from the just-resolved pon prompt) and the auto-loop spins
    /// forever passing on a popup that actually wants a discard click — the
    /// "stuck after pon, 11 tiles in hand" freeze observed 2026-05-09.
    /// Fall through to the regular discard fallback in that case.</para>
    /// </summary>
    private unsafe LegalActions BuildLegalActions(
        AtkUnitBase* unit, int stateCode, List<Tile> hand, AtkValue* atkValues, int atkCount)
    {
        var states = profile.StateCodes;
        bool isCallPromptState =
            stateCode == states.CallPrompt ||
            stateCode == states.CallPromptList ||
            (stateCode == states.SelfDeclareList && hand.Count == 14);

        if (isCallPromptState && IsCallModalVisible(unit))
        {
            const ActionFlags acceptMask =
                ActionFlags.Pon | ActionFlags.Chi |
                ActionFlags.MinKan | ActionFlags.ShouMinKan |
                ActionFlags.Ron | ActionFlags.Riichi | ActionFlags.Tsumo;
            if (atkValues != null)
            {
                var scanned = BuildCallPromptLegal(hand, atkValues, atkCount);
                if ((scanned.Flags & acceptMask) != 0)
                    return scanned;
            }
            return BuildCallPromptLegalFromListItems(unit);
        }

        // "Our turn to discard" = 14 tiles with 0 melds, 11 with 1 meld, 8 with 2, etc. —
        // all satisfy hand % 3 == 2.
        if (hand.Count > 0 && hand.Count % 3 == 2)
            return new LegalActions(ActionFlags.Discard, [], [], [], []);

        return LegalActions.None;
    }

    private unsafe bool IsCallModalVisible(AtkUnitBase* unit)
    {
        if (unit == null)
            return false;
        var host = unit->GetNodeById(profile.NodeIds.CallModalHost);
        if (host == null)
            return false;
        // Type-check before casting — component node types are ≥ 1000; native
        // types are single-digit. If a future patch renumbers the host id to a
        // non-component node, the cast would dereference the wrong struct.
        if ((int)host->Type < 1000)
            return false;
        var comp = ((AtkComponentNode*)host)->Component;
        if (comp == null)
            return false;
        var shell = comp->GetNodeById(profile.NodeIds.CallModalShell);
        return shell != null && shell->NodeFlags.HasFlag(NodeFlags.Visible);
    }

    private unsafe LegalActions BuildCallPromptLegal(
        List<Tile> hand, AtkValue* atkValues, int atkCount)
    {
        var labels = ScanButtonLabels(atkValues, atkCount, scanLimit: 20);
        if (!labels.HasAnyAcceptOffer)
            return new LegalActions(ActionFlags.Pass, [], [], [], []);

        ActionFlags flags = ActionFlags.Pass;
        var pons = new List<MeldCandidate>();
        var chis = new List<MeldCandidate>();
        var kans = new List<MeldCandidate>();

        // Ron / Riichi / Tsumo: flag-only — no candidate derivation. The policy
        // already handles agari declarations top of Choose, and Riichi just gets
        // confirmed by AutoPlayLoop with option 0.
        if (labels.OffersRon)
            flags |= ActionFlags.Ron;
        if (labels.OffersRiichi)
            flags |= ActionFlags.Riichi;
        if (labels.OffersTsumo)
            flags |= ActionFlags.Tsumo;

        var counts = new int[Tile.Count34];
        foreach (var t in hand)
            counts[t.Id]++;

        if (labels.OffersPon)
        {
            flags |= ActionFlags.Pon;
            // Prefer the discarded tile from AtkValues — Doman exposes it as a
            // consecutive duplicate in [16..21]. Falls back to the unique-pair
            // heuristic when the addon-side data isn't available (e.g. unit tests
            // or a future variant where the duplicate signal moves).
            if (atkValues != null)
                AppendPonCandidateFromAtkValues(hand, counts, atkValues, atkCount, pons);
            if (pons.Count == 0)
                AppendPonCandidate(hand, counts, pons);
        }

        // MinKan: emit a candidate only when triplet is unambiguous AND pon isn't
        // on the same row (DispatchCall hardcodes opt 0 = Pon, so emitting a Kan
        // candidate alongside pon would risk misfiring).
        if (labels.OffersKan)
        {
            flags |= ActionFlags.MinKan;
            if (!labels.OffersPon)
                AppendKanCandidate(hand, counts, kans);
        }

        // Chi: claimed tile is at the configured AtkValue index (chi-claim slot).
        // Pon+Chi simultaneous prompt: skip the chi candidate when pon is also
        // offered (DispatchCall always clicks option 0, and pon is leftmost).
        if (labels.OffersChi)
        {
            flags |= ActionFlags.Chi;
            if (!labels.OffersPon)
                AppendChiCandidate(hand, atkValues, atkCount, chis);
        }

        return new LegalActions(flags, [], pons, chis, kans);
    }

    /// <summary>
    /// Fallback when <see cref="AppendPonCandidateFromAtkValues"/> didn't find
    /// the claim tile in the AtkValue scan. Enumerate every pair we hold and
    /// emit a Pon candidate per pair — the call policy picks the best by
    /// shanten gain, and the click itself is just opcode-11 / option-0
    /// (the game forms whichever tile is actually being claimed, regardless
    /// of which candidate the policy reasoned about).
    ///
    /// <para>The earlier "unique pair only" rule silently dropped Pon prompts
    /// whenever the hand carried two-or-more pairs — observed 2026-05-09 09:54
    /// where a 3-pair hand declined every Pon offer with "no call candidates
    /// offered" because the unique-pair check returned ambiguous and never
    /// emitted anything.</para>
    /// </summary>
    private void AppendPonCandidate(List<Tile> hand, int[] counts, List<MeldCandidate> pons)
    {
        for (int id = 0; id < Tile.Count34; id++)
        {
            if (counts[id] < 2)
                continue;
            var claimed = Tile.FromId(id);
            var derived = CallCandidateDeriver.Derive(hand, claimed, fromSeat: 1);
            pons.AddRange(derived.Pon);
        }
    }

    /// <summary>
    /// Read the discarded tile that triggered the pon prompt directly from the
    /// addon's AtkValues. Doman publishes it as a consecutive duplicate Int in
    /// the [16..21] range — verified empirically against two distinct pon
    /// captures on 2026-05-08:
    /// <list type="bullet">
    ///   <item>Hand <c>44669m289p28s77z</c> ponning Red dragon: duplicate 76074
    ///   at [20][21], decodes to tile_id 33.</item>
    ///   <item>Hand <c>2233m5p1133s45z</c> ponning 3s: duplicate 76061 at
    ///   [19][20], decodes to tile_id 20.</item>
    /// </list>
    /// The duplicate slot floats inside the range — neither a fixed index like
    /// <c>chiClaimedTile</c> nor a count-based heuristic — so we scan and pick
    /// the tile_id that appears at least twice. The user must also have ≥ 2 of
    /// it (otherwise pon couldn't have been offered); the count check protects
    /// against a future Doman patch repurposing this slot.
    /// </summary>
    private unsafe void AppendPonCandidateFromAtkValues(
        List<Tile> hand, int[] counts, AtkValue* atkValues, int atkCount, List<MeldCandidate> pons)
    {
        const int scanLo = 16;
        const int scanHi = 21;
        int end = Math.Min(atkCount, scanHi + 1);
        Span<int> seen = stackalloc int[Tile.Count34];
        int? claimedId = null;
        for (int i = scanLo; i < end; i++)
        {
            if (atkValues[i].Type != ValueType.Int)
                continue;
            int tileId = DecodeTileId(atkValues[i].Int);
            if (tileId < 0)
                continue;
            seen[tileId]++;
            if (seen[tileId] >= 2)
                claimedId = tileId;
        }
        if (claimedId is null || counts[claimedId.Value] < 2)
            return;
        var claimed = Tile.FromId(claimedId.Value);
        var derived = CallCandidateDeriver.Derive(hand, claimed, fromSeat: 1);
        pons.AddRange(derived.Pon);
    }

    private void AppendKanCandidate(List<Tile> hand, int[] counts, List<MeldCandidate> kans)
    {
        if (TryFindUniqueRunOrTriplet(counts, minCount: 3) is not int tripId)
            return;
        var claimed = Tile.FromId(tripId);
        var derived = CallCandidateDeriver.Derive(hand, claimed, fromSeat: 1);
        kans.AddRange(derived.Kan);
    }

    /// <summary>
    /// Resolve the chi-claimed tile from AtkValues. The configured slot
    /// (<see cref="LayoutAtkValueIndices.ChiClaimedTile"/>, default 19) is
    /// correct for some prompts — confirmed by the successful 2026-05-09
    /// 10:06 chi accept where atk[19]=9s lined up with the user's 7s+8s.
    /// But it's not consistent: at 2026-05-09 09:54 atk[19]=East across a
    /// dozen back-to-back chi prompts, derivation always returned zero
    /// (chi can't be on honors), and we declined every chi the game
    /// offered — most of the post-fix call-decline volume.
    ///
    /// <para>Strategy: try the configured slot first; if it produces a
    /// derivable chi, use it. Otherwise scan a wider window for any int
    /// slot whose tile id, treated as a chi claim, yields ≥1 valid
    /// sequence with the current hand. Take the first match. The candidate
    /// is only used by the call policy's evaluator — the click itself
    /// is just opcode-11 / option-0, and the game forms whichever
    /// chi-variant matches the actual offer.</para>
    /// </summary>
    private unsafe void AppendChiCandidate(
        List<Tile> hand, AtkValue* atkValues, int atkCount, List<MeldCandidate> chis)
    {
        if (atkValues == null)
            return;

        int configuredIdx = profile.AtkValues.ChiClaimedTile;
        if (TryDeriveChiFromSlot(hand, atkValues, atkCount, configuredIdx, chis))
            return;

        // Configured slot didn't yield a chi. Scan the [0..30] window for any
        // suited tile whose claim derives ≥1 chi against the current hand.
        // Bounded scan because beyond ~30 the array tends to carry unrelated
        // payloads (seat scores, discard piles further upstream) that would
        // produce false-positive candidates.
        int scanLimit = Math.Min(atkCount, 30);
        for (int i = 0; i < scanLimit; i++)
        {
            if (i == configuredIdx)
                continue;
            if (TryDeriveChiFromSlot(hand, atkValues, atkCount, i, chis))
                return;
        }
    }

    private unsafe bool TryDeriveChiFromSlot(
        List<Tile> hand, AtkValue* atkValues, int atkCount,
        int slot, List<MeldCandidate> chis)
    {
        if (slot < 0 || slot >= atkCount)
            return false;
        if (atkValues[slot].Type != ValueType.Int)
            return false;
        int tileId = DecodeTileId(atkValues[slot].Int);
        if (tileId < 0)
            return false;
        var claimed = Tile.FromId(tileId);
        if (claimed.IsHonor)
            return false;
        var derived = CallCandidateDeriver.Derive(hand, claimed, fromSeat: 3);
        if (derived.Chi.Count == 0)
            return false;
        chis.AddRange(derived.Chi);
        return true;
    }

    /// <summary>
    /// Find the tile id whose count meets <paramref name="minCount"/> when exactly
    /// one such tile exists. Returns null when zero or multiple qualify — the
    /// caller emits no candidate, which lets the call evaluator pass.
    /// </summary>
    private static int? TryFindUniqueRunOrTriplet(int[] counts, int minCount)
    {
        int matchCount = 0;
        int matchId = -1;
        for (int id = 0; id < Tile.Count34; id++)
        {
            if (counts[id] >= minCount)
            {
                matchCount++;
                matchId = id;
            }
        }
        return matchCount == 1 ? matchId : null;
    }

    private unsafe ButtonLabelScan ScanButtonLabels(AtkValue* atkValues, int atkCount, int scanLimit)
    {
        var scan = new ButtonLabelScan();
        int end = Math.Min(atkCount, scanLimit);
        for (int i = 0; i < end; i++)
        {
            var v = atkValues[i];
            if (v.Type != ValueType.String && v.Type != ValueType.String8 && v.Type != ValueType.ManagedString)
                continue;
            if (v.String.Value == null)
                continue;
            scan.RecordLabel(v.String.ToString());
        }
        return scan;
    }

    /// <summary>
    /// Tracks which call-prompt buttons we saw while scanning the AtkValues array.
    /// </summary>
    private struct ButtonLabelScan
    {
        public bool OffersPon;
        public bool OffersChi;
        public bool OffersKan;
        public bool OffersRon;
        public bool OffersRiichi;
        public bool OffersTsumo;

        public bool HasAnyAcceptOffer =>
            OffersPon || OffersChi || OffersKan ||
            OffersRon || OffersRiichi || OffersTsumo;

        public void RecordLabel(string label)
        {
            switch (label)
            {
                case "Pon":
                    OffersPon = true;
                    break;
                case "Chi":
                    OffersChi = true;
                    break;
                case "Kan":
                    OffersKan = true;
                    break;
                case "Ron":
                    OffersRon = true;
                    break;
                case "Riichi":
                    OffersRiichi = true;
                    break;
                case "Tsumo":
                    OffersTsumo = true;
                    break;
            }
        }
    }

    /// <summary>
    /// Build LegalActions for an AtkComponentList-based call prompt (state 28 today).
    /// Reads each visible ListItemRenderer child of the modal shell and maps its
    /// text label to an <see cref="ActionFlags"/> bit.
    /// </summary>
    private unsafe LegalActions BuildCallPromptLegalFromListItems(AtkUnitBase* unit)
    {
        var labels = ReadVisibleListItemLabels(unit);
        if (labels.Count == 0)
            return new LegalActions(ActionFlags.Pass, [], [], [], []);

        ActionFlags flags = ActionFlags.Pass;
        foreach (var raw in labels)
        {
            switch (raw.Trim())
            {
                case "Pon":
                    flags |= ActionFlags.Pon;
                    break;
                case "Chi":
                    flags |= ActionFlags.Chi;
                    break;
                case "Kan":
                    flags |= ActionFlags.MinKan;
                    break;
                case "Ron":
                    flags |= ActionFlags.Ron;
                    break;
                case "Riichi":
                    flags |= ActionFlags.Riichi;
                    break;
                case "Tsumo":
                    flags |= ActionFlags.Tsumo;
                    break;
                    // "Pass" / "Cancel" contribute no accept flag — AutoPlayLoop derives
                    // the pass option index from the count of accept flags set.
            }
        }
        return new LegalActions(flags, [], [], [], []);
    }

    private unsafe List<string> ReadVisibleListItemLabels(AtkUnitBase* unit)
    {
        var labels = new List<string>();
        if (unit == null)
            return labels;

        var host = unit->GetNodeById(profile.NodeIds.CallModalHost);
        if (host == null || (int)host->Type < 1000)
            return labels;
        var hostComp = ((AtkComponentNode*)host)->Component;
        if (hostComp == null)
            return labels;
        var shell = hostComp->GetNodeById(profile.NodeIds.CallModalShell);
        if (shell == null || (int)shell->Type < 1000)
            return labels;
        var shellComp = ((AtkComponentNode*)shell)->Component;
        if (shellComp == null)
            return labels;

        var ulm = shellComp->UldManager;
        if (ulm.NodeList == null || ulm.NodeListCount == 0)
            return labels;

        var items = new List<(float y, string text)>();
        for (int i = 0; i < ulm.NodeListCount; i++)
        {
            var node = ulm.NodeList[i];
            if (node == null || (int)node->Type < 1000 || !node->NodeFlags.HasFlag(NodeFlags.Visible))
                continue;
            var itemComp = ((AtkComponentNode*)node)->Component;
            if (itemComp == null)
                continue;
            string text = FindFirstTextInComponent(itemComp) ?? string.Empty;
            items.Add((node->Y, text));
        }
        // Top-to-bottom: the game's FireCallback option index for a list click
        // follows visual order (opt 0 = top button).
        items.Sort((a, b) => a.y.CompareTo(b.y));
        foreach (var (_, t) in items)
            labels.Add(t);
        return labels;
    }

    private static unsafe string? FindFirstTextInComponent(AtkComponentBase* comp)
    {
        if (comp == null)
            return null;
        var ulm = comp->UldManager;
        if (ulm.NodeList == null || ulm.NodeListCount == 0)
            return null;
        for (int i = 0; i < ulm.NodeListCount; i++)
        {
            var node = ulm.NodeList[i];
            if (node == null || node->Type != NodeType.Text)
                continue;
            var textNode = (AtkTextNode*)node;
            var s = textNode->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }
        return null;
    }

    // -----------------------------------------------------------------
    // Diagnostic dumps. Hardcoded memory ranges are debug-only — these
    // get inspected when reverse-engineering new variants and aren't
    // part of the runtime data path.
    // -----------------------------------------------------------------

    private unsafe void MaybeLogCallPromptTransition(
        VariantReadContext ctx, nint addonBase, int stateCode, AtkValue* atkValues, int atkCount,
        IReadOnlyList<Tile> hand, LegalActions legal)
    {
        const ActionFlags promptFlags =
            ActionFlags.Pon | ActionFlags.Chi | ActionFlags.MinKan |
            ActionFlags.ShouMinKan | ActionFlags.Ron |
            ActionFlags.Riichi | ActionFlags.Tsumo;

        bool isPrompt = stateCode == profile.StateCodes.CallPrompt && (legal.Flags & promptFlags) != 0;
        if (!isPrompt)
        {
            lastLoggedCallPromptState = -1;
            return;
        }
        if (lastLoggedCallPromptState == profile.StateCodes.CallPrompt)
            return;
        lastLoggedCallPromptState = profile.StateCodes.CallPrompt;

        if (atkValues == null)
            return;

        // Always-on managed event: snapshot AtkValues + decoded candidates and
        // fire so GameLogger ships them via the games stream. The diagnostic
        // file write below stays gated on EventLogger.Enabled (verbose RE log).
        var ints = SnapshotAtkInts(atkValues, atkCount, max: 24);
        ctx.EventLogger.RaiseCallPrompt(new CallPromptEvent(
            ObservedAtUtc: DateTime.UtcNow,
            AddonName: Name,
            StateCode: stateCode,
            Flags: (int)legal.Flags,
            PonClaimedTileIds: ExtractClaimedTileIds(legal.PonCandidates),
            ChiClaimedTileIds: ExtractClaimedTileIds(legal.ChiCandidates),
            KanClaimedTileIds: ExtractClaimedTileIds(legal.KanCandidates),
            IntValues: ints));

        if (!ctx.EventLogger.Enabled)
            return;

        try
        {
            System.IO.Directory.CreateDirectory(pluginConfigDir);
            var dir = pluginConfigDir;
            var path = System.IO.Path.Combine(dir, "emj-call-prompts.log");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# {DateTime.UtcNow:o}  variant={Name}  state={stateCode}  atkCount={atkCount}");
            sb.Append($"hand={Tiles.Render(hand)}  flags={legal.Flags}  ");
            sb.AppendLine($"pon={legal.PonCandidates.Count} chi={legal.ChiCandidates.Count} kan={legal.KanCandidates.Count}");

            for (int i = 0; i < atkCount && i < 64; i++)
                AppendAtkValue(sb, atkValues[i], i);

            DumpMemoryRegion(sb, addonBase);
            sb.AppendLine();

            System.IO.File.AppendAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            log.Error($"call-prompt diagnostic log error: {ex.Message}");
        }
    }

    private static unsafe int?[] SnapshotAtkInts(AtkValue* values, int count, int max)
    {
        if (values == null || count <= 0)
            return Array.Empty<int?>();
        int n = Math.Min(count, max);
        var result = new int?[n];
        for (int i = 0; i < n; i++)
        {
            var v = values[i];
            result[i] = v.Type switch
            {
                ValueType.Int => v.Int,
                ValueType.UInt => unchecked((int)v.UInt),
                ValueType.Bool => v.Byte != 0 ? 1 : 0,
                _ => (int?)null,
            };
        }
        return result;
    }

    private static int[] ExtractClaimedTileIds(IReadOnlyList<MeldCandidate> candidates)
    {
        if (candidates.Count == 0)
            return Array.Empty<int>();
        var ids = new int[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
            ids[i] = candidates[i].ClaimedTile.Id;
        return ids;
    }

    private static unsafe void AppendAtkValue(System.Text.StringBuilder sb, AtkValue v, int i)
    {
        sb.Append($"  [{i,3}] {v.Type,-14} ");
        switch (v.Type)
        {
            case ValueType.Int:
                sb.Append($"Int={v.Int}");
                break;
            case ValueType.UInt:
                sb.Append($"UInt={v.UInt} (0x{v.UInt:X})");
                break;
            case ValueType.Bool:
                sb.Append($"Bool={v.Byte != 0}");
                break;
            case ValueType.String:
            case ValueType.String8:
            case ValueType.ManagedString:
                var s = v.String.Value != null ? v.String.ToString() : "(null)";
                sb.Append($"String=\"{s}\"");
                break;
            default:
                sb.Append($"raw=0x{v.UInt:X}");
                break;
        }
        sb.AppendLine();
    }

    private unsafe void MaybeLogMeldTransition(
        VariantReadContext ctx, nint addonBase, int stateCode, IReadOnlyList<Tile> hand)
    {
        if (hand.Count >= 13 || hand.Count <= 0)
        {
            lastLoggedMeldHandCount = -1;
            return;
        }
        if (hand.Count == lastLoggedMeldHandCount)
            return;
        lastLoggedMeldHandCount = hand.Count;

        if (!ctx.EventLogger.Enabled)
            return;

        try
        {
            System.IO.Directory.CreateDirectory(pluginConfigDir);
            var dir = pluginConfigDir;
            var path = System.IO.Path.Combine(dir, "emj-meld-captures.log");

            var sb = new System.Text.StringBuilder();
            int inferredMelds = (14 - hand.Count) / 3;
            int remainder = (14 - hand.Count) % 3;
            sb.AppendLine(
                $"# {DateTime.UtcNow:o}  variant={Name}  state={stateCode}  closedHand={hand.Count}  " +
                $"inferredMelds={inferredMelds}{(remainder != 0 ? " (off-sync)" : "")}  " +
                $"hand={Tiles.Render(hand)}");

            DumpAddonMeldRegion(sb, addonBase);
            DumpAgentEmj(sb);

            sb.AppendLine();
            System.IO.File.AppendAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            log.Error($"meld-capture diagnostic log error: {ex.Message}");
        }
    }

    private static unsafe void DumpAddonMeldRegion(System.Text.StringBuilder sb, nint addonBase)
    {
        byte* basePtr = (byte*)addonBase;
        sb.AppendLine("  -- addon @ +0x0500..+0x3000 (per-seat blocks + post-hand area + extended) --");
        for (int off = 0x0500; off < 0x3000; off += 16)
            AppendHexRow(sb, basePtr, off, 16);
    }

    private static unsafe void DumpAgentEmj(System.Text.StringBuilder sb)
    {
        var agentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
        if (agentModule == null)
        {
            sb.AppendLine("  -- AgentModule unavailable --");
            return;
        }
        var agent = agentModule->GetAgentByInternalId(
            (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId)5);
        if (agent == null)
        {
            sb.AppendLine("  -- AgentEmj unavailable (GetAgentByInternalId returned null) --");
            return;
        }

        sb.AppendLine($"  -- AgentEmj @ 0x{(nint)agent:X} +0x0000..+0x2000 --");
        byte* agentPtr = (byte*)agent;
        for (int off = 0; off < 0x2000; off += 16)
            AppendHexRow(sb, agentPtr, off, 16);
    }

    private static unsafe void AppendHexRow(System.Text.StringBuilder sb, byte* basePtr, int offset, int length)
    {
        sb.Append($"  +0x{offset:X4}: ");
        for (int i = 0; i < length; i++)
        {
            sb.Append($"{basePtr[offset + i]:X2} ");
            if (i == 7)
                sb.Append(' ');
        }
        sb.Append(" |");
        for (int i = 0; i < length; i++)
        {
            byte b = basePtr[offset + i];
            sb.Append(b >= 32 && b < 127 ? (char)b : '.');
        }
        sb.AppendLine("|");
    }

    private static unsafe void DumpMemoryRegion(System.Text.StringBuilder sb, nint addonBase)
    {
        byte* basePtr = (byte*)addonBase;
        DumpRange(sb, basePtr, 0x0100, 0x0400);
        DumpRange(sb, basePtr, 0x0E00, 0x0400);
    }

    private static unsafe void DumpRange(System.Text.StringBuilder sb, byte* basePtr, int offset, int length)
    {
        sb.AppendLine($"  -- memory @ +0x{offset:X4}..+0x{offset + length:X4} --");
        for (int row = 0; row < length; row += 16)
            AppendHexRow(sb, basePtr, offset + row, 16);
    }
}
