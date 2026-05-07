using Mahjong.Plugin.Dalamud.Telemetry;

namespace Mahjong.Plugin.Dalamud.Tests;

public class EndpointHolderTests
{
    private static TelemetryEndpoint Endpoint(string url, bool enabled = true) =>
        new(UploadUrl: url, Enabled: enabled, MinPluginVersion: null);

    [Fact]
    public void Throws_when_initial_endpoint_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => new EndpointHolder(null!));
    }

    [Fact]
    public void Current_starts_at_initial_value()
    {
        var initial = Endpoint("https://a.example.com");
        var holder = new EndpointHolder(initial);
        Assert.Same(initial, holder.Current);
    }

    [Fact]
    public void Set_replaces_the_current_endpoint()
    {
        var initial = Endpoint("https://a.example.com");
        var next = Endpoint("https://b.example.com");
        var holder = new EndpointHolder(initial);

        holder.Set(next);

        Assert.Same(next, holder.Current);
    }

    [Fact]
    public void Set_with_null_keeps_the_existing_endpoint()
    {
        var initial = Endpoint("https://a.example.com");
        var holder = new EndpointHolder(initial);

        holder.Set(null!);

        // Null is treated as "ignore" — we don't want a transient resolver
        // failure to nuke the live endpoint.
        Assert.Same(initial, holder.Current);
    }

    [Fact]
    public void Set_can_disable_the_endpoint()
    {
        var initial = Endpoint("https://a.example.com", enabled: true);
        var disabled = Endpoint("https://a.example.com", enabled: false);
        var holder = new EndpointHolder(initial);

        holder.Set(disabled);

        Assert.False(holder.Current.Enabled);
    }
}
