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
/// <para><b>Per-snapshot scope (~6 KB):</b>
/// <list type="bullet">
///   <item><c>AtkUnitBase</c> header + a 4 KB tail (covers AddonEmj's known
///   size, including all four seat blocks at layout offsets ~0x04FE / 0x07DE
///   / 0x0ABE / 0x0D9E for the released Emj variant).</item>
///   <item><c>RootNode</c> header + the live ~0x300 region. Empirical byte
///   variability across a 77-min capture (461e8ece, 2026-05-10) showed
///   everything past 0x400 in the root node is near-static node-tree
///   plumbing — capturing 0x1000 there wastes ~3 KB per snapshot.</item>
///   <item>Each observed seat-pool struct from <see cref="SeatPoolRegistry"/>
///   (4 KB each, up to 4 seats). Currently empty in production: the only
///   producer of pool addresses is the asm-hook discard capture (see
///   <see cref="Hooks.Strategies.NativeAsmDiscardCapture"/>), which the
///   factory keeps disabled because the 2026-04-27 sig collides with idle
///   game code on post-2026-05 builds. Per-seat data is still captured —
///   it lives inside the addon dump above; the heap-allocated pool structs
///   the asm hook used to expose are separate and currently invisible.</item>
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

    // The state-change cadence captures three discrete addon "shapes" by
    // AtkValuesCount: 50 (lobby/idle), 73 (menu/transition), and 109 (active
    // hand). The 2026-05 corpus showed 50/73 dumps account for ~32% of all
    // memdump traffic without carrying any signal the gameplay layer can
    // use — every interesting field (hand, seat blocks, dora) is only
    // populated in the 109-bucket. Drop ticks below this threshold for
    // state-change captures; explicit pre-discard / post-call captures
    // still go through (they're rarer and the count can be transient at
    // those moments).
    internal const int MinAtkValuesForStateChangeDump = 100;

    private const int AddonDumpBytes = 0x300 + 0x1000; // header + 4 KB tail (covers all 4 seat blocks)
    // Root node: header + ~0x300 of live region. The full 0x1000 we used to
    // capture is mostly a static AtkNode tree on the released build — a
    // 77-min capture (461e8ece, 2026-05-10) found two real signal bytes
    // (~0x310, ~0x3F0) and a long lockstep-pointer cluster at 0x280..0x3B0
    // that only flips on UI re-allocation. Past 0x400 was 95%+ dead.
    private const int RootDumpBytes = 0x400;
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
    /// Capture a snapshot tagged with <paramref name="reason"/>. Standard
    /// reasons are <c>"state-change"</c> (the high-frequency cadence,
    /// gated on <c>AtkValuesCount</c>) and the input-bracket pair
    /// <c>"input-pre"</c> / <c>"input-post"</c> (fired around every
    /// FireCallback against the Mahjong addon — used during RE sessions to
    /// diff addon bytes that mutate in lockstep with click-driven
    /// state). Pre/post captures bypass the atk_count gate so we never
    /// miss the bracketing snapshots.
    ///
    /// <para>Safe to call from any thread; the heavy work (memcpy +
    /// hashing + JSON serialize) runs on the calling thread, and
    /// bad-pointer reads are caught and surfaced through the error
    /// sink.</para>
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

            // Gate the high-frequency state-change cadence on AtkValuesCount.
            // Other reasons (input-pre / input-post bracketing FireCallback
            // events) are coarse, rare, and useful even mid-transition — let
            // them through unconditionally so we never miss a labeled pair.
            bool isStateChange = reason == "state-change";
            if (isStateChange && unit->AtkValuesCount < MinAtkValuesForStateChangeDump)
                return;

            var entry = BuildEntry(reason, unit);

            // Hash-dedup the high-frequency state-change cadence so the same
            // byte layout doesn't ship twice per session. Explicit-reason
            // dumps (input-pre / input-post and any future tagged reasons)
            // skip dedup: the value of those captures is precisely the
            // (reason, t, hash) tuple — collapsing two clicks that happened
            // to land on the same memory layout would defeat the bracket.
            if (isStateChange)
            {
                if (seenHashes.Contains(entry.Hash))
                    return;
                seenHashes.Add(entry.Hash);
            }

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
        // varies across client variants. Bounded to RootDumpBytes; deeper
        // nodes can be reconstructed offline from the address graph in the
        // addon dump itself.
        byte[]? rootBytes = null;
        nint rootAddr = 0;
        if (unit->RootNode != null)
        {
            rootAddr = (nint)unit->RootNode;
            rootBytes = SafeReadBytes(rootAddr, RootDumpBytes);
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

        List<SeatPoolDump>? pools = null;
        foreach (var poolBase in seatPools.Bases)
        {
            var poolBytes = SafeReadBytes(poolBase, SeatPoolDumpBytes);
            if (poolBytes is null)
                continue;
            pools ??= new List<SeatPoolDump>(4);
            pools.Add(new SeatPoolDump(
                Address: poolBase.ToInt64(),
                BytesB64: Convert.ToBase64String(poolBytes)));
        }

        var hash = ComputeHash(addonBytes, rootBytes, atkBytes, pools);

        // Layout-aware metadata. Both fields stay null until the variant
        // selector has resolved a layout this session — pre-resolution
        // snapshots (rare; only the first few ticks of a session) ship
        // without them and analyzers can fall back to addon-name lookup.
        var layout = reader.ActiveLayout;
        var seatOffsets = layout is null ? null : new AddonSeatOffsets(
            SelfCount: layout.Offsets.SelfDiscardCountByte,
            SelfScore: layout.Offsets.SelfScore,
            ShimochaCount: layout.Offsets.ShimochaDiscardCountByte,
            ShimochaScore: layout.Offsets.ShimochaScore,
            ToimenCount: layout.Offsets.ToimenDiscardCountByte,
            ToimenScore: layout.Offsets.ToimenScore,
            KamichaCount: layout.Offsets.KamichaDiscardCountByte,
            KamichaScore: layout.Offsets.KamichaScore,
            HandArrayStart: layout.Offsets.HandArrayStart);

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
            Variant: layout?.Name,
            AddonSeatOffsets: seatOffsets,
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
        byte[]? addonBytes, byte[]? rootBytes, byte[]? atkBytes, List<SeatPoolDump>? pools)
    {
        using var sha = SHA256.Create();
        if (addonBytes is not null)
            sha.TransformBlock(addonBytes, 0, addonBytes.Length, null, 0);
        if (rootBytes is not null)
            sha.TransformBlock(rootBytes, 0, rootBytes.Length, null, 0);
        if (atkBytes is not null)
            sha.TransformBlock(atkBytes, 0, atkBytes.Length, null, 0);
        if (pools is not null)
        {
            foreach (var p in pools)
            {
                var b = Convert.FromBase64String(p.BytesB64);
                sha.TransformBlock(b, 0, b.Length, null, 0);
            }
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
        // Null (and omitted via JsonIgnoreCondition.WhenWritingNull) when no
        // seat-pool addresses have been observed this session — the typical
        // case on the released build, where the asm-hook producer is offline.
        // Keeping the field as nullable rather than ripping it out preserves
        // forward compatibility for when the asm hook lands a verified sig.
        [property: JsonPropertyName("seat_pools")] List<SeatPoolDump>? SeatPools,
        // Variant name ("Emj" / "EmjL" / ...) and per-seat byte offsets into
        // the addon dump. Both null until the variant selector has resolved a
        // layout this session. Lets offline analyzers slice the four seat
        // blocks out of `addon_b64` without name-guessing the layout file.
        [property: JsonPropertyName("variant")] string? Variant,
        [property: JsonPropertyName("addon_seat_offsets")] AddonSeatOffsets? AddonSeatOffsets,
        [property: JsonPropertyName("hash")] string Hash);

    private sealed record SeatPoolDump(
        [property: JsonPropertyName("addr")] long Address,
        [property: JsonPropertyName("b64")] string BytesB64);

    /// <summary>
    /// Per-seat byte offsets into the addon dump (<c>addon_b64</c>). The
    /// score field is the start of a 4-byte int; the count_byte field is a
    /// single byte two bytes before the score. Hand array is the player's
    /// closed hand, 14 × 4-byte ints starting at <c>hand_array_start</c>.
    /// </summary>
    private sealed record AddonSeatOffsets(
        [property: JsonPropertyName("self_count_byte")] int SelfCount,
        [property: JsonPropertyName("self_score")] int SelfScore,
        [property: JsonPropertyName("shimocha_count_byte")] int ShimochaCount,
        [property: JsonPropertyName("shimocha_score")] int ShimochaScore,
        [property: JsonPropertyName("toimen_count_byte")] int ToimenCount,
        [property: JsonPropertyName("toimen_score")] int ToimenScore,
        [property: JsonPropertyName("kamicha_count_byte")] int KamichaCount,
        [property: JsonPropertyName("kamicha_score")] int KamichaScore,
        [property: JsonPropertyName("hand_array_start")] int HandArrayStart);
}
