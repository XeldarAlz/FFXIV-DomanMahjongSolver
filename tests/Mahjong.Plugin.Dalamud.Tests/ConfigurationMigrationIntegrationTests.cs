using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// End-to-end migration integration tests. Hand a v0 config (the shape that
/// existed before Phase 7.B-1) through the actual migrator chain Plugin.cs
/// constructs at boot, and verify the output is a healthy v2 record.
/// </summary>
public class ConfigurationMigrationIntegrationTests
{
    private static IConfigMigrator<Configuration>[] FullChain() =>
    [
        new ConfigMigratorV0ToV1(),
        new ConfigMigratorV1ToV2(),
    ];

    [Fact]
    public void V0_to_v2_yields_a_complete_record()
    {
        var v0 = new Configuration { Version = 0 };
        var v2 = ConfigMigrationRunner.Run(v0, 0, 2, FullChain());

        Assert.Equal(2, v2.Version);
        Assert.NotEqual(Guid.Empty, v2.InstallId);
    }

    [Fact]
    public void V0_with_existing_install_id_keeps_it_through_to_v2()
    {
        var existing = Guid.NewGuid();
        var v0 = new Configuration { Version = 0, InstallId = existing };
        var v2 = ConfigMigrationRunner.Run(v0, 0, 2, FullChain());

        Assert.Equal(existing, v2.InstallId);
    }

    [Fact]
    public void V0_to_v2_preserves_user_facing_fields()
    {
        var v0 = new Configuration
        {
            Version = 0,
            TosAccepted = true,
            AutomationArmed = true,
            SuggestionOnly = false,
            PolicyTier = "mcts",
            DevMode = true,
            HumanizedDelayMs = 2000,
            ShowInGameHighlight = false,
            ShowSuggestionDetails = true,
            EnableGameLogging = false,
        };
        var v2 = ConfigMigrationRunner.Run(v0, 0, 2, FullChain());

        Assert.True(v2.TosAccepted);
        Assert.True(v2.AutomationArmed);
        Assert.False(v2.SuggestionOnly);
        Assert.Equal("mcts", v2.PolicyTier);
        Assert.True(v2.DevMode);
        Assert.Equal(2000, v2.HumanizedDelayMs);
        Assert.False(v2.ShowInGameHighlight);
        Assert.True(v2.ShowSuggestionDetails);
        Assert.False(v2.EnableGameLogging);
    }

    [Fact]
    public void V1_to_v2_skips_the_v0_step()
    {
        var v1 = new Configuration { Version = 1 };
        var v2 = ConfigMigrationRunner.Run(v1, 1, 2, FullChain());
        Assert.Equal(2, v2.Version);
    }

    [Fact]
    public void Already_at_v2_returns_input_unchanged()
    {
        var v2 = new Configuration { Version = 2, InstallId = Guid.NewGuid() };
        var output = ConfigMigrationRunner.Run(v2, 2, 2, FullChain());
        Assert.Same(v2, output);
    }

    [Fact]
    public void Two_distinct_v0_inputs_get_distinct_install_ids()
    {
        var a = ConfigMigrationRunner.Run(new Configuration { Version = 0 }, 0, 2, FullChain());
        var b = ConfigMigrationRunner.Run(new Configuration { Version = 0 }, 0, 2, FullChain());
        Assert.NotEqual(a.InstallId, b.InstallId);
    }

    [Fact]
    public void Migration_does_not_mutate_input()
    {
        var input = new Configuration { Version = 0 };
        ConfigMigrationRunner.Run(input, 0, 2, FullChain());
        // Input record is untouched.
        Assert.Equal(0, input.Version);
        Assert.Equal(Guid.Empty, input.InstallId);
    }
}
