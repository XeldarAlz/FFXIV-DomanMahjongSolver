namespace Mahjong.Plugin.Game;

/// <summary>
/// Read/write façade over the persisted plugin configuration. The configuration
/// record itself is owned by <c>Mahjong.Plugin.Dalamud</c> (Dalamud's
/// <c>IPluginConfiguration</c> requirement); this interface exposes the
/// read-write contract every other class needs.
///
/// Synchronous because Dalamud's <c>SavePluginConfig</c> is synchronous, and
/// ImGui handlers run on the framework thread. The atomicity guarantee is
/// "validate + mutate + persist in one call" — no cross-thread story needed.
/// </summary>
/// <typeparam name="TConfig">The plugin's configuration record (immutable).</typeparam>
public interface IConfigService<TConfig> where TConfig : class
{
    /// <summary>Current configuration. Returned reference is immutable.</summary>
    TConfig Current { get; }

    /// <summary>
    /// Apply a transform, persist, and atomically swap the <see cref="Current"/>
    /// reference. Subscribers to <see cref="Changed"/> are notified after the
    /// swap completes.
    /// </summary>
    void Update(Func<TConfig, TConfig> mutate);

    /// <summary>Fired after a successful update. Subscribers run on the caller's thread.</summary>
    event Action<TConfig>? Changed;
}
