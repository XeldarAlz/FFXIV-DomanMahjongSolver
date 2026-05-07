namespace Mahjong.Plugin.Dalamud.Tests;

public class ConfigurationTests
{
    [Fact]
    public void Default_construction_uses_current_schema_version()
    {
        var c = new Configuration();
        Assert.Equal(Configuration.CurrentSchemaVersion, c.Version);
    }

    [Fact]
    public void Defaults_match_documented_values()
    {
        var c = new Configuration();
        Assert.False(c.AutomationArmed);
        Assert.True(c.SuggestionOnly);
        Assert.Equal("efficiency", c.PolicyTier);
        Assert.False(c.TosAccepted);
        Assert.False(c.DevMode);
        Assert.Equal(1200, c.HumanizedDelayMs);
        Assert.True(c.ShowInGameHighlight);
        Assert.False(c.ShowSuggestionDetails);
        Assert.True(c.EnableGameLogging);
    }

    [Fact]
    public void Init_properties_are_settable_at_construction()
    {
        var c = new Configuration
        {
            Version = 0,
            AutomationArmed = true,
            SuggestionOnly = false,
            PolicyTier = "mcts",
            TosAccepted = true,
        };
        Assert.Equal(0, c.Version);
        Assert.True(c.AutomationArmed);
        Assert.False(c.SuggestionOnly);
        Assert.Equal("mcts", c.PolicyTier);
        Assert.True(c.TosAccepted);
    }

    [Fact]
    public void With_expression_produces_a_new_instance_with_one_field_changed()
    {
        var a = new Configuration();
        var b = a with { TosAccepted = true };
        Assert.NotSame(a, b);
        Assert.False(a.TosAccepted);
        Assert.True(b.TosAccepted);
        Assert.Equal(a.PolicyTier, b.PolicyTier);
    }

    [Fact]
    public void With_expression_can_change_multiple_fields_atomically()
    {
        var a = new Configuration();
        var b = a with
        {
            AutomationArmed = true,
            SuggestionOnly = false,
            PolicyTier = "mcts",
        };
        Assert.True(b.AutomationArmed);
        Assert.False(b.SuggestionOnly);
        Assert.Equal("mcts", b.PolicyTier);
    }

    [Fact]
    public void Records_with_identical_values_are_value_equal()
    {
        var a = new Configuration { TosAccepted = true };
        var b = new Configuration { TosAccepted = true };
        Assert.Equal(a, b);
    }

    [Fact]
    public void Version_is_mutable_for_IPluginConfiguration_compliance()
    {
        // Dalamud's IPluginConfiguration requires Version to be a settable
        // property. The migration runner is the only legitimate writer; the
        // test pins that the field is in fact mutable so a future "make it
        // init-only" change has to consciously break Dalamud compatibility.
        var c = new Configuration();
        c.Version = 7;
        Assert.Equal(7, c.Version);
    }

    [Fact]
    public void Schema_version_constant_is_at_least_one()
    {
        Assert.True(Configuration.CurrentSchemaVersion >= 1);
    }
}
