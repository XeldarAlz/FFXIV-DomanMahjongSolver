using DomanMahjongAI.Engine;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DomanMahjongAI.Policy.Tuning;

/// <summary>
/// Tenhou 6-format log parser. Maps Tenhou's 136-tile IDs to our 34-tile IDs,
/// extracts per-kyoku starting state and discards. Scope is intentionally small:
/// enough to replay hands against our policies and compare decisions. Riichi,
/// calls, and agari detail parsing are TODO — for weight tuning the starting
/// hand + wall order is the core signal.
///
/// Tenhou 136-ID encoding: id = suit_base + number*4 + copy_index
///   0..35  = man  (1m..9m, 4 copies each, id/4 - 0  = 1-indexed rank → our id = id/4)
///   36..71 = pin
///   72..107 = sou
///   108..135 = honors (E,S,W,N,haku,hatsu,chun in order, 4 copies each)
/// Red-5 (aka) = copy 0 at id 16 (5m), 52 (5p), 88 (5s). We lose the aka bit when
/// mapping to 34-space; that's acceptable for ukeire/shanten but drops a dora.
/// </summary>
public static class TenhouLog
{
    public static Tile From136(int pai)
    {
        if (pai < 0 || pai >= 136)
            throw new ArgumentOutOfRangeException(nameof(pai), $"expected 0..135, got {pai}");
        return Tile.FromId(pai / 4);
    }

    public static bool IsRed5(int pai) => pai == 16 || pai == 52 || pai == 88;

    public enum EventKind { None, Riichi, Pon, Chi, Kan, Agari, Other }

    public readonly record struct Event(EventKind Kind, string RawTag, int TileId);

    public readonly record struct Kyoku(
        int Round,
        int Dealer,
        int Honba,
        int RiichiSticks,
        int[] StartScores,           // length 4
        Tile[] DoraIndicators,
        Tile[][] StartingHands,      // [seat][tile]
        int[][] DrawTiles,           // [seat][draw order] → 34-space id
        int[][] DiscardTiles,        // [seat][discard order] → 34-space id
        Event[][] DrawEvents,        // [seat][draw order] → event (parallel to DrawTiles) — non-None = special
        Event[][] DiscardEvents);    // [seat][discard order] → event (parallel to DiscardTiles)

