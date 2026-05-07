using System;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Composition;

/// <summary>
/// V1 → V2: mints a per-install <see cref="Configuration.InstallId"/>. The id
/// is the only stable handle the telemetry uploader uses to identify a client,
/// so it must exist before any upload is attempted. Generated once here and
/// never rotated — losing the id is equivalent to losing all prior uploads
/// from this install (server-side dedup will treat the install as new).
/// </summary>
internal sealed class ConfigMigratorV1ToV2 : IConfigMigrator<Configuration>
{
    public int FromVersion => 1;
    public int ToVersion => 2;

    public Configuration Migrate(Configuration input) =>
        input with
        {
            Version = ToVersion,
            InstallId = input.InstallId == Guid.Empty ? Guid.NewGuid() : input.InstallId,
        };
}
