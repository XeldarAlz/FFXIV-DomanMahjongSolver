using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Telemetry;
using Mahjong.Plugin.Game;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;

namespace Mahjong.Plugin.Dalamud.Hooks.Strategies;

/// <summary>
/// Primary <see cref="IDiscardCapture"/> strategy. Hooks a single mid-function
/// instruction inside the Doman discard handler and writes (R14 = pool base,
/// EAX = tile_id) tuples into an unmanaged 64-slot ring buffer. A framework
/// tick drains the ring and fires <see cref="DiscardObserved"/> for each new
/// entry.
///
/// Verified empirically via Cheat Engine on 2026-04-27:
/// <code>
///   ffxiv_dx11.exe + 0x1A20A36..0x1A20A49:
///     41 FF 86 00 10 00 00     inc [r14+0x1000]    ; pool's discard count++
///     8B 85 90 00 00 00         mov eax,[rbp+0x90]   ; load tile_id
///     41 89 86 04 10 00 00     mov [r14+0x1004],eax ; pool's latest tile_id = eax
/// </code>
///
/// The asm trampoline is intentionally minimal — 7 register pushes, no calls,
/// no managed transitions — so it can't break under mid-function stack
/// alignment. Earlier attempts to call back into managed code from the hook
/// site failed silently.
///
/// <para>Seat resolution: this strategy doesn't know which seat a pool base
/// belongs to (the asm site only sees R14 = the per-seat pool struct). It
/// reports <see cref="DiscardEvent.Seat"/> = -1; downstream code that needs
/// seat attribution either relies on the addon-poll fallback or maps pool
/// bases to seats via observed snapshots elsewhere.</para>
/// </summary>
public sealed class NativeAsmDiscardCapture : IDiscardCapture
{
    public const string Name = "native-asm";

    // Distinctive 20 bytes spanning the inc + load + store sequence inside
    // the discard handler. inc is 7 bytes, the [rbp+0x90] load is 6, the
    // [r14+0x1004] store is 7 — totaling 20 bytes the AOB scanner matches on.
    // Public so <see cref="SigscanProbe"/> can probe the same pattern without
    // instantiating the full asm-hook strategy.
    public const string DiscardSig =
        "41 FF 86 00 10 00 00 8B 85 90 00 00 00 41 89 86 04 10 00 00";

    // Ring buffer layout (in unmanaged memory):
    //   +0x00..+0x07   uint64 head_index (monotonic, slot = head & MASK)
    //   +0x08..+0x0F   reserved
    //   +0x10..+0x10+(SLOTS*16)  slot[i] = (uint64 R14, int32 tileId, int32 _pad)
    private const int RingSlots = 64;
    private const int RingMask = 63;
    private const int SlotSize = 16;
    private const int RingDataOffset = 16;
    private const int BufferSize = RingDataOffset + RingSlots * SlotSize;

    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly SeatPoolRegistry? seatPools;
    private readonly ISigprobeLog sigprobes;
    private nint buffer;
    private IAsmHook? asmHook;
    private bool disposed;
    private ulong lastReadHead;
    private ulong totalCaptured;
    private int lastTileId = -1;

    public HookHealth Health { get; private set; } = HookHealth.Offline;
    public string StrategyName => Name;
    public ulong TotalCaptured => totalCaptured;
    public int LastTileId => lastTileId;
    public event Action<DiscardEvent>? DiscardObserved;

    /// <summary>
    /// Diagnostic: total times the asm trampoline has fired (read directly
    /// from the unmanaged head counter). May exceed
    /// <see cref="TotalCaptured"/> by the contents of an unread drain.
    /// </summary>
    public unsafe ulong NativeHitCount =>
        buffer == 0 ? 0 : *(ulong*)buffer;

