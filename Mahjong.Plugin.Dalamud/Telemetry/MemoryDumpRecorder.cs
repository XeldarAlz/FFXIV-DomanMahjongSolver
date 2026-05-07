using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Logging;

namespace Mahjong.Plugin.Dalamud.Telemetry;

/// <summary>
/// Captures labeled memory snapshots of the live Mahjong addon and ships
/// them through the telemetry pipeline so cross-client RE can be done
/// offline against a real corpus instead of one developer's reproductions.
///
/// <para><b>Per-snapshot scope (~64 KB):</b>
/// <list type="bullet">
///   <item><c>AtkUnitBase</c> header + a 4 KB tail (covers AddonEmj's known size).</item>
///   <item>Each observed seat-pool struct from <see cref="SeatPoolRegistry"/>
///   (4 KB each, up to 4 seats).</item>
///   <item>Full <c>AtkValues</c> array (variable, capped at 1024 entries).</item>
/// </list></para>
///
/// <para><b>Cadence:</b> every <c>StateAggregator.Changed</c> event triggers
/// a snapshot, and external callers (action dispatchers, FireCallback hook)
/// can call <see cref="Record"/> directly to label pre/post-action pairs.
/// Hash-dedup at the byte-level prevents the same layout shipping twice
/// per session — most state-change ticks won't actually move bytes.</para>
///
/// <para><b>Output:</b> NDJSON under <c>memdumps/memdumps-yyyyMMdd-HHmmss.ndjson</c>;
/// the file rolls every 1 MB so the uploader can ship slices incrementally
/// instead of waiting on one giant blob at session end.</para>
/// </summary>
public sealed class MemoryDumpRecorder : IDisposable
{
    public const int SchemaVersion = 1;
    private const int AddonDumpBytes = 0x300 + 0x1000; // header + 4 KB tail
    private const int SeatPoolDumpBytes = 0x1000;
    private const int MaxAtkValues = 1024;
    private const long FileRolloverBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AddonEmjReader reader;
    private readonly SeatPoolRegistry seatPools;
    private readonly ErrorSink errors;
    private readonly string memdumpsDir;
    private readonly HashSet<string> seenHashes = new();
    private readonly object writerLock = new();
    private string? currentPath;
    private long currentBytes;
    private long sequence;
    private bool disposed;

    public string MemdumpsDir => memdumpsDir;

