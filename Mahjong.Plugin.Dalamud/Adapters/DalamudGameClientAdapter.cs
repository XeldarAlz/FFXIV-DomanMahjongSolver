namespace Mahjong.Plugin.Dalamud.Adapters;

/// <summary>
/// Concrete <see cref="IGameClientAdapter"/> for the Dalamud-hosted plugin.
/// Composes the individual service adapters into the umbrella facade that
/// plugin domain code consumes.
///
/// Phase 7.B will add accessors here for additional Dalamud services as the
/// remaining plugin code migrates off the static <c>Plugin.X</c> properties.
/// </summary>
internal sealed class DalamudGameClientAdapter : IGameClientAdapter
{
    public IFrameworkScheduler Scheduler { get; }
    public IEventLog Log { get; }

    public DalamudGameClientAdapter(IFrameworkScheduler scheduler, IEventLog log)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(log);
        Scheduler = scheduler;
        Log = log;
    }
}
