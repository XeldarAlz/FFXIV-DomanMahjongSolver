using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Mahjong.Plugin.Dalamud.Composition;

/// <summary>
/// Bundle of every Dalamud-injected service the plugin uses, captured into a
/// single immutable record. Plugin.cs forwards the <c>[PluginService]</c>
/// statics Dalamud populates at load into one instance of this record, and
/// every other class that needs Dalamud services takes it as a constructor
/// parameter rather than reaching for the static <c>Plugin.X</c> properties.
///
/// <para>Why a record instead of N individual constructor params:
/// the plugin has a dozen Dalamud services and most collaborators need a
/// shifting subset over time. A single immutable bundle keeps constructors
/// stable when an unrelated service is added, makes the dependency
/// signature obvious, and trivially passes through MEDI as a singleton.</para>
///
/// <para>Why public: collaborators outside the <c>Composition</c> namespace
/// take this in their constructors. Tests can build a stub with whichever
/// fields they need (others left null) — every consumer should only be
/// reading the handful of fields it actually depends on.</para>
/// </summary>
public sealed record DalamudServices(
    IPluginLog Log,
    IFramework Framework,
    IDalamudPluginInterface PluginInterface,
    ICommandManager CommandManager,
    IChatGui ChatGui,
    IClientState ClientState,
    IDataManager DataManager,
    ICondition Condition,
    IGameGui GameGui,
    IAddonLifecycle AddonLifecycle,
    ISigScanner SigScanner,
    IGameInteropProvider GameInterop);
