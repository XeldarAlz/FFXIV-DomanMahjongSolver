namespace Mahjong.Plugin.Game;

public enum DispatchResult
{
    Ok,
    AddonNotFound,
    AddonNotVisible,
    InvalidSlot,
    HookFailed,
}

/// <summary>
/// Executes a chosen <see cref="ActionChoice"/> against the live game —
/// translating it into the appropriate Dalamud callback / native click /
/// FireCallback opcode. Implementation owns Dalamud thread-discipline; the
/// caller can be on any thread.
/// </summary>
public interface IActionDispatcher
{
    Task<DispatchResult> DispatchAsync(ActionChoice action, CancellationToken cancellationToken = default);
}
