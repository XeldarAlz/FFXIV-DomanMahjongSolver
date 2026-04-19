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

    public readonly record struct Kyoku(
        int Round,
        int Dealer,
        int Honba,
        int RiichiSticks,
        int[] StartScores,           // length 4
        Tile[] DoraIndicators,
        Tile[][] StartingHands,      // [seat][tile]
        int[][] DrawTiles,           // [seat][draw order in that seat's draws] → 34-space id
        int[][] DiscardTiles);       // [seat][discard order in that seat's discards] → 34-space id

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
        for (int seat = 0; seat < 4; seat++)
        {
            int baseIdx = 4 + seat * 3;
            hands[seat] = ParseTileArray(kyoku[baseIdx]);
            draws[seat] = ParseIntTileArray(kyoku[baseIdx + 1]);
            discards[seat] = ParseIntTileArray(kyoku[baseIdx + 2]);
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
            DiscardTiles: discards);
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

    private static int[] ParseIntTileArray(JsonElement arr)
    {
        var result = new List<int>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Number)
                result.Add(From136(el.GetInt32()).Id);
        }
        return result.ToArray();
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
