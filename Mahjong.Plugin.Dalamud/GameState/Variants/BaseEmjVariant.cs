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
            int tileId = raw - profile.TileTextureBase;
            if (tileId >= 0 && tileId < Tile.Count34)
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
        int wallRemaining = ResolveWallRemaining(stateCode, atkValues, atkCount, discardCounts);

        var seats = BuildSeatViews(discardCounts);
        var legal = BuildLegalActions(unit, stateCode, hand, atkValues, atkCount);

        MaybeLogCallPromptTransition(ctx, addr, stateCode, atkValues, atkCount, hand, legal);
        MaybeLogMeldTransition(ctx, addr, stateCode, hand);

        // Resolve our own open melds. The addon's on-disk meld struct is still
        // un-mapped; instead the MeldTracker captures each meld when the auto-play
        // (or hooked manual click) accepts a call prompt. Reset the tracker when the
        // closed-hand count proves a new round has started.
        ctx.MeldTracker.ResetIfRoundEnded(hand.Count);
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
            int tileId = raw - profile.TileTextureBase;
            if (tileId < 0 || tileId >= Tile.Count34)
                continue;
            hand.Add(Tile.FromId(tileId));
        }
        return hand;
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
        int doraTileId = rawDora - profile.TileTextureBase;
        if (doraTileId >= 0 && doraTileId < Tile.Count34)
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

    private unsafe int ResolveWallRemaining(int stateCode, AtkValue* atkValues, int atkCount, int[] discardCounts)
    {
        // Trust the addon's wall count when state == post-draw idle and the slot is set.
        if (stateCode == profile.StateCodes.PostDrawIdle
            && atkValues != null
            && atkCount > profile.AtkValues.WallCount
            && atkValues[profile.AtkValues.WallCount].Type == ValueType.Int)
        {
            int reported = atkValues[profile.AtkValues.WallCount].Int;
            if (reported > 0 && reported <= 136)
                return reported;
        }

        // Fallback: wall_remaining ≈ initial_live_wall − total_discards (each discard
        // follows a draw). Ignores kan draws from the dead wall (minor under-estimate).
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
    /// </summary>
    private unsafe LegalActions BuildLegalActions(
        AtkUnitBase* unit, int stateCode, List<Tile> hand, AtkValue* atkValues, int atkCount)
    {
        var states = profile.StateCodes;
        bool isCallPromptState =
            stateCode == states.CallPrompt ||
            stateCode == states.SelfDeclareList ||
            stateCode == states.CallPromptList;

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

    private void AppendPonCandidate(List<Tile> hand, int[] counts, List<MeldCandidate> pons)
    {
        if (TryFindUniqueRunOrTriplet(counts, minCount: 2) is not int pairId)
            return;
        var claimed = Tile.FromId(pairId);
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

    private unsafe void AppendChiCandidate(
        List<Tile> hand, AtkValue* atkValues, int atkCount, List<MeldCandidate> chis)
    {
        int idx = profile.AtkValues.ChiClaimedTile;
        if (atkValues == null || atkCount <= idx || atkValues[idx].Type != ValueType.Int)
            return;
        int tex = atkValues[idx].Int;
        int tileId = tex - profile.TileTextureBase;
        if (tileId < 0 || tileId >= Tile.Count34)
            return;
        var claimed = Tile.FromId(tileId);
        var derived = CallCandidateDeriver.Derive(hand, claimed, fromSeat: 3);
        chis.AddRange(derived.Chi);
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

        if (!ctx.EventLogger.Enabled || atkValues == null)
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
