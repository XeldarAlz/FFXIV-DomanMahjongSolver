using Dalamud.Plugin.Services;

namespace Mahjong.Plugin.Dalamud.Adapters;

/// <summary>
/// Bridges <see cref="IEventLog"/> onto Dalamud's <see cref="IPluginLog"/>.
/// Every plugin component that wants to log can take <see cref="IEventLog"/>
/// in its constructor and stay testable — production wires this adapter,
/// tests substitute an in-memory fake.
///
/// The category prefix lets log readers grep by component
/// (e.g. <c>[AutoPlay] ...</c>, <c>[AddonReader] ...</c>).
/// </summary>
internal sealed class DalamudEventLog : IEventLog
{
    private readonly IPluginLog log;

    public DalamudEventLog(IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;
    }

    public void Log(EventLevel level, string category, string message, Exception? exception = null)
    {
        var line = string.IsNullOrEmpty(category) ? message : $"[{category}] {message}";
        switch (level)
        {
            case EventLevel.Info:
                log.Information(line);
                break;
            case EventLevel.Warning:
                log.Warning(line);
                break;
            case EventLevel.Error when exception is null:
                log.Error(line);
                break;
            case EventLevel.Error:
                log.Error(exception, line);
                break;
        }
    }
}
