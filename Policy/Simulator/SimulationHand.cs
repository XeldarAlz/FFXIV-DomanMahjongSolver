using DomanMahjongAI.Engine;
using System.Collections.Generic;

namespace DomanMahjongAI.Policy.Simulator;

/// <summary>
/// Mutable per-hand simulation state. Tracks all 4 seats' closed hands (hidden from
/// outside perspective), public discards, open melds, wall, scores, and turn order.
/// </summary>
internal sealed class SimulationHand
{
    public readonly int[][] ClosedCounts = new int[4][];
    public readonly List<Meld>[] Melds = new List<Meld>[4];
    public readonly List<Tile>[] Discards = new List<Tile>[4];
    public readonly List<bool>[] DiscardIsTedashi = new List<bool>[4];
    public readonly Queue<Tile> Wall = new();
    public Tile DoraIndicator;
    public readonly int[] Scores = new int[4];
    public int Dealer;
    public int Round;
    public int Honba;
#pragma warning disable CS0649
    public int RiichiSticks;   // unused in MVP, written when riichi is added later
#pragma warning restore CS0649
    public int CurrentSeat;
    public readonly bool[] Riichi = new bool[4];
    public Tile? LastDrawnTile;

    public SimulationHand()
    {
        for (int i = 0; i < 4; i++)
        {
            ClosedCounts[i] = new int[Tile.Count34];
            Melds[i] = new List<Meld>();
            Discards[i] = new List<Tile>();
            DiscardIsTedashi[i] = new List<bool>();
        }
    }

    public int HandTileCount(int seat)
    {
        int total = 0;
        for (int k = 0; k < Tile.Count34; k++) total += ClosedCounts[seat][k];
        return total;
    }

    /// <summary>
    /// Build the observable snapshot from <paramref name="observerSeat"/>'s perspective.
    /// Hand counts are filled from the observer's closed tiles (expanded back to a tile list).
    /// Other seats' hands are not included — StateSnapshot.Hand is only the observer's.
    /// </summary>
    public StateSnapshot ToSnapshot(int observerSeat, ActionFlags legal)
    {
        // Build observer's hand: 13 sorted tiles + optionally the drawn 14th at slot 13.
        var sortedHand = new List<Tile>();
        for (int k = 0; k < Tile.Count34; k++)
            for (int c = 0; c < ClosedCounts[observerSeat][k]; c++)
                sortedHand.Add(Tile.FromId(k));

        // If observer is the current seat and has drawn, the "extra" tile isn't separately
        // tracked here — we store all 14 in ClosedCounts. Use the count as-is.

        // Seat-relative ordering: 0=self, 1=shimocha, 2=toimen, 3=kamicha.
        var seatsRelative = new SeatView[4];
        var scoresRelative = new int[4];
        for (int rel = 0; rel < 4; rel++)
        {
            int abs = (observerSeat + rel) % 4;
            seatsRelative[rel] = new SeatView(
                Discards[abs].ToArray(),
                DiscardIsTedashi[abs].ToArray(),
                Melds[abs].ToArray(),
                Riichi[abs],
                Riichi[abs] ? 0 : -1,     // riichi discard index simplified
                Ippatsu: false,
                IsTenpaiCalled: false);
            scoresRelative[rel] = Scores[abs];
        }

        return StateSnapshot.Empty with
        {
            Hand = sortedHand,
            OurMelds = Melds[observerSeat].ToArray(),
            OurSeat = observerSeat,
            RoundWind = Round,
            Honba = Honba,
            RiichiSticks = RiichiSticks,
            Scores = scoresRelative,
            DoraIndicators = new[] { DoraIndicator },
            WallRemaining = Wall.Count,
            DealerSeat = Dealer,
            Seats = seatsRelative,
            Legal = new LegalActions(legal, [], [], [], []),
        };
    }
}
