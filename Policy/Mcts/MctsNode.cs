using DomanMahjongAI.Engine;
using System.Collections.Generic;

namespace DomanMahjongAI.Policy.Mcts;

/// <summary>
/// Node in the MCTS tree. Decision-node variant: represents "after the action
/// <see cref="Action"/> was taken from the parent". Root is a special node with
/// null Action. Children are populated on expansion.
/// </summary>
internal sealed class MctsNode
{
    public StateSnapshot State { get; }
    public MctsNode? Parent { get; }
    public Tile? Action { get; }

    public int Visits { get; set; }
    public double TotalValue { get; set; }
    public bool Expanded { get; set; }

    public List<MctsNode> Children { get; } = new();

    public MctsNode(StateSnapshot state, MctsNode? parent, Tile? action)
    {
        State = state;
        Parent = parent;
        Action = action;
    }

    public double MeanValue => Visits == 0 ? 0 : TotalValue / Visits;

    /// <summary>UCB1 score with parent N used for exploration.</summary>
    public double Ucb1(int parentVisits, double c)
    {
        if (Visits == 0) return double.PositiveInfinity;
        double exploit = MeanValue;
        double explore = c * System.Math.Sqrt(System.Math.Log(parentVisits) / Visits);
        return exploit + explore;
    }
}
