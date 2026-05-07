namespace Mahjong.Plugin.Game;

/// <summary>
/// One step in a configuration-migration chain. Each implementation upgrades
/// a config from <see cref="FromVersion"/> to <see cref="ToVersion"/>.
///
/// Typical chain: a v0→v1 migrator initializes a new field added in v1, a
/// v1→v2 migrator renames a field, etc. The chain runs on load whenever the
/// persisted config's version is below the current schema version.
/// </summary>
public interface IConfigMigrator<TConfig> where TConfig : class
{
    int FromVersion { get; }
    int ToVersion { get; }

    /// <summary>
    /// Produce the upgraded config. Implementations must not mutate the input;
    /// return a new instance (records make this trivial via <c>with</c>).
    /// </summary>
    TConfig Migrate(TConfig input);
}
