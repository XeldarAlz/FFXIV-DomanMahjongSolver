using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Composition;

/// <summary>
/// Brings pre-immutable configs (Version=0, the original mutable
/// <c>Configuration</c> class) up to the v1 record schema. The shape stayed
/// compatible — Dalamud's deserializer populates the same field names — so
/// the migrator only needs to bump <see cref="Configuration.Version"/>.
///
/// Future schema changes (renames, type changes, defaulted new fields) ship
/// their own <see cref="IConfigMigrator{TConfig}"/> and bump
/// <see cref="Configuration.CurrentSchemaVersion"/>.
/// </summary>
internal sealed class ConfigMigratorV0ToV1 : IConfigMigrator<Configuration>
{
    public int FromVersion => 0;
    public int ToVersion => 1;

    public Configuration Migrate(Configuration input) =>
        input with { Version = ToVersion };
}
