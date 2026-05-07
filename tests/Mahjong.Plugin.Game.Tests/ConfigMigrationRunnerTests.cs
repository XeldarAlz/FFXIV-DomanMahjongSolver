using System;
using System.Collections.Generic;

namespace Mahjong.Plugin.Game.Tests;

public class ConfigMigrationRunnerTests
{
    private sealed record FakeConfig(int Version, string Payload);

    private sealed class StepMigrator : IConfigMigrator<FakeConfig>
    {
        public StepMigrator(int from, int to, Func<FakeConfig, FakeConfig>? mutate = null)
        {
            FromVersion = from;
            ToVersion = to;
            this.mutate = mutate ?? (c => c with { Version = to });
        }

        private readonly Func<FakeConfig, FakeConfig> mutate;

        public int FromVersion { get; }
        public int ToVersion { get; }
        public FakeConfig Migrate(FakeConfig input) => mutate(input);
    }

    private static readonly FakeConfig V0 = new(0, "v0");

    [Fact]
    public void Returns_input_when_versions_match()
    {
        var same = new FakeConfig(3, "x");
        var result = ConfigMigrationRunner.Run(
            same, currentVersion: 3, targetVersion: 3,
            new List<IConfigMigrator<FakeConfig>>());
        Assert.Same(same, result);
    }

    [Fact]
    public void Throws_when_persisted_version_is_newer_than_target()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigMigrationRunner.Run(
                V0, currentVersion: 5, targetVersion: 2,
                new List<IConfigMigrator<FakeConfig>>()));
        Assert.Contains("refusing to downgrade", ex.Message);
    }

    [Fact]
    public void Applies_chain_in_version_order_regardless_of_list_order()
    {
        var migrators = new IConfigMigrator<FakeConfig>[]
        {
            new StepMigrator(2, 3, c => c with { Version = 3, Payload = c.Payload + "->v3" }),
            new StepMigrator(0, 1, c => c with { Version = 1, Payload = c.Payload + "->v1" }),
            new StepMigrator(1, 2, c => c with { Version = 2, Payload = c.Payload + "->v2" }),
        };
        var result = ConfigMigrationRunner.Run(V0, 0, 3, migrators);

        Assert.Equal(3, result.Version);
        Assert.Equal("v0->v1->v2->v3", result.Payload);
    }

    [Fact]
    public void Throws_with_listing_when_chain_has_a_gap()
    {
        var migrators = new IConfigMigrator<FakeConfig>[]
        {
            new StepMigrator(0, 1),
            new StepMigrator(2, 3),
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigMigrationRunner.Run(V0, 0, 3, migrators));
        Assert.Contains("No migrator registered from version 1", ex.Message);
        Assert.Contains("0→1", ex.Message);
        Assert.Contains("2→3", ex.Message);
    }

    [Fact]
    public void Throws_when_a_migrator_does_not_advance_the_version()
    {
        var migrators = new IConfigMigrator<FakeConfig>[]
        {
            new StepMigrator(0, 0),
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigMigrationRunner.Run(V0, 0, 1, migrators));
        Assert.Contains("non-progressing", ex.Message);
    }

    [Fact]
    public void Throws_when_migrator_returns_null()
    {
        var migrators = new IConfigMigrator<FakeConfig>[]
        {
            new StepMigrator(0, 1, _ => null!),
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigMigrationRunner.Run(V0, 0, 1, migrators));
        Assert.Contains("returned null", ex.Message);
    }

    [Fact]
    public void Throws_on_null_config()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ConfigMigrationRunner.Run<FakeConfig>(null!, 0, 1, Array.Empty<IConfigMigrator<FakeConfig>>()));
    }

    [Fact]
    public void Throws_on_null_migrators()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ConfigMigrationRunner.Run(V0, 0, 1, null!));
    }
}
