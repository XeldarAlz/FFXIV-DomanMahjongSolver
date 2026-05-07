using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace Mahjong.Plugin.Dalamud.UI;

public sealed class AboutWindow : Window, IDisposable
{
    private const string RepoUrl = "https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver";
    private const string IssuesUrl = "https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver/issues";
    private const string DiscussionsUrl = "https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver/discussions";
    private const string SecurityUrl = "https://github.com/XeldarAlz/FFXIV-DomanMahjongSolver/security/advisories/new";
    private const string Author = "XeldarAlz";
    private const string License = "AGPL-3.0-or-later";

    private readonly IPluginLog log;

    public AboutWindow(IPluginLog log) : base("Doman Mahjong Solver — About###domanmahjong-about")
    {
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;

        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(560, 380);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(900, 2000),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        using var _s = Theme.PushWindowStyle();

        DrawHeader();
        ImGui.Dummy(new Vector2(0, 6));
        DrawDetailsTable();
        ImGui.Dummy(new Vector2(0, 8));
        DrawDescription();
    }

    private static void DrawHeader()
    {
        ImGui.SetWindowFontScale(1.20f);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Header);
        ImGui.TextUnformatted("Doman Mahjong Solver");
        ImGui.PopStyleColor();
        ImGui.SetWindowFontScale(1.0f);

        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString() ?? "?";
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.TextUnformatted($"build {version} · {License}");
        ImGui.PopStyleColor();
    }

    private void DrawDetailsTable()
    {
        if (!ImGui.BeginTable("##about_table", 2,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.PadOuterX))
            return;

        ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);

        DrawTextRow("Author", Author);
        DrawLinkRow("GitHub", RepoUrl);
        DrawLinkRow("Report a bug", IssuesUrl);
        DrawLinkRow("Discussions", DiscussionsUrl);
        DrawLinkRow("Security disclosure", SecurityUrl);

        ImGui.EndTable();
    }

    private static void DrawDescription()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(
            "This project is currently in alpha and under active development while the game " +
            "is being reverse-engineered. Expect bugs — if you find any, please report them. " +
            "Impatient? Join me in developing this plugin.");
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 6));

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(
            "Doman Mahjong Solver reads the Gold Saucer mahjong addon and either highlights " +
            "the best move (Hints) or clicks for you with humanized pacing (Auto-play). " +
            "Bug reports and replays from real matches are welcome via GitHub issues.");
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private static void DrawTextRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        ImGui.TableSetColumnIndex(1);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Header);
        ImGui.TextUnformatted(value);
        ImGui.PopStyleColor();
    }

    private void DrawLinkRow(string label, string url)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();

        ImGui.TableSetColumnIndex(1);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Info);
        ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X);
        ImGui.TextUnformatted(url);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted("Click to open · right-click to copy");
        ImGui.EndTooltip();

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            OpenInBrowser(url);
        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.SetClipboardText(url);
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Mahjong.Plugin.Dalamud] failed to launch browser for {0}, copied to clipboard instead", url);
            ImGui.SetClipboardText(url);
        }
    }
}
