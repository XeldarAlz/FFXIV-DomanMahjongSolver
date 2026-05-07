namespace Mahjong.Plugin.Game;

/// <summary>
/// Reasons an <see cref="IAddonReader.Read"/> can fail. Specific enough that
/// the UI / logging layer can distinguish "addon is closed (idle)" from
/// "addon is open but on a layout we don't know yet (variant mismatch)" and
/// surface the right diagnostic to the user.
/// </summary>
public enum ReadError
{
    /// <summary>The Mahjong addon isn't loaded — the player isn't at a table.</summary>
    AddonMissing,

    /// <summary>The addon exists but isn't visible (transitioning, hidden by another window).</summary>
    NotVisible,

    /// <summary>No registered <see cref="IVariantStrategy"/>'s probe matched the live layout.</summary>
    VariantMismatch,

    /// <summary>The variant probe matched but the read produced implausible values.</summary>
    ProbeFailed,

    /// <summary>Read attempted but the addon's internal state isn't ready for reading yet.</summary>
    ProbeTimeout,

    /// <summary>An unexpected exception was thrown during the read; details in the event log.</summary>
    Unexpected,
}
