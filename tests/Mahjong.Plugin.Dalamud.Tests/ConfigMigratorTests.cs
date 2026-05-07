using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Tests;

public class ConfigMigratorTests
{
    [Fact]
    public void V0ToV1_just_bumps_version()
    {
        var input = new Configuration { Version = 0, TosAccepted = true };
        var migrator = new ConfigMigratorV0ToV1();
        var output = migrator.Migrate(input);

        Assert.Equal(1, output.Version);
        Assert.True(output.TosAccepted);
        Assert.Equal(input.PolicyTier, output.PolicyTier);
    }

    [Fact]
    public void V0ToV1_returns_a_new_instance()
    {
        var input = new Configuration { Version = 0 };
        var migrator = new ConfigMigratorV0ToV1();
        var output = migrator.Migrate(input);
        Assert.NotSame(input, output);
    }

    [Fact]
    public void V0ToV1_declares_correct_versions()
    {
        var migrator = new ConfigMigratorV0ToV1();
        Assert.Equal(0, migrator.FromVersion);
        Assert.Equal(1, migrator.ToVersion);
    }

    [Fact]
    public void V1ToV2_mints_a_fresh_install_id_when_missing()
    {
        var input = new Configuration { Version = 1, InstallId = Guid.Empty };
        var migrator = new ConfigMigratorV1ToV2();
        var output = migrator.Migrate(input);

        Assert.Equal(2, output.Version);
        Assert.NotEqual(Guid.Empty, output.InstallId);
    }

    [Fact]
    public void V1ToV2_preserves_an_existing_install_id()
    {
        var existing = Guid.NewGuid();
        var input = new Configuration { Version = 1, InstallId = existing };
        var migrator = new ConfigMigratorV1ToV2();
        var output = migrator.Migrate(input);

        Assert.Equal(existing, output.InstallId);
    }

    [Fact]
    public void V1ToV2_declares_correct_versions()
    {
        var migrator = new ConfigMigratorV1ToV2();
        Assert.Equal(1, migrator.FromVersion);
        Assert.Equal(2, migrator.ToVersion);
    }

    [Fact]
    public void Full_chain_v0_to_v2_mints_install_id_and_preserves_other_fields()
    {
        var input = new Configuration
        {
            Version = 0,
            TosAccepted = true,
            PolicyTier = "mcts",
            HumanizedDelayMs = 800,
        };

        var migrators = new IConfigMigrator<Configuration>[]
        {
            new ConfigMigratorV0ToV1(),
            new ConfigMigratorV1ToV2(),
        };

        var output = ConfigMigrationRunner.Run(
            input, currentVersion: 0, targetVersion: 2, migrators);

        Assert.Equal(2, output.Version);
        Assert.True(output.TosAccepted);
        Assert.Equal("mcts", output.PolicyTier);
        Assert.Equal(800, output.HumanizedDelayMs);
        Assert.NotEqual(Guid.Empty, output.InstallId);
    }

    [Fact]
    public void Chain_skips_already_completed_steps_when_starting_at_v1()
    {
        var input = new Configuration { Version = 1, InstallId = Guid.Empty };

        var migrators = new IConfigMigrator<Configuration>[]
        {
            new ConfigMigratorV0ToV1(),
            new ConfigMigratorV1ToV2(),
        };

        var output = ConfigMigrationRunner.Run(
            input, currentVersion: 1, targetVersion: 2, migrators);

        Assert.Equal(2, output.Version);
        Assert.NotEqual(Guid.Empty, output.InstallId);
    }
}
