using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace DomanMahjongAI.UI;

public sealed class DebugOverlay : Window, IDisposable
{
    private readonly Plugin plugin;

    public DebugOverlay(Plugin plugin)
        : base("Doman Mahjong AI###domanmahjong-debug")
    {
        this.plugin = plugin;
        Size = new Vector2(520, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        ImGui.TextUnformatted("Doman Mahjong AI — debug overlay");
        ImGui.Separator();

        if (!cfg.TosAccepted)
        {
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.2f, 1f),
                "Automation disabled until ToS acknowledgement is accepted.");
            if (ImGui.Button("Acknowledge and enable automation controls"))
            {
                cfg.TosAccepted = true;
                cfg.Save();
            }
            ImGui.Separator();
        }

        var armed = cfg.AutomationArmed;
        if (ImGui.Checkbox("Automation armed", ref armed))
        {
            cfg.AutomationArmed = armed && cfg.TosAccepted;
            cfg.Save();
        }

        var suggestion = cfg.SuggestionOnly;
        if (ImGui.Checkbox("Suggestion-only mode", ref suggestion))
        {
            cfg.SuggestionOnly = suggestion;
            cfg.Save();
        }

        ImGui.TextUnformatted($"Policy tier: {cfg.PolicyTier}");

        ImGui.Separator();
        DrawAddonPanel();
    }

    private void DrawAddonPanel()
    {
        ImGui.TextUnformatted("AddonEmj status");
        ImGui.Separator();

        var obs = plugin.AddonReader.Poll();

        if (!obs.Present)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                "Addon \"Emj\" not found. Open a Doman Mahjong match in-game.");
            if (obs.LastLifecycleEvent is not null)
                ImGui.TextDisabled($"last event: {obs.LastLifecycleEvent}");
            return;
        }

        ImGui.TextUnformatted($"Address:  0x{obs.Address:X}");
        ImGui.TextUnformatted($"Visible:  {obs.IsVisible}");
        ImGui.TextUnformatted($"Size:     {obs.Width} x {obs.Height}");
        ImGui.TextUnformatted($"Event:    {obs.LastLifecycleEvent ?? "(none)"}");

        var age = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - obs.LastSeenUtcTicks);
        ImGui.TextUnformatted($"Last seen: {age.TotalMilliseconds:F0} ms ago");

        ImGui.Spacing();
        ImGui.TextDisabled("Struct offsets are not yet reverse-engineered — snapshot fields below are placeholders.");

        var snap = plugin.Aggregator.Latest;
        if (snap is not null)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"Schema v{snap.SchemaVersion}");
            ImGui.TextUnformatted($"Our seat: {snap.OurSeat}  |  dealer: {snap.DealerSeat}  |  wall: {snap.WallRemaining}");
        }
    }
}
