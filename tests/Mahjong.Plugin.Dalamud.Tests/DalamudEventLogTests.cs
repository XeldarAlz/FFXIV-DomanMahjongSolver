using Mahjong.Plugin.Dalamud.Adapters;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game;
using Serilog.Events;

namespace Mahjong.Plugin.Dalamud.Tests;

public class DalamudEventLogTests
{
    [Fact]
    public void Throws_when_log_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => new DalamudEventLog(null!));
    }

    [Fact]
    public void Info_routes_to_Information_severity()
    {
        var log = new RecordingPluginLog();
        IEventLog adapter = new DalamudEventLog(log);

        adapter.Log(EventLevel.Info, "Auto", "starting");

        Assert.Single(log.Entries);
        Assert.Equal(LogEventLevel.Information, log.Entries[0].Level);
        Assert.Contains("[Auto] starting", log.Entries[0].Message);
    }

    [Fact]
    public void Warning_routes_to_Warning_severity()
    {
        var log = new RecordingPluginLog();
        IEventLog adapter = new DalamudEventLog(log);

        adapter.Log(EventLevel.Warning, "Probe", "no match");

        Assert.Single(log.Entries);
        Assert.Equal(LogEventLevel.Warning, log.Entries[0].Level);
    }

    [Fact]
    public void Error_without_exception_routes_to_Error_string_overload()
    {
        var log = new RecordingPluginLog();
        IEventLog adapter = new DalamudEventLog(log);

        adapter.Log(EventLevel.Error, "Hook", "boom");

        Assert.Single(log.Entries);
        Assert.Equal(LogEventLevel.Error, log.Entries[0].Level);
        Assert.Null(log.Entries[0].Exception);
    }

    [Fact]
    public void Error_with_exception_routes_to_Error_exception_overload()
    {
        var log = new RecordingPluginLog();
        IEventLog adapter = new DalamudEventLog(log);
        var ex = new InvalidOperationException("nope");

        adapter.Log(EventLevel.Error, "Hook", "boom", ex);

        Assert.Single(log.Entries);
        Assert.Equal(LogEventLevel.Error, log.Entries[0].Level);
        Assert.Same(ex, log.Entries[0].Exception);
    }

    [Fact]
    public void Empty_category_omits_the_bracket_prefix()
    {
        var log = new RecordingPluginLog();
        IEventLog adapter = new DalamudEventLog(log);

        adapter.Log(EventLevel.Info, "", "bare message");

        Assert.Equal("bare message", log.Entries[0].Message);
    }

    [Fact]
    public void Category_is_wrapped_in_brackets()
    {
        var log = new RecordingPluginLog();
        IEventLog adapter = new DalamudEventLog(log);

        adapter.Log(EventLevel.Info, "Cat", "m");

        Assert.Equal("[Cat] m", log.Entries[0].Message);
    }
}
