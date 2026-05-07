using Dalamud.Plugin.Services;
using Serilog.Events;

namespace Mahjong.Plugin.Dalamud.Tests.Stubs;

/// <summary>
/// Silent <see cref="IPluginLog"/> stub. Records nothing — its only job is to
/// satisfy constructors that need a log without spamming the test runner.
/// Tests that want to assert on log output use <see cref="RecordingPluginLog"/>.
/// </summary>
public sealed class StubPluginLog : IPluginLog
{
    public LogEventLevel MinimumLogLevel { get; set; } = LogEventLevel.Verbose;
    public Serilog.ILogger Logger => Serilog.Core.Logger.None;

    public void Fatal(string messageTemplate, params object[] values) { }
    public void Fatal(Exception? exception, string messageTemplate, params object[] values) { }
    public void Error(string messageTemplate, params object[] values) { }
    public void Error(Exception? exception, string messageTemplate, params object[] values) { }
    public void Warning(string messageTemplate, params object[] values) { }
    public void Warning(Exception? exception, string messageTemplate, params object[] values) { }
    public void Information(string messageTemplate, params object[] values) { }
    public void Information(Exception? exception, string messageTemplate, params object[] values) { }
    public void Info(string messageTemplate, params object[] values) { }
    public void Info(Exception? exception, string messageTemplate, params object[] values) { }
    public void Debug(string messageTemplate, params object[] values) { }
    public void Debug(Exception? exception, string messageTemplate, params object[] values) { }
    public void Verbose(string messageTemplate, params object[] values) { }
    public void Verbose(Exception? exception, string messageTemplate, params object[] values) { }
    public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object[] values) { }
}

/// <summary>
/// Recording <see cref="IPluginLog"/>. Captures every entry into
/// <see cref="Entries"/> for tests that want to assert "this code logged
/// the right thing at the right severity".
/// </summary>
public sealed class RecordingPluginLog : IPluginLog
{
    public LogEventLevel MinimumLogLevel { get; set; } = LogEventLevel.Verbose;
    public Serilog.ILogger Logger => Serilog.Core.Logger.None;
    public List<(LogEventLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    private void Record(LogEventLevel level, Exception? ex, string template, object[] values)
    {
        Entries.Add((level, values is { Length: > 0 } ? string.Format(template.Replace("{0}", "{0}"), values) : template, ex));
    }

    public void Fatal(string messageTemplate, params object[] values) => Record(LogEventLevel.Fatal, null, messageTemplate, values);
    public void Fatal(Exception? exception, string messageTemplate, params object[] values) => Record(LogEventLevel.Fatal, exception, messageTemplate, values);
    public void Error(string messageTemplate, params object[] values) => Record(LogEventLevel.Error, null, messageTemplate, values);
    public void Error(Exception? exception, string messageTemplate, params object[] values) => Record(LogEventLevel.Error, exception, messageTemplate, values);
    public void Warning(string messageTemplate, params object[] values) => Record(LogEventLevel.Warning, null, messageTemplate, values);
    public void Warning(Exception? exception, string messageTemplate, params object[] values) => Record(LogEventLevel.Warning, exception, messageTemplate, values);
    public void Information(string messageTemplate, params object[] values) => Record(LogEventLevel.Information, null, messageTemplate, values);
    public void Information(Exception? exception, string messageTemplate, params object[] values) => Record(LogEventLevel.Information, exception, messageTemplate, values);
    public void Info(string messageTemplate, params object[] values) => Record(LogEventLevel.Information, null, messageTemplate, values);
    public void Info(Exception? exception, string messageTemplate, params object[] values) => Record(LogEventLevel.Information, exception, messageTemplate, values);
    public void Debug(string messageTemplate, params object[] values) => Record(LogEventLevel.Debug, null, messageTemplate, values);
    public void Debug(Exception? exception, string messageTemplate, params object[] values) => Record(LogEventLevel.Debug, exception, messageTemplate, values);
    public void Verbose(string messageTemplate, params object[] values) => Record(LogEventLevel.Verbose, null, messageTemplate, values);
    public void Verbose(Exception? exception, string messageTemplate, params object[] values) => Record(LogEventLevel.Verbose, exception, messageTemplate, values);
    public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object[] values) => Record(level, exception, messageTemplate, values);
}
