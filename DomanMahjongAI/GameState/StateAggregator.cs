using DomanMahjongAI.Engine;
using System;

namespace DomanMahjongAI.GameState;

/// <summary>
/// Owns the latest <see cref="StateSnapshot"/>. Rebuilds on each framework tick
/// (throttled) and also on every lifecycle event from the reader. Fires
/// <see cref="Changed"/> only when the snapshot actually differs.
/// </summary>
public sealed class StateAggregator : IDisposable
{
    private readonly AddonEmjReader reader;
    private bool disposed;
    private long lastRebuildTicks;
    private const long MinTickIntervalTicks = 160_000;  // ~16ms

    public StateSnapshot? Latest { get; private set; }

    public event Action<StateSnapshot>? Changed;

    public StateAggregator(AddonEmjReader reader)
    {
        this.reader = reader;
        this.reader.ObservationChanged += OnObservationChanged;

        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        Plugin.Framework.Update -= OnFrameworkUpdate;
        reader.ObservationChanged -= OnObservationChanged;
    }

    private void OnObservationChanged(AddonEmjObservation _) => Rebuild();

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework _)
    {
        long now = DateTime.UtcNow.Ticks;
        if (now - lastRebuildTicks < MinTickIntervalTicks) return;
        lastRebuildTicks = now;
        Rebuild();
    }

    private void Rebuild()
    {
        var next = reader.TryBuildSnapshot();
        if (next is null) return;
        if (next.SchemaVersion != StateSnapshot.CurrentSchemaVersion) return;

        if (Latest is null || !SnapshotEqual(Latest, next))
        {
            Latest = next;
            Changed?.Invoke(next);
        }
    }

    /// <summary>
    /// Reference-compare first, then structural compare of the few fields that
    /// actually differ frame-to-frame. Cheap because StateSnapshot records are
    /// constructed rarely (only on addon events).
    /// </summary>
    private static bool SnapshotEqual(StateSnapshot a, StateSnapshot b)
    {
        if (ReferenceEquals(a, b)) return true;
        // Record-based equality would deep-compare the enumerables correctly,
        // but it's not cheap. Defer to the default until we see a profiler hit.
        return a == b;
    }
}
