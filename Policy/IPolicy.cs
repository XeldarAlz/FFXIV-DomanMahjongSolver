using DomanMahjongAI.Engine;

namespace DomanMahjongAI.Policy;

public interface IPolicy
{
    ActionChoice Choose(StateSnapshot state);
}
