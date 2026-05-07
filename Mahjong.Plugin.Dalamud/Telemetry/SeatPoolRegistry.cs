using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Mahjong.Plugin.Dalamud.Telemetry;

/// <summary>
/// Append-only registry of seat-pool struct addresses observed at runtime,
/// populated by the asm-hook discard capture. The asm trampoline writes
/// R14 (the per-seat pool base) into a ring buffer on every discard;
/// <see cref="Hooks.Strategies.NativeAsmDiscardCapture"/> drains the ring,
/// pushes each unique address through <see cref="Observe"/>, and the
/// memory-dump recorder reads <see cref="Bases"/> when capturing snapshots.
///
/// <para>Doman has four seats, so the registry plateaus at four entries
/// once a hand has cycled through. Lock-free reads are fine — values are
/// only ever appended, and consumers re-read on every dump.</para>
/// </summary>
public sealed class SeatPoolRegistry
{
    private readonly ConcurrentDictionary<nint, byte> bases = new();

    /// <summary>Live snapshot of observed pool bases. Stable order is not
    /// guaranteed.</summary>
    public IReadOnlyCollection<nint> Bases => (IReadOnlyCollection<nint>)bases.Keys;

    /// <summary>
    /// Record a freshly-observed pool address. Dedupes; safe to call
    /// from any thread (the asm hook drain runs on the framework thread).
    /// Zero / negative addresses are dropped — the asm trampoline can
    /// momentarily write garbage during a torn read, and we'd rather miss
    /// one event than crash on a bad pointer dereference downstream.
    /// </summary>
    public void Observe(nint poolBase)
    {
        if (poolBase == 0 || poolBase == -1)
            return;
        bases.TryAdd(poolBase, 0);
    }
}
