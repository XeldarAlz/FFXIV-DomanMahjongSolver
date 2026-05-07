using Dalamud.Game;
using Mahjong.Plugin.Dalamud.Telemetry;

namespace Mahjong.Plugin.Dalamud.Tests;

public class TelemetryEnvelopeTests
{
    [Fact]
    public void Build_carries_supplied_install_id()
    {
        var id = Guid.NewGuid();
        var env = TelemetryEnvelope.Build(id, ClientLanguage.English);
        Assert.Equal(id, env.InstallId);
    }

    [Fact]
    public void Build_renders_client_language_as_region_string()
    {
        var env = TelemetryEnvelope.Build(Guid.NewGuid(), ClientLanguage.Japanese);
        Assert.Equal("Japanese", env.ClientRegion);
    }

    [Fact]
    public void Build_carries_current_schema_version()
    {
        var env = TelemetryEnvelope.Build(Guid.NewGuid(), ClientLanguage.English);
        Assert.Equal(TelemetryEnvelope.CurrentSchemaVersion, env.SchemaVersion);
    }

    [Fact]
    public void Build_resolves_a_plugin_version()
    {
        // Plugin version comes from the assembly metadata; fallback is
        // "0.0.0". Either way, the field is non-empty and the SafeGet
        // wrapper guarantees the build never throws.
        var env = TelemetryEnvelope.Build(Guid.NewGuid(), ClientLanguage.English);
        Assert.False(string.IsNullOrEmpty(env.PluginVersion));
    }

    [Fact]
    public void Build_resolves_a_plugin_hash_or_unknown()
    {
        var env = TelemetryEnvelope.Build(Guid.NewGuid(), ClientLanguage.English);
        Assert.False(string.IsNullOrEmpty(env.PluginHash));
        // Hash is either "unknown" or a 16-char hex prefix.
        Assert.True(env.PluginHash == "unknown" || env.PluginHash.Length == 16);
    }

    [Fact]
    public void Build_records_os_platform()
    {
        var env = TelemetryEnvelope.Build(Guid.NewGuid(), ClientLanguage.English);
        Assert.False(string.IsNullOrEmpty(env.OsPlatform));
    }

    [Fact]
    public void Schema_version_constant_is_at_least_one()
    {
        Assert.True(TelemetryEnvelope.CurrentSchemaVersion >= 1);
    }

    [Fact]
    public void Records_with_same_payload_are_value_equal()
    {
        var id = Guid.NewGuid();
        var a = new TelemetryEnvelope(id, "1.0", "abc", "g", "EN", "Win32NT", 1);
        var b = new TelemetryEnvelope(id, "1.0", "abc", "g", "EN", "Win32NT", 1);
        Assert.Equal(a, b);
    }
}
