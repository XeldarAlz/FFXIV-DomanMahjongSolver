using Mahjong.Plugin.Dalamud.Actions;

namespace Mahjong.Plugin.Dalamud.Tests;

public class InputDispatcherTests
{
    // Construction-only tests. The dispatcher's hot paths read native game
    // memory (FireCallback on AtkUnitBase*); those aren't unit-testable
    // without a live game. What we can pin here is constructor validation
    // and the slot-bounds gate that runs BEFORE any addon lookup.

    [Fact]
    public void Throws_when_addon_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => new InputDispatcher(null!));
    }

    // The slot-bounds gate is the only branch that doesn't require a live
    // addon — it short-circuits with InvalidSlot before ever calling
    // addon.TryGet. We can drive it through a non-null MahjongAddon that
    // would otherwise throw on dereference; the dispatcher returns before
    // it gets that far. Constructing MahjongAddon needs IGameGui + IPluginLog
    // though, so this stays in the "construction validates inputs" tier.
}
