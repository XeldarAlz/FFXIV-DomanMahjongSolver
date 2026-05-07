namespace Mahjong.Plugin.Game;

/// <summary>
/// Reads the live mahjong addon's state into a typed snapshot. The
/// Dalamud-coupled implementation lives in <c>Mahjong.Plugin.Dalamud</c>;
/// tests can stub this with a fake that returns canned snapshots.
///
/// Returns <see cref="Result{TValue, TError}"/> rather than nullable so
/// callers see a typed reason for every miss instead of guessing why null
/// came back.
/// </summary>
public interface IAddonReader
{
    /// <summary>Read the current state, or a typed reason why we can't.</summary>
    Result<StateSnapshot, ReadError> Read();
}