    /// <summary>
    /// Construct the asm-hook strategy. <paramref name="seatPools"/> is
    /// optional — when supplied, every drained ring slot's R14 (per-seat
    /// pool base) is forwarded to the registry so the memory-dump recorder
    /// can include those structs in its snapshots. The factory passes it in
    /// for the live plugin; tests / poll-fallback paths that don't dump
    /// memory pass null.
    /// </summary>
    public NativeAsmDiscardCapture(
        IPluginLog log,
        IFramework framework,
        ISigScanner sigScanner,
        SeatPoolRegistry? seatPools = null,
        ISigprobeLog? sigprobes = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(sigScanner);
        this.log = log;
        this.framework = framework;
        this.seatPools = seatPools;
        this.sigprobes = sigprobes ?? NullSigprobeLog.Instance;

        AllocateBuffer();
        if (!TryActivateHook(sigScanner))
            return;

        Health = HookHealth.Active;
        framework.Update += OnFrameworkUpdate;
    }

    private void AllocateBuffer()
    {
        buffer = Marshal.AllocHGlobal(BufferSize);
        unsafe
        {
            var p = (byte*)buffer;
            for (int i = 0; i < BufferSize; i++)
                p[i] = 0;
        }
    }

    private bool TryActivateHook(ISigScanner sigScanner)
    {
        nint matchAddress;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            matchAddress = sigScanner.ScanText(DiscardSig);
            sw.Stop();
            sigprobes.Record(
                sigName: "doman.discard-handler",
                pattern: DiscardSig,
                matchAddress: matchAddress,
                elapsedMs: sw.Elapsed.TotalMilliseconds,
                success: true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            sigprobes.Record(
                sigName: "doman.discard-handler",
                pattern: DiscardSig,
                matchAddress: 0,
                elapsedMs: sw.Elapsed.TotalMilliseconds,
                success: false,
                errorMessage: ex.Message);
            log.Error($"[DiscardCapture/native-asm] sigscan failed: {ex.Message}");
            return false;
        }

        // Hook AT the `mov [r14+0x1004], eax` instruction, offset +13 from
        // sig start. ExecuteFirst means our code runs BEFORE the original
        // mov; EAX already holds tile_id (loaded by the preceding mov
        // eax,[rbp+0x90]) so reading it is safe.
        nint hookSite = matchAddress + 13;
        var asmBytes = BuildTrampoline((ulong)buffer);

        try
        {
            var hooks = Reloaded.Hooks.ReloadedHooks.Instance;
            asmHook = hooks
                .CreateAsmHook(asmBytes, (long)hookSite, AsmHookBehaviour.ExecuteFirst)
                .Activate();
            log.Info(
                $"[DiscardCapture/native-asm] activated at 0x{hookSite:X} " +
                $"(sig at 0x{matchAddress:X}, ring buffer at 0x{buffer:X}).");
            return true;
        }
        catch (Exception ex)
        {
            sigprobes.Record(
                sigName: "doman.discard-handler-asmhook",
                pattern: DiscardSig,
                matchAddress: hookSite,
                elapsedMs: 0,
                success: false,
                errorMessage: ex.Message);
            log.Error($"[DiscardCapture/native-asm] failed to activate: {ex}");
            asmHook = null;
            return false;
        }
    }

