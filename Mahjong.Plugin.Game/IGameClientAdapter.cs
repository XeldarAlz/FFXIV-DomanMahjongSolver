namespace Mahjong.Plugin.Game;

/// <summary>
/// Umbrella facade over Dalamud's plugin services. The concrete plugin shell
/// (<c>Mahjong.Plugin.Dalamud</c>) implements this; every other class in the
/// plugin layer takes <see cref="IGameClientAdapter"/> in its constructor
/// instead of touching <c>Plugin.Framework / Plugin.GameGui / Plugin.Log</c>
/// statics directly.
///
/// Phase 6.B replaces the ~40 static <c>Plugin.X</c> accesses across the
/// plugin layer with calls through this interface.
/// </summary>
public interface IGameClientAdapter
{
    IFrameworkScheduler Scheduler { get; }
    IEventLog Log { get; }
}
