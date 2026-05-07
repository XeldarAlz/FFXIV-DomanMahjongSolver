namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Per-property "with-expression updates exactly that field, leaves others
/// alone" tests. Catches mistakes where a future Configuration field forgets
/// to participate in record equality.
/// </summary>
public class ConfigurationPropertyTests
{
    private static Configuration NonDefault() => new()
    {
        AutomationArmed = true,
        SuggestionOnly = false,
        PolicyTier = "mcts",
        TosAccepted = true,
        DevMode = true,
        HumanizedDelayMs = 700,
        ShowInGameHighlight = false,
        ShowSuggestionDetails = true,
        EnableGameLogging = false,
        InstallId = Guid.NewGuid(),
    };

    [Fact]
    public void With_AutomationArmed_changes_only_that_field()
    {
        var a = NonDefault();
        var b = a with { AutomationArmed = !a.AutomationArmed };
        Assert.NotEqual(a.AutomationArmed, b.AutomationArmed);
        Assert.Equal(a.PolicyTier, b.PolicyTier);
        Assert.Equal(a.TosAccepted, b.TosAccepted);
        Assert.Equal(a.HumanizedDelayMs, b.HumanizedDelayMs);
        Assert.Equal(a.InstallId, b.InstallId);
    }

    [Fact]
    public void With_PolicyTier_changes_only_that_field()
    {
        var a = NonDefault();
        var b = a with { PolicyTier = "efficiency" };
        Assert.Equal("efficiency", b.PolicyTier);
        Assert.Equal(a.AutomationArmed, b.AutomationArmed);
    }

    [Fact]
    public void With_HumanizedDelayMs_changes_only_that_field()
    {
        var a = NonDefault();
        var b = a with { HumanizedDelayMs = 1500 };
        Assert.Equal(1500, b.HumanizedDelayMs);
        Assert.Equal(a.PolicyTier, b.PolicyTier);
    }

    [Fact]
    public void With_InstallId_changes_only_that_field()
    {
        var a = NonDefault();
        var fresh = Guid.NewGuid();
        var b = a with { InstallId = fresh };
        Assert.Equal(fresh, b.InstallId);
        Assert.Equal(a.PolicyTier, b.PolicyTier);
    }

    [Fact]
    public void Records_with_distinct_install_ids_are_not_equal()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var a = new Configuration { InstallId = id1 };
        var b = new Configuration { InstallId = id2 };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Records_with_distinct_policy_tiers_are_not_equal()
    {
        var a = new Configuration { PolicyTier = "efficiency" };
        var b = new Configuration { PolicyTier = "mcts" };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Default_install_id_is_empty_guid()
    {
        var c = new Configuration();
        Assert.Equal(Guid.Empty, c.InstallId);
    }

    [Fact]
    public void All_boolean_defaults_match_documented_intent()
    {
        // Pin defaults that user-facing copy depends on. AutomationArmed
        // must default to false (off-by-default for ToS reasons),
        // SuggestionOnly defaults to true (hints, not auto-play),
        // EnableGameLogging defaults on (training corpus collection).
        var c = new Configuration();
        Assert.False(c.AutomationArmed);
        Assert.True(c.SuggestionOnly);
        Assert.False(c.TosAccepted);
        Assert.False(c.DevMode);
        Assert.True(c.ShowInGameHighlight);
        Assert.False(c.ShowSuggestionDetails);
        Assert.True(c.EnableGameLogging);
    }

    [Fact]
    public void Configuration_implements_IPluginConfiguration()
    {
        var c = new Configuration();
        Assert.IsAssignableFrom<global::Dalamud.Configuration.IPluginConfiguration>(c);
    }
}
