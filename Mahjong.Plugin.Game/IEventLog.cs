namespace Mahjong.Plugin.Game;

public enum EventLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Plugin-wide logging facade. Wraps Dalamud's <c>IPluginLog</c> in production;
/// tests substitute an in-memory fake to assert on emitted events.
///
/// Categories are short stable strings ("AddonReader", "DiscardHook",
/// "AutoPlay") — the implementation can route them differently per category
/// (e.g. drop chatty AutoPlay logs in release builds).
/// </summary>
public interface IEventLog
{
    void Log(EventLevel level, string category, string message, Exception? exception = null);

    void Info(string category, string message) => Log(EventLevel.Info, category, message);
    void Warn(string category, string message) => Log(EventLevel.Warning, category, message);
    void Error(string category, string message, Exception? ex = null) => Log(EventLevel.Error, category, message, ex);
}
