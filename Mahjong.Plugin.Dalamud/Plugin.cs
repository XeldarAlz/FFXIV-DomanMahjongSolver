using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.Adapters;
using Mahjong.Plugin.Dalamud.Commands;
using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Hooks;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Telemetry;
using Mahjong.Plugin.Dalamud.UI;
using Mahjong.Plugin.Game;
using Mahjong.Policy;
using Mahjong.Policy.Efficiency;
using Mahjong.Policy.Mcts;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Plugin.Dalamud;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("Mahjong.Plugin.Dalamud");

    /// <summary>
    /// MEDI service container — owns every injectable service for the plugin's
    /// lifetime. Built in the constructor after Dalamud's <see cref="PluginService"/>
    /// statics are populated; disposed in <see cref="Dispose"/> so the
    /// AssemblyLoadContext gets cleaned up cleanly across plugin reloads.
    ///
    /// Phase 7.A: container is built and the policy components are resolved
    /// from it. Phase 7.B will migrate the remaining ~40 static <c>Plugin.X</c>
    /// accesses across the plugin to constructor injection through this provider.
    /// </summary>
    public ServiceProvider Services { get; }

    /// <summary>
    /// Read-write façade over the persisted configuration. Use
    /// <see cref="IConfigService{TConfig}.Update"/> to mutate; never reach
    /// into the underlying record directly.
    /// </summary>
    public IConfigService<Configuration> ConfigService { get; }

    /// <summary>
    /// Convenience accessor that returns the *current* configuration record.
    /// The reference is replaced on every edit, so don't cache it across
    /// frames — read it fresh from <see cref="ConfigService"/> when you need
    /// the latest values.
    /// </summary>
    public Configuration Configuration => ConfigService.Current;

    public MainWindow MainWindow { get; }
    public AboutWindow AboutWindow { get; }
    public DebugOverlay DebugOverlay { get; }
    public HandOverlay HandOverlay { get; }
    public AddonEmjReader AddonReader { get; }
    public MeldTracker MeldTracker { get; } = new();
    public StateAggregator Aggregator { get; }
    public IPolicy Policy { get; private set; }
    public IPolicy EfficiencyPolicyInstance { get; }
    public IPolicy IsmctsPolicyInstance { get; }
    public InputEventLogger EventLogger { get; }
    public InputDispatcher Dispatcher { get; }
    public GameLogger GameLogger { get; }
    public AutoPlayLoop AutoPlay { get; }

    /// <summary>
    /// Captures every Doman discard the moment it commits. Strategy is chosen
    /// at startup by <see cref="DiscardCaptureFactory"/> — native asm hook
    /// when available, addon-poll fallback otherwise. <see cref="IDiscardCapture.Health"/>
    /// reports which path is live.
    /// </summary>
    public IDiscardCapture DiscardCapture { get; }

    /// <summary>
    /// Diagnostic file logger that mirrors every captured discard to
    /// <c>emj-discards.log</c>. Useful for verifying the fallback strategy
    /// during reverse-engineering sessions.
    /// </summary>
    public DiscardCaptureLogger DiscardCaptureLogger { get; }

    /// <summary>
    /// Anonymous telemetry pipeline. Captures errors, structured findings,
    /// and labeled memory dumps from the live addon, and ships them through
    /// the <see cref="TelemetryUploader"/> background worker so cross-client
    /// reverse-engineering can be done offline against a real corpus.
    /// </summary>
    public ErrorSink ErrorSink { get; }
    public IFindingsLog FindingsLog { get; }
    public ISigprobeLog SigprobeLog { get; }
    public SeatPoolRegistry SeatPoolRegistry { get; } = new();
    public MemoryDumpRecorder MemoryDumpRecorder { get; }
    public TelemetryUploader TelemetryUploader { get; }
    public DiscardTracker DiscardTracker { get; }
    public InputRecorder InputRecorder { get; }

    private readonly System.Net.Http.HttpClient telemetryHttp;
    private MirroredPluginLog mirroredLog = null!;

    private readonly MjAutoCommand command;

    public Plugin()
    {
        var loaded = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration { Version = 0 };
        var migrators = new IConfigMigrator<Configuration>[]
        {
            new ConfigMigratorV0ToV1(),
            new ConfigMigratorV1ToV2(),
        };
        var migrated = ConfigMigrationRunner.Run(
            loaded,
            currentVersion: loaded.Version,
            targetVersion: Mahjong.Plugin.Dalamud.Configuration.CurrentSchemaVersion,
            migrators);

        // If migration produced a new instance, persist it now so the next
        // launch starts at the current schema version without re-running.
        if (!ReferenceEquals(loaded, migrated))
            PluginInterface.SavePluginConfig(migrated);

        // Bundle every Dalamud-injected service into a single record and
        // hand it to the composition root. After this point the rest of
        // the plugin reaches for services through constructor injection,
        // not through the static `Plugin.X` properties.
        //
        // The IPluginLog handed to the container is a `MirroredPluginLog`
        // that forwards every call through to Dalamud's logger and
        // additionally writes Warning/Error/Fatal events to the plugin's
        // ErrorSink (attached below once the sink exists). Without this,
        // Plugin.Log.Warning(...) calls only land in Dalamud's local log
        // file and never reach the errors telemetry stream.
        mirroredLog = new MirroredPluginLog(Log);
        var dalamud = new DalamudServices(
            Log: mirroredLog,
            Framework: Framework,
            PluginInterface: PluginInterface,
            CommandManager: CommandManager,
            ChatGui: ChatGui,
            ClientState: ClientState,
            DataManager: DataManager,
            Condition: Condition,
            GameGui: GameGui,
            AddonLifecycle: AddonLifecycle,
            SigScanner: SigScanner,
            GameInterop: GameInterop);

        Services = PluginServices.Build(dalamud, migrated);
        ConfigService = Services.GetRequiredService<IConfigService<Configuration>>();

        // Policies resolve through the container. The two concrete instances
        // are still surfaced as properties for back-compat with code that
        // hasn't migrated to constructor injection yet (Phase 7.B work).
        EfficiencyPolicyInstance = Services.GetRequiredService<EfficiencyPolicy>();
        IsmctsPolicyInstance = Services.GetRequiredService<IsmctsPolicy>();
        Policy = Services.GetRequiredService<IPolicy>();

        // Telemetry sinks first — AddonEmjReader / VariantSelector emit
        // findings as soon as they probe the live addon, so the sinks must
        // exist before any reader construction.
        var configDir = PluginInterface.GetPluginConfigDirectory();
        ErrorSink = new ErrorSink(configDir);
        FindingsLog = new FindingsLog(configDir, ErrorSink);
        SigprobeLog = new SigprobeLog(configDir);
        // Now that ErrorSink exists, hook it into the log mirror so every
        // subsequent Plugin.Log.Warning/Error/Fatal also lands in the
        // errors stream. Calls before this point (Dalamud's own load-time
        // chatter) just pass through — short window, nothing critical.
        mirroredLog.AttachSink(ErrorSink);

        // Resolve the addon-name helper from the container so its lastResolved
        // cache is shared across every collaborator that probes the addon.
        var mahjongAddon = Services.GetRequiredService<MahjongAddon>();
        Dispatcher = new InputDispatcher(mahjongAddon);

        // `IDalamudPluginInterface.AssemblyLocation` is the only reliable way
        // to locate sibling files — `Assembly.Location` and
        // `AppContext.BaseDirectory` both come back empty inside Dalamud's
        // plugin AssemblyLoadContext (verified empirically in the findings
        // stream). Layouts live in `<plugin-dir>/layouts/*.json`, copied
        // there by the .csproj as build content.
        var pluginAssemblyDir = PluginInterface.AssemblyLocation.DirectoryName ?? configDir;
        var layoutsDir = Path.Combine(pluginAssemblyDir, "layouts");

        AddonReader = new AddonEmjReader(
            AddonLifecycle, Log, mahjongAddon, MeldTracker, configDir, layoutsDir, FindingsLog);
        Aggregator = new StateAggregator(AddonReader, Framework);
        EventLogger = new InputEventLogger(
            AddonReader, MeldTracker, AddonLifecycle, GameInterop, Log, mahjongAddon, configDir);
        AddonReader.EventLogger = EventLogger;
        InputRecorder = new InputRecorder(EventLogger, configDir);
        // `() => Policy` so the logger always sees the user's currently-active
        // tier (efficiency vs. mcts), even after a SetPolicy switch — Plugin.Policy
        // is mutable and replaced wholesale on tier change.
        GameLogger = new GameLogger(Aggregator, ConfigService, Log, configDir, () => Policy, EventLogger);
        AutoPlay = new AutoPlayLoop(this, Framework, Log, mahjongAddon);

        // Discard capture: native asm hook on the discard-write site (verified
        // 2026-04-27 via Cheat Engine), with an addon-poll fallback that
        // diffs StateAggregator snapshots. The factory tries native first
        // and falls back automatically — Health/StrategyName surface which
        // path is live. SigprobeLog is threaded in so every ScanText attempt
        // gets recorded for the sigprobes telemetry stream.
        DiscardCapture = DiscardCaptureFactory.Create(
            Log, Framework, SigScanner, Aggregator, SeatPoolRegistry, SigprobeLog);
        DiscardCaptureLogger = new DiscardCaptureLogger(
            DiscardCapture, PluginInterface.GetPluginConfigDirectory());
        DiscardTracker = new DiscardTracker(DiscardCapture, configDir);

        // ---- Remainder of telemetry pipeline ----
        // Sinks above wire into the readers; this section builds the
        // network-side uploader and the memory-dump recorder. Every stage
        // is fail-safe (errors funnel into ErrorSink which itself never
        // throws), so a broken endpoint never bubbles up to game code.
        var envelope = TelemetryEnvelope.Build(migrated.InstallId, ClientState.ClientLanguage);
        telemetryHttp = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        var http = new HttpTelemetryClient(telemetryHttp, envelope, Log);
        var endpointHolder = new EndpointHolder(
            new TelemetryEndpoint(EndpointResolver.EmbeddedFallbackUrl, true, null));
        TelemetryUploader = new TelemetryUploader(http, endpointHolder, ConfigService, Log, configDir);

        // Resolve the live endpoint asynchronously — startup must not block
        // on a slow GitHub fetch. Until it returns, uploads target the
        // embedded fallback URL above.
        _ = ResolveEndpointAsync(telemetryHttp, endpointHolder);

        MemoryDumpRecorder = new MemoryDumpRecorder(
            AddonReader, SeatPoolRegistry, ErrorSink, configDir);

        // Snapshot every state change. Hash-dedup inside the recorder
        // collapses identical layouts so this is cheap on quiet ticks; the
        // atk_count gate inside Record drops idle-cadence captures.
        Aggregator.Changed += _ => MemoryDumpRecorder.Record("state-change");

        // Bracket every Mahjong-addon FireCallback with a (pre, post) memdump
        // pair so the offline RE pipeline (tools/find-discard-offset.mjs et al)
        // can diff addon bytes that mutate in lockstep with a single click.
        // Both reasons bypass the atk_count gate inside Record because the
        // gate applies only to "state-change". The pre event reads addon
        // state synchronously before the original FireCallback runs, so the
        // captured bytes still reflect pre-mutation memory.
        EventLogger.BeforeFireCallback += _ => MemoryDumpRecorder.Record("input-pre");
        EventLogger.CallbackObserved += _ => MemoryDumpRecorder.Record("input-post");

        MainWindow = new MainWindow(this);
        AboutWindow = new AboutWindow(Log);
        DebugOverlay = new DebugOverlay(this, Framework, CommandManager, mahjongAddon);
        HandOverlay = new HandOverlay(this, PluginInterface, mahjongAddon);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(AboutWindow);
        WindowSystem.AddWindow(DebugOverlay);

        command = new MjAutoCommand(
            this, ChatGui, CommandManager, Framework, PluginInterface, SigScanner, mahjongAddon);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;

        Log.Information("Doman Mahjong Solver loaded.");
    }

    public void Dispose()
    {
        command.Dispose();
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        AboutWindow.Dispose();
        DebugOverlay.Dispose();
        HandOverlay.Dispose();
        AutoPlay.Dispose();
        DiscardCaptureLogger.Dispose();
        DiscardTracker.Dispose();
        DiscardCapture.Dispose();
        GameLogger.Dispose();
        InputRecorder.Dispose();
        EventLogger.Dispose();
        Aggregator.Dispose();
        AddonReader.Dispose();

        // Telemetry: flush the uploader first so any in-flight POSTs
        // complete (under a 10s hard cap), then tear down the sinks. A
        // stuck network won't block plugin unload.
        TelemetryUploader.Dispose();
        MemoryDumpRecorder.Dispose();
        (FindingsLog as IDisposable)?.Dispose();
        ErrorSink.Dispose();
        telemetryHttp.Dispose();

        // Dispose last — singletons in the container may hold references that
        // the components above still touch during their own Dispose.
        Services.Dispose();
    }

    /// <summary>
    /// Fetches the live telemetry endpoint config from GitHub and updates
    /// the holder. Errors are intentionally swallowed — startup must never
    /// block on this, and the embedded fallback URL keeps uploads working
    /// in the offline-at-load case.
    /// </summary>
    private async System.Threading.Tasks.Task ResolveEndpointAsync(
        System.Net.Http.HttpClient http, EndpointHolder holder)
    {
        try
        {
            var resolved = await EndpointResolver.ResolveAsync(http).ConfigureAwait(false);
            holder.Set(resolved);
            Log.Info($"[Telemetry] endpoint resolved: enabled={resolved.Enabled}");
        }
        catch (Exception ex)
        {
            ErrorSink.RecordException("Plugin.ResolveEndpointAsync", ex);
        }
    }

    public void ToggleMainWindow() => MainWindow.Toggle();

    public void ToggleAboutWindow() => AboutWindow.Toggle();

    public void ToggleDebugOverlay() => DebugOverlay.Toggle();

    public void SetPolicy(string tier)
    {
        var t = tier.ToLowerInvariant();
        var resolved = t == "mcts" ? "mcts" : "efficiency";
        Policy = resolved == "mcts" ? IsmctsPolicyInstance : EfficiencyPolicyInstance;
        ConfigService.Update(c => c with { PolicyTier = resolved });
    }
}