    /// <summary>
    /// Parse a single-kyoku Tenhou JSON array (e.g., one element of the root "log" array).
    /// Format (from tenhou.net/6 logs):
    ///   [round_info, scores, dora_indicators, ura_dora_indicators,
    ///    seat0_hand, seat0_draws, seat0_discards,
    ///    seat1_hand, seat1_draws, seat1_discards,
    ///    seat2_hand, seat2_draws, seat2_discards,
    ///    seat3_hand, seat3_draws, seat3_discards,
    ///    result]
    /// Draws/discards may contain string tokens for riichi/call events; those are
    /// filtered out and counted only when they are plain integers (plain tile IDs).
    /// </summary>
    public static Kyoku ParseKyoku(JsonElement kyoku)
    {
        if (kyoku.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("expected JSON array for kyoku");

        var roundInfo = kyoku[0];
        int roundDealerHonba = roundInfo[0].GetInt32();
        int honba = roundInfo[1].GetInt32();
        int riichiSticks = roundInfo[2].GetInt32();
        int round = roundDealerHonba / 4;
        int dealer = roundDealerHonba % 4;

        var startScores = new int[4];
        var scoresEl = kyoku[1];
        for (int i = 0; i < 4 && i < scoresEl.GetArrayLength(); i++)
            startScores[i] = scoresEl[i].GetInt32();

        var dora = new List<Tile>();
        foreach (var d in kyoku[2].EnumerateArray())
            dora.Add(From136(d.GetInt32()));

        var hands = new Tile[4][];
        var draws = new int[4][];
        var discards = new int[4][];
        var drawEvents = new Event[4][];
        var discardEvents = new Event[4][];
        for (int seat = 0; seat < 4; seat++)
        {
            int baseIdx = 4 + seat * 3;
            hands[seat] = ParseTileArray(kyoku[baseIdx]);
            var (drawTiles, drawEv) = ParseEventArray(kyoku[baseIdx + 1]);
            var (discTiles, discEv) = ParseEventArray(kyoku[baseIdx + 2]);
            draws[seat] = drawTiles;
            discards[seat] = discTiles;
            drawEvents[seat] = drawEv;
            discardEvents[seat] = discEv;
        }

        return new Kyoku(
            Round: round,
            Dealer: dealer,
            Honba: honba,
            RiichiSticks: riichiSticks,
            StartScores: startScores,
            DoraIndicators: dora.ToArray(),
            StartingHands: hands,
            DrawTiles: draws,
            DiscardTiles: discards,
            DrawEvents: drawEvents,
            DiscardEvents: discardEvents);
    }

    private static Tile[] ParseTileArray(JsonElement arr)
    {
        var result = new List<Tile>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Number)
                result.Add(From136(el.GetInt32()));
        }
        return result.ToArray();
    }

    /// <summary>
    /// Parse a draw-or-discard slot array. Numbers are plain tile IDs; strings are
    /// event tags ("r60" = riichi discard, "p..." = pon call, "c..." = chi call,
    /// "m..."/"k..."/"a..." = kan variants, "agari"/"r"/etc.). Each string carries
    /// a 136-ID suffix where applicable — we extract the trailing integer block.
    /// The output arrays are parallel: tiles[i] is the tile at position i, and
    /// events[i] is the event type and raw tag (Kind=None for plain tiles).
    /// </summary>
    private static (int[] tiles, Event[] events) ParseEventArray(JsonElement arr)
    {
        var tiles = new List<int>();
        var events = new List<Event>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Number)
            {
                int id = From136(el.GetInt32()).Id;
                tiles.Add(id);
                events.Add(new Event(EventKind.None, "", id));
            }
            else if (el.ValueKind == JsonValueKind.String)
            {
                string tag = el.GetString() ?? "";
                var (kind, tileId) = ParseEventTag(tag);
                if (tileId >= 0)
                {
                    tiles.Add(tileId);
                    events.Add(new Event(kind, tag, tileId));
                }
                else
                {
                    // Pure event string with no tile payload — record event-only slot
                    // with tile id = -1. Consumers can filter these out.
                    tiles.Add(-1);
                    events.Add(new Event(kind, tag, -1));
                }
            }
        }
        return (tiles.ToArray(), events.ToArray());
    }

    internal static (EventKind kind, int tileId) ParseEventTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return (EventKind.Other, -1);

        char prefix = char.ToLowerInvariant(tag[0]);
        EventKind kind = prefix switch
        {
            'r' => EventKind.Riichi,
            'p' => EventKind.Pon,
            'c' => EventKind.Chi,
            'm' or 'k' or 'a' => EventKind.Kan,
            _ => EventKind.Other,
        };

        // Extract leading digit block after the prefix char(s). Tenhou tags are
        // concatenations like "r60" or "c123456"; we take the first integer block.
        int start = 0;
        while (start < tag.Length && !char.IsDigit(tag[start])) start++;
        int end = start;
        while (end < tag.Length && char.IsDigit(tag[end])) end++;
        if (start == end) return (kind, -1);
        if (!int.TryParse(tag.AsSpan(start, end - start), out int pai136)) return (kind, -1);
        if (pai136 < 0 || pai136 >= 136) return (kind, -1);
        return (kind, From136(pai136).Id);
    }

    /// <summary>
    /// Parse a full Tenhou JSON document. Expected shape: root is an object with
    /// a "log" field whose value is an array of kyoku arrays.
    /// </summary>
    public static Kyoku[] ParseDocument(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("log", out var log))
            throw new ArgumentException("Tenhou JSON has no 'log' field");

        var result = new List<Kyoku>();
        foreach (var kyoku in log.EnumerateArray())
            result.Add(ParseKyoku(kyoku));
        return result.ToArray();
    }
}