    /// <summary>
    /// Trampoline that writes (R14, EAX) to the next ring slot.
    /// Stack: 4 pushes (rax, rcx, rdx, r8) = 32 bytes — keeps RSP aligned.
    /// No call, no managed transition — pure native increments and stores.
    /// <code>
    ///   push rax
    ///   push rcx
    ///   push rdx
    ///   push r8
    ///   mov  rcx, buffer_base
    ///   mov  rdx, [rcx]                ; rdx = head
    ///   lea  r8,  [rdx+1]
    ///   mov  [rcx], r8                 ; head++
    ///   and  rdx, RingMask
    ///   shl  rdx, 4                    ; * SlotSize (16)
    ///   lea  rcx, [rcx + rdx + 0x10]   ; slot ptr
    ///   mov  [rcx], r14
    ///   mov  [rcx+8], eax
    ///   pop  r8
    ///   pop  rdx
    ///   pop  rcx
    ///   pop  rax
    /// </code>
    /// </summary>
    private static byte[] BuildTrampoline(ulong bufferAddress) =>
    [
        0x50,                                        // push rax
        0x51,                                        // push rcx
        0x52,                                        // push rdx
        0x41, 0x50,                                  // push r8

        0x48, 0xB9,                                  // mov rcx, imm64
            (byte)(bufferAddress        & 0xFF),
            (byte)((bufferAddress >>  8) & 0xFF),
            (byte)((bufferAddress >> 16) & 0xFF),
            (byte)((bufferAddress >> 24) & 0xFF),
            (byte)((bufferAddress >> 32) & 0xFF),
            (byte)((bufferAddress >> 40) & 0xFF),
            (byte)((bufferAddress >> 48) & 0xFF),
            (byte)((bufferAddress >> 56) & 0xFF),

        0x48, 0x8B, 0x11,                            // mov rdx, [rcx]
        0x4C, 0x8D, 0x42, 0x01,                      // lea r8, [rdx+1]
        0x4C, 0x89, 0x01,                            // mov [rcx], r8
        0x48, 0x83, 0xE2, RingMask,                  // and rdx, 0x3F
        0x48, 0xC1, 0xE2, 0x04,                      // shl rdx, 4
        0x48, 0x8D, 0x4C, 0x11, RingDataOffset,      // lea rcx, [rcx + rdx + 0x10]

        0x4C, 0x89, 0x31,                            // mov [rcx], r14
        0x89, 0x41, 0x08,                            // mov [rcx+8], eax

        0x41, 0x58,                                  // pop r8
        0x5A,                                        // pop rdx
        0x59,                                        // pop rcx
        0x58,                                        // pop rax
    ];

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (buffer == 0 || disposed)
            return;
        var drained = DrainRingBuffer();
        if (drained.Count == 0)
            return;
        var now = DateTime.UtcNow;
        foreach (var (poolBase, tileId, seq) in drained)
        {
            totalCaptured++;
            lastTileId = tileId;
            // Register the observed seat-pool address regardless of tile
            // validity — even a torn read tells us a real per-seat struct
            // sits at that address, and the registry dedupes anyway.
            seatPools?.Observe(poolBase);
            if (tileId < 0 || tileId >= Tile.Count34)
                continue;
            DiscardObserved?.Invoke(new DiscardEvent(
                Seat: -1,
                Tile: Tile.FromId(tileId),
                ObservedAtUtc: now,
                SequenceNumber: seq));
        }
    }

    private unsafe List<(nint PoolBase, int TileId, ulong Seq)> DrainRingBuffer()
    {
        ulong head = *(ulong*)buffer;
        if (head == lastReadHead)
            return new(0);

        ulong startSeq = lastReadHead;
        ulong endSeq = head;
        if (endSeq - startSeq > (ulong)RingSlots)
            startSeq = endSeq - (ulong)RingSlots;

        var result = new List<(nint, int, ulong)>((int)(endSeq - startSeq));
        for (ulong seq = startSeq; seq < endSeq; seq++)
        {
            ulong slotIdx = seq & (ulong)RingMask;
            byte* slot = (byte*)(buffer + RingDataOffset + (long)slotIdx * SlotSize);
            nint poolBase = (nint)(*(ulong*)slot);
            int tileId = *(int*)(slot + 8);
            result.Add((poolBase, tileId, seq));
        }
        lastReadHead = head;
        return result;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        if (Health == HookHealth.Active)
            framework.Update -= OnFrameworkUpdate;

        try
        { asmHook?.Disable(); }
        catch { }
        asmHook = null;

        if (buffer != 0)
        {
            Marshal.FreeHGlobal(buffer);
            buffer = 0;
        }

        Health = HookHealth.Offline;
    }
}
