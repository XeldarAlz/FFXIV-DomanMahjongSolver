namespace Mahjong.Plugin.Game;

/// <summary>
/// Null-object <see cref="IDiscardCapture"/>. Returned by the plugin when
/// no strategy could activate (rare — usually a patched binary that breaks
/// every observation path), and useful as a default in tests that don't
/// care about discard capture.
///
/// <para>Health is permanently <see cref="HookHealth.Offline"/>; the event
/// never fires and the diagnostic counters never advance.</para>
/// </summary>
public sealed class InertDiscardCapture : IDiscardCapture
{
    public const string Name = "inert";

    public HookHealth Health => HookHealth.Offline;
    public string StrategyName => Name;
    public ulong TotalCaptured => 0;
    public int LastTileId => -1;

    public event Action<DiscardEvent>? DiscardObserved
    {
        add { _ = value; }
        remove { _ = value; }
    }

    public void Dispose() { }
}
