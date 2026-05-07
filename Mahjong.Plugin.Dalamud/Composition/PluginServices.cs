using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.Adapters;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Game;
using Mahjong.Policy.Abstractions.Random;
using Mahjong.Policy.Abstractions.Weights;
using Mahjong.Policy.Efficiency;
using Mahjong.Policy.Mcts;
using Mahjong.Policy.Opponents;
using Mahjong.Policy.Placement;
using Mahjong.Rules;
using Mahjong.Rules.Rulesets;
using Mahjong.Rules.Scoring;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Plugin.Dalamud.Composition;

/// <summary>
/// Composition root for the plugin. Builds the Microsoft.Extensions.DependencyInjection
/// container, registering:
///   * Dalamud service adapters (<see cref="DalamudEventLog"/>, <see cref="DalamudFrameworkScheduler"/>)
///   * <see cref="IRuleSet"/> + scoring/dora/fu rules
///   * <see cref="IWeightProvider"/> + policy sub-policies
///   * Top-level <see cref="IPolicy"/> chosen by <c>Configuration.PolicyTier</c>
///
/// Plugin.cs builds the container once at startup and disposes it on plugin
/// unload — Dalamud's <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
/// gets cleaned up properly without leaks across plugin reloads.
///
/// Phase 7.A wires the container; Phase 7.B migrates the existing 40+ static
/// <c>Plugin.X</c> accesses to constructor injection across the plugin layer.
/// </summary>
public static class PluginServices
{
    /// <summary>Build the configured service provider for a plugin instance.</summary>
    public static ServiceProvider Build(
        DalamudServices dalamud,
        Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(dalamud);
        ArgumentNullException.ThrowIfNull(configuration);

        var services = new ServiceCollection();

        RegisterDalamudAdapters(services, dalamud);
        RegisterConfiguration(services, dalamud.PluginInterface, configuration);
        RegisterRules(services);
        RegisterWeights(services);
        RegisterRandomness(services);
        RegisterPolicies(services, configuration);

        return services.BuildServiceProvider(validateScopes: false);
    }

    private static void RegisterConfiguration(
        IServiceCollection services,
        IDalamudPluginInterface pluginInterface,
        Configuration configuration)
    {
        // The migration chain runs at plugin load — what we register here is
        // the post-migration live config, the service that gates further
        // edits, and the migrator catalog (kept registered so tests / future
        // wiring can resolve them, even though Plugin.cs already applied
        // them once at startup).
        services.AddSingleton<IConfigService<Configuration>>(
            new DalamudConfigService(pluginInterface.SavePluginConfig, configuration));
        services.AddSingleton<IConfigMigrator<Configuration>, ConfigMigratorV0ToV1>();
    }

    private static void RegisterDalamudAdapters(
        IServiceCollection services, DalamudServices dalamud)
    {
        // Every Dalamud service the plugin uses is registered as a singleton
        // — Dalamud hands us one instance per plugin lifetime and that's
        // exactly what the container should hand back to collaborators.
        // The bundle itself is also registered, so a class that needs more
        // than two Dalamud services can take the whole record.
        services.AddSingleton(dalamud);
        services.AddSingleton(dalamud.Log);
        services.AddSingleton(dalamud.Framework);
        services.AddSingleton(dalamud.PluginInterface);
        services.AddSingleton(dalamud.CommandManager);
        services.AddSingleton(dalamud.ChatGui);
        services.AddSingleton(dalamud.ClientState);
        services.AddSingleton(dalamud.DataManager);
        services.AddSingleton(dalamud.Condition);
        services.AddSingleton(dalamud.GameGui);
        services.AddSingleton(dalamud.AddonLifecycle);
        services.AddSingleton(dalamud.SigScanner);
        services.AddSingleton(dalamud.GameInterop);

        services.AddSingleton<IEventLog, DalamudEventLog>();
        services.AddSingleton<IFrameworkScheduler, DalamudFrameworkScheduler>();
        services.AddSingleton<IGameClientAdapter, DalamudGameClientAdapter>();

        // The addon resolver caches `lastResolved` across calls; making it a
        // singleton means every collaborator amortizes the same cache instead
        // of each one paying the probe cost on its first call.
        services.AddSingleton<MahjongAddon>();
    }

    private static void RegisterRules(IServiceCollection services)
    {
        // The live plugin runs against FFXIV's Doman client — Doman rules.
        // (Tenhou replay code paths inject RiichiRuleSet directly when needed;
        // they don't pull from this container.)
        services.AddSingleton<IRuleSet, DomanRuleSet>();
        services.AddSingleton<IScoringRule, StandardScoringRule>();
        services.AddSingleton<IDoraRule, StandardDoraRule>();
        services.AddSingleton<IFuRule, StandardFuRule>();
    }

    private static void RegisterWeights(IServiceCollection services)
    {
        // Phase 7 ships with the hardcoded defaults. JsonWeightProvider can
        // swap in once a weights.json shipping convention is finalized.
        services.AddSingleton<IWeightProvider, DefaultWeightProvider>();
    }

    private static void RegisterRandomness(IServiceCollection services)
    {
        // Time-based seed for the live plugin. Tests / tuner replace this
        // with a fixed seed via direct construction.
        services.AddSingleton<IRandomSource>(_ => new SeededRandomSource());
    }

    private static void RegisterPolicies(IServiceCollection services, Configuration configuration)
    {
        services.AddSingleton<IOpponentModel>(sp =>
            new OpponentModel(sp.GetRequiredService<IWeightProvider>().Current.Opponent));

        services.AddSingleton<IPlacementPolicy>(sp =>
            new PlacementAdjuster(sp.GetRequiredService<IWeightProvider>().Current.Placement));

        services.AddSingleton<IDiscardPolicy, HeuristicDiscardPolicy>();
        services.AddSingleton<ICallPolicy, HeuristicCallPolicy>();
        services.AddSingleton<IRiichiPolicy, HeuristicRiichiPolicy>();
        services.AddSingleton<IPushFoldPolicy, HeuristicPushFoldPolicy>();

        services.AddSingleton<IRolloutPolicy>(sp =>
            new Rollout(sp.GetRequiredService<IRandomSource>(),
                        sp.GetRequiredService<IWeightProvider>().Current.Rollout));

        services.AddSingleton<EfficiencyPolicy>();
        services.AddSingleton<IsmctsPolicy>(sp =>
            new IsmctsPolicy(rng: sp.GetRequiredService<IRandomSource>()));

        // Top-level policy chosen by user configuration. Singleton so every
        // tick sees the same instance — switching tiers happens through
        // Plugin.SetPolicy which rebuilds the top-level binding.
        services.AddSingleton<IPolicy>(sp =>
            configuration.PolicyTier == "mcts"
                ? sp.GetRequiredService<IsmctsPolicy>()
                : sp.GetRequiredService<EfficiencyPolicy>());
    }
}
