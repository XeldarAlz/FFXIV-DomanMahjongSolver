using Mahjong.Plugin.Dalamud.Telemetry;

namespace Mahjong.Plugin.Dalamud.Tests;

public class TelemetryEndpointTests
{
    [Fact]
    public void Endpoint_record_carries_three_fields()
    {
        var ep = new TelemetryEndpoint(
            UploadUrl: "https://example.com/upload",
            Enabled: true,
            MinPluginVersion: "0.5.0");

        Assert.Equal("https://example.com/upload", ep.UploadUrl);
        Assert.True(ep.Enabled);
        Assert.Equal("0.5.0", ep.MinPluginVersion);
    }

    [Fact]
    public void Min_plugin_version_can_be_null()
    {
        var ep = new TelemetryEndpoint("https://example.com", true, null);
        Assert.Null(ep.MinPluginVersion);
    }

    [Fact]
    public void Records_with_identical_values_are_value_equal()
    {
        var a = new TelemetryEndpoint("https://example.com", true, "1.0");
        var b = new TelemetryEndpoint("https://example.com", true, "1.0");
        Assert.Equal(a, b);
    }

    [Fact]
    public void EndpointResolver_exposes_fallback_url_constant()
    {
        Assert.False(string.IsNullOrEmpty(EndpointResolver.EmbeddedFallbackUrl));
        Assert.StartsWith("https://", EndpointResolver.EmbeddedFallbackUrl);
    }

    [Fact]
    public void EndpointResolver_exposes_config_url_constant()
    {
        Assert.False(string.IsNullOrEmpty(EndpointResolver.ConfigUrl));
        Assert.StartsWith("https://raw.githubusercontent.com/", EndpointResolver.ConfigUrl);
    }
}