    public MemoryDumpRecorder(
        AddonEmjReader reader,
        SeatPoolRegistry seatPools,
        ErrorSink errors,
        string pluginConfigDirectory)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(seatPools);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDirectory);
        this.reader = reader;
        this.seatPools = seatPools;
        this.errors = errors;
        memdumpsDir = Path.Combine(pluginConfigDirectory, "memdumps");
        try
        { Directory.CreateDirectory(memdumpsDir); }
        catch { }
    }

    public void Dispose() => disposed = true;

    /// <summary>
    /// Capture a snapshot tagged with <paramref name="reason"/> (e.g.
    /// <c>"state-change"</c>, <c>"pre-discard"</c>, <c>"post-call"</c>).
    /// Safe to call from any thread; the heavy work (memcpy + hashing +
    /// JSON serialize) runs on the calling thread, and bad-pointer reads
    /// are caught and surfaced through the error sink.
    /// </summary>
    public unsafe void Record(string reason)
    {
        if (disposed)
            return;

        try
        {
            var obs = reader.LastObservation;
            if (!obs.Present || obs.Address == 0)
                return;

            var unit = (AtkUnitBase*)obs.Address;
            var entry = BuildEntry(reason, unit);

            // Hash-dedup: identical-bytes snapshot shouldn't ship twice
            // per session. The hash spans every byte payload — if any
            // tile moves, the hash changes and we re-emit.
            if (seenHashes.Contains(entry.Hash))
                return;
            seenHashes.Add(entry.Hash);

            WriteEntry(entry);
        }
        catch (Exception ex)
        {
            errors.RecordException("MemoryDumpRecorder.Record", ex);
        }
    }

    private unsafe MemDumpEntry BuildEntry(string reason, AtkUnitBase* unit)
    {
        var addonBytes = SafeReadBytes((nint)unit, AddonDumpBytes);

        // RootNode dump — useful for node-id RE since the tree layout
        // varies across client variants. Bounded to 4 KB; deeper nodes
        // can be reconstructed offline from the address graph in the
        // addon dump itself.
        byte[]? rootBytes = null;
        nint rootAddr = 0;
        if (unit->RootNode != null)
        {
            rootAddr = (nint)unit->RootNode;
            rootBytes = SafeReadBytes(rootAddr, 0x1000);
        }

        // AtkValues — addon's parameter array. Bounded at MaxAtkValues
        // entries (each 0x10 bytes) to prevent runaway dumps if the
        // addon reports an absurd count due to memory corruption.
        int atkCount = Math.Min((int)unit->AtkValuesCount, MaxAtkValues);
        byte[]? atkBytes = null;
        nint atkAddr = 0;
        if (unit->AtkValues != null && atkCount > 0)
        {
            atkAddr = (nint)unit->AtkValues;
            atkBytes = SafeReadBytes(atkAddr, atkCount * sizeof(AtkValue));
        }

        var pools = new List<SeatPoolDump>();
        foreach (var poolBase in seatPools.Bases)
        {
            var poolBytes = SafeReadBytes(poolBase, SeatPoolDumpBytes);
            if (poolBytes is not null)
                pools.Add(new SeatPoolDump(
                    Address: poolBase.ToInt64(),
                    BytesB64: Convert.ToBase64String(poolBytes)));
        }

        var hash = ComputeHash(addonBytes, rootBytes, atkBytes, pools);

        return new MemDumpEntry(
            T: NowIso(),
            Seq: Interlocked.Increment(ref sequence),
            V: SchemaVersion,
            Reason: reason ?? "(none)",
            AddonAddress: ((nint)unit).ToInt64(),
            AddonBytesB64: addonBytes is null ? null : Convert.ToBase64String(addonBytes),
            RootNodeAddress: rootAddr.ToInt64(),
            RootNodeBytesB64: rootBytes is null ? null : Convert.ToBase64String(rootBytes),
            AtkValuesAddress: atkAddr.ToInt64(),
            AtkValuesCount: atkCount,
            AtkValuesBytesB64: atkBytes is null ? null : Convert.ToBase64String(atkBytes),
            SeatPools: pools,
            Hash: hash);
    }

    /// <summary>
    /// Memcpy a managed byte[] from an arbitrary address. Bad pointers
    /// (unmapped, freed, or just garbage) raise AccessViolationException
    /// from native code — we catch and return null so the snapshot still
    /// emits with the reachable parts.
    /// </summary>
    private static unsafe byte[]? SafeReadBytes(nint address, int length)
    {
        if (address == 0 || length <= 0)
            return null;
        try
        {
            var buf = new byte[length];
            Marshal.Copy(address, buf, 0, length);
            return buf;
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeHash(
        byte[]? addonBytes, byte[]? rootBytes, byte[]? atkBytes, List<SeatPoolDump> pools)
    {
        using var sha = SHA256.Create();
        if (addonBytes is not null)
            sha.TransformBlock(addonBytes, 0, addonBytes.Length, null, 0);
        if (rootBytes is not null)
            sha.TransformBlock(rootBytes, 0, rootBytes.Length, null, 0);
        if (atkBytes is not null)
            sha.TransformBlock(atkBytes, 0, atkBytes.Length, null, 0);
        foreach (var p in pools)
        {
            var b = Convert.FromBase64String(p.BytesB64);
            sha.TransformBlock(b, 0, b.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).Substring(0, 16);
    }

    private void WriteEntry(MemDumpEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOpts);
        lock (writerLock)
        {
            if (currentPath is null || currentBytes >= FileRolloverBytes)
                RollFile();
            try
            {
                using var w = new StreamWriter(new FileStream(
                    currentPath!, FileMode.Append, FileAccess.Write, FileShare.Read));
                w.WriteLine(line);
                currentBytes += line.Length + 1;
            }
            catch (Exception ex)
            {
                errors.RecordException("MemoryDumpRecorder.WriteEntry", ex);
            }
        }
    }

    private void RollFile()
    {
        var fn = $"memdumps-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.ndjson";
        currentPath = Path.Combine(memdumpsDir, fn);
        currentBytes = 0;
    }

    private static string NowIso() =>
        DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private sealed record MemDumpEntry(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("seq")] long Seq,
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("addon_addr")] long AddonAddress,
        [property: JsonPropertyName("addon_b64")] string? AddonBytesB64,
        [property: JsonPropertyName("root_addr")] long RootNodeAddress,
        [property: JsonPropertyName("root_b64")] string? RootNodeBytesB64,
        [property: JsonPropertyName("atk_addr")] long AtkValuesAddress,
        [property: JsonPropertyName("atk_count")] int AtkValuesCount,
        [property: JsonPropertyName("atk_b64")] string? AtkValuesBytesB64,
        [property: JsonPropertyName("seat_pools")] List<SeatPoolDump> SeatPools,
        [property: JsonPropertyName("hash")] string Hash);

    private sealed record SeatPoolDump(
        [property: JsonPropertyName("addr")] long Address,
        [property: JsonPropertyName("b64")] string BytesB64);
}
