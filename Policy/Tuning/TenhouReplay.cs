using DomanMahjongAI.Engine;
using System;
using System.Collections.Generic;

namespace DomanMahjongAI.Policy.Tuning;

/// <summary>
/// Replays a parsed Tenhou kyoku turn-by-turn for one seat, reconstructing the
/// observable state at each discard decision, asking the supplied policy for its
/// choice, and comparing against the recorded discard. Yields per-decision
/// metrics for policy validation / weight tuning.
///
/// Simplifications: draw/discard events are interleaved strictly in seat order,
/// starting with the dealer; calls are not replayed (calls break the simple
/// sequence — a full replay needs the event stream with tags, which the MVP
/// parser doesn't extract yet).
/// </summary>
public static class TenhouReplay
{
    public readonly record struct Decision(
        int TurnIndex,
        Tile ActualDiscard,
        Tile PolicyPick,
        bool Matched);

    public readonly record struct ReplayResult(
        int TotalDecisions,
        int Matches,
        double Accuracy,
        Decision[] Decisions);

    public static ReplayResult ReplaySeat(
        TenhouLog.Kyoku kyoku,
        IPolicy policy,
        int seat)
    {
        if (seat < 0 || seat >= 4) throw new ArgumentOutOfRangeException(nameof(seat));

        // Build starting closed hand for our seat.
        var counts = new int[Tile.Count34];
        foreach (var t in kyoku.StartingHands[seat]) counts[t.Id]++;

        var draws = kyoku.DrawTiles[seat];
        var discards = kyoku.DiscardTiles[seat];
        int steps = Math.Min(draws.Length, discards.Length);

        var decisions = new List<Decision>();
        var publicDiscards = new List<Tile>[4];
        for (int i = 0; i < 4; i++) publicDiscards[i] = new List<Tile>();

        for (int turn = 0; turn < steps; turn++)
        {
            // Draw tile arrives in hand.
            int drawId = draws[turn];
            counts[drawId]++;

            // Build snapshot from the public perspective of this seat.
            var handList = new List<Tile>();
            for (int k = 0; k < Tile.Count34; k++)
                for (int c = 0; c < counts[k]; c++)
                    handList.Add(Tile.FromId(k));

            var seats = new SeatView[4];
            for (int rel = 0; rel < 4; rel++)
            {
                int abs = (seat + rel) % 4;
                seats[rel] = new SeatView(
                    Discards: publicDiscards[abs].ToArray(),
                    DiscardIsTedashi: new bool[publicDiscards[abs].Count],
                    Melds: [],
                    Riichi: false,
                    RiichiDiscardIndex: -1,
                    Ippatsu: false,
                    IsTenpaiCalled: false);
            }

            var snap = StateSnapshot.Empty with
            {
                Hand = handList,
                OurSeat = seat,
                RoundWind = kyoku.Round,
                Honba = kyoku.Honba,
                RiichiSticks = kyoku.RiichiSticks,
                Scores = kyoku.StartScores,
                DoraIndicators = kyoku.DoraIndicators,
                DealerSeat = kyoku.Dealer,
                Seats = seats,
                Legal = new LegalActions(ActionFlags.Discard, [], [], [], []),
            };

            var choice = policy.Choose(snap);
            var policyPick = choice.DiscardTile ?? Tile.FromId(drawId);
            var actual = Tile.FromId(discards[turn]);

            decisions.Add(new Decision(turn, actual, policyPick, policyPick.Id == actual.Id));

            // Apply the *actual* discard so subsequent turns match the recorded line.
            counts[actual.Id]--;
            publicDiscards[seat].Add(actual);
        }

        int matches = 0;
        foreach (var d in decisions) if (d.Matched) matches++;
        double acc = decisions.Count == 0 ? 0.0 : (double)matches / decisions.Count;

        return new ReplayResult(decisions.Count, matches, acc, decisions.ToArray());
    }
}
