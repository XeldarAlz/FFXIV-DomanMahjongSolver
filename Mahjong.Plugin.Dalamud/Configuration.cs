using System;
using Dalamud.Configuration;

namespace Mahjong.Plugin.Dalamud;

/// <summary>
/// Persisted plugin configuration. Immutable record — every change goes
/// through <see cref="Mahjong.Plugin.Game.IConfigService{TConfig}.Update"/>,
/// which produces a new instance via <c>with</c>, persists it through
/// Dalamud, and atomically swaps the live reference. UI handlers and
/// commands should never reach the underlying instance directly.
///
/// <para><see cref="Version"/> stays mutable because Dalamud's
/// <see cref="IPluginConfiguration"/> interface declares it that way; the
/// migration runner is the only legitimate writer (on load), so the rest
/// of the plugin treats it as read-only.</para>
/// </summary>
[Serializable]
public sealed record Configuration : IPluginConfiguration
{
    /// <summary>
    /// Schema version this code understands. Bump when fields are added,
    /// removed, or change meaning, and add a matching <c>IConfigMigrator</c>
    /// step.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Persisted schema version. Mutable per <see cref="IPluginConfiguration"/>
    /// contract; only the migration runner should write to it.
    /// </summary>
    public int Version { get; set; } = CurrentSchemaVersion;

    public bool AutomationArmed { get; init; } = false;

    public bool SuggestionOnly { get; init; } = true;

    public string PolicyTier { get; init; } = "efficiency";

    public bool TosAccepted { get; init; } = false;

    /// <summary>
    /// When true, the MainWindow exposes a "Developer tools" section that opens
    /// the debug overlay (live state, dispatch tests, memory dumps). End-user
    /// builds leave this false.
    /// </summary>
    public bool DevMode { get; init; } = false;

    /// <summary>Target median delay (ms) between auto-play actions.</summary>
    public int HumanizedDelayMs { get; init; } = 1200;

    /// <summary>
    /// Draw a colored box + arrow on the recommended discard tile directly in the
    /// Doman Mahjong game UI. Intended as the primary cue in "Suggestions" mode so
    /// users don't have to parse shanten/ukeire numbers.
    /// </summary>
    public bool ShowInGameHighlight { get; init; } = true;

    /// <summary>
    /// When true, the main window shows the shanten / ukeire / score table under the
    /// headline pick. Defaults off — most users just want the "discard X" cue.
    /// </summary>
    public bool ShowSuggestionDetails { get; init; } = false;

    /// <summary>
    /// Write per-hand NDJSON game logs under <c>pluginConfigs/Mahjong.Plugin.Dalamud/games/</c>.
    /// Feeds the Doman-specific training corpus for later supervised policy learning.
    /// On by default during development; opt-out for end-user builds that want to
    /// skip the disk writes.
    /// </summary>
    public bool EnableGameLogging { get; init; } = true;

    /// <summary>
    /// Stable, anonymous identifier for this install. Generated once on first
    /// migration to v2 and never changes for the life of the plugin config.
    /// Sent as <c>X-Install-Id</c> on every telemetry upload so the server can
    /// dedupe and rate-limit per install without ever seeing a character or
    /// Content ID. <see cref="Guid.Empty"/> means "not yet minted" — the
    /// uploader treats that as a fatal init error and skips the upload.
    /// </summary>
    public Guid InstallId { get; init; } = Guid.Empty;
}
