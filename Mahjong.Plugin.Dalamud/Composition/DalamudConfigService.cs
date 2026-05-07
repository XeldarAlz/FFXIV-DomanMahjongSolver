using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Composition;

/// <summary>
/// Default <see cref="IConfigService{TConfig}"/>. Holds the live configuration
/// reference, applies a transform inside a lock so two ImGui handlers on the
/// framework thread can't interleave, persists via the supplied
/// <paramref name="save"/> callback, then swaps the public reference and
/// fires <see cref="Changed"/>.
///
/// <para>The save delegate is the only collaborator: in the live plugin it
/// closes over <c>IDalamudPluginInterface.SavePluginConfig</c>; in tests it
/// closes over a recording list. Keeping the seam this small (one method,
/// one type) lets the mock-adapter test suite skip Dalamud entirely.</para>
///
/// <para>The lock is cheap and lets us keep the contract synchronous: every
/// caller sees a consistent <see cref="Current"/> the moment <see cref="Update"/>
/// returns, with no async-over-sync ImGui pitfalls.</para>
/// </summary>
public sealed class DalamudConfigService : IConfigService<Configuration>
{
    private readonly Action<Configuration> save;
    private readonly object updateLock = new();

    public DalamudConfigService(Action<Configuration> save, Configuration initial)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(initial);
        this.save = save;
        Current = initial;
    }

    public Configuration Current { get; private set; }

    public event Action<Configuration>? Changed;

    public void Update(Func<Configuration, Configuration> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        Configuration next;
        lock (updateLock)
        {
            next = mutate(Current)
                ?? throw new InvalidOperationException(
                    "Configuration mutator returned null.");

            // Persist before swapping — if the save callback throws, the
            // live reference stays at the last good value.
            save(next);
            Current = next;
        }

        Changed?.Invoke(next);
    }
}
