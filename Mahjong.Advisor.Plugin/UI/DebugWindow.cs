using Mahjong.Advisor;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using MahjongAdvisor.Automation;
using MahjongAdvisor.Logging;
using MahjongAdvisor.Reading;

namespace MahjongAdvisor.UI;

public enum SetupTab
{
    Advisor,
    Automation,
    Setup,
    Settings,
    About,
}

/// <summary>
/// The main control panel. Kept separate from the overlay so the always-on
/// advice can stay small while diagnostics, automation and tuning live here.
/// </summary>
public sealed class DebugWindow : Window
{
    private readonly Plugin plugin;
    private SetupTab? pendingTab;
    private List<NodeDump> lastDump = [];
    private string dumpStatus = "Sit at a table with a few tiles discarded, then dump.";
    private readonly int[] pondInput = new int[4];
    private readonly int[] meldInput = new int[4];
    private string loadedProfile = "";
    private bool settingsDirty;

    public DebugWindow(Plugin plugin)
        : base("Mahjong Advisor - Control Panel##mjadv-debug")
    {
        ArgumentNullException.ThrowIfNull(plugin);
        this.plugin = plugin;
        Size = new Vector2(640, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 380),
            MaximumSize = new Vector2(1100, 1100),
        };
    }

    /// <summary>Open the panel and select a tab. Used by the installer buttons and commands.</summary>
    public void Open(SetupTab tab)
    {
        pendingTab = tab;
        IsOpen = true;
    }

    public override void Draw()
    {
        // Sliders fire every frame while dragging. Apply to config immediately,
        // but only write to disk once the widget is released.
        if (settingsDirty && !ImGui.IsAnyItemActive())
        {
            plugin.SaveConfiguration();
            settingsDirty = false;
        }

        if (!ImGui.BeginTabBar("##mjadv-tabs"))
            return;

        DrawTab("Advisor", SetupTab.Advisor, DrawAdvisorTab);
        DrawTab("Automation", SetupTab.Automation, DrawAutomationTab);
        DrawTab("Setup", SetupTab.Setup, DrawSetupTab);
        DrawTab("Settings", SetupTab.Settings, DrawSettingsTab);
        DrawTab("About", SetupTab.About, DrawAboutTab);

        ImGui.EndTabBar();
        pendingTab = null;
    }

    private void DrawTab(string label, SetupTab tab, Action draw)
    {
        var flags = pendingTab == tab ? ImGuiTabItemFlags.SetSelected : 0;
        if (!ImGui.BeginTabItem(label, flags))
            return;

        draw();
        ImGui.EndTabItem();
    }

    // ---------------------------------------------------------------- Advisor

    private void DrawAdvisorTab()
    {
        var read = plugin.LastRead;
        if (read is null)
        {
            UiTheme.MutedText("No read yet. Open a Doman Mahjong table.");
            return;
        }

        UiTheme.Section("Table");
        UiTheme.KeyValue("Status", read.Status);
        UiTheme.KeyValue("Profile", $"{read.Profile?.Name ?? "none"}  ({read.Profile?.AddonName ?? "-"})");
        UiTheme.KeyValue("State", read.StateCode.ToString());
        UiTheme.KeyValue("Build", GameLogger.BuildStamp);

        if (read.RejectedTiles > 0)
            ImGui.TextColored(UiTheme.Warn, $"{read.RejectedTiles} hand-array entries did not decode.");
        if (read.TextureBaseShifted)
            ImGui.TextColored(UiTheme.Warn, "Tile texture base shifted and was retuned automatically.");

        var table = read.Table;
        if (table is null)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.Warn, "No readable hand yet - the table is probably still dealing.");
            return;
        }

        UiTheme.Section("Hand");
        UiTheme.KeyValue("Hand", Tiles.Format(table.Hand));
        UiTheme.KeyValue("Wall", table.WallRemaining.ToString());
        UiTheme.KeyValue("Dora", string.Join(" ", table.DoraIndicators.Select(Tiles.Name)));
        UiTheme.KeyValue("Meld data", table.Observations.HasFlag(Observed.OpponentMelds) ? "read" : "not read");
        UiTheme.KeyValue("Hand slots", read.HandSlotsValid ? "read" : "not read");

        if (table.Scores.Count == 4)
        {
            int rank = PlacementAdjuster.RankOf(table.Scores);
            UiTheme.KeyValue("Scores", string.Join("  ", table.Scores.Select(x => x.ToString("N0"))));
            UiTheme.KeyValue("Standing", PlacementAdjuster.Describe(rank));
        }
        else
        {
            UiTheme.KeyValue("Scores", "not read");
        }

        DrawCurrentAdvice(read);
        DrawThreats(table);
        DrawSeats(table);

        if (ImGui.CollapsingHeader("Observation flags"))
        {
            foreach (Observed flag in Enum.GetValues<Observed>())
            {
                if (flag is Observed.None or Observed.SafetyReady)
                    continue;
                bool set = table.Observations.HasFlag(flag);
                ImGui.TextColored(set ? UiTheme.Good : UiTheme.Muted,
                    $"{(set ? "yes" : " no")}  {flag}");
            }
        }

        if (ImGui.CollapsingHeader("Read diagnostics"))
        {
            var locator = plugin.Reader.DiscardArrays;
            UiTheme.MutedText($"Discards: {locator.Status}");

            var mine = table.Seats.Select(x => x.Discards.Count).ToArray();
            var clientCounts = plugin.Reader.DiscardCountBytes;
            if (clientCounts.Length == 4)
            {
                bool agrees = mine.Zip(clientCounts)
                    .All(p => p.First == p.Second || p.First == p.Second - 1);
                ImGui.TextColored(agrees ? UiTheme.Good : UiTheme.Warn,
                    $"Piles read {string.Join("/", mine)} - client says {string.Join("/", clientCounts)}");
            }

            if (plugin.Reader.AutoPondNodeIds.Length == 4)
            {
                ImGui.TextColored(UiTheme.Good,
                    $"Pond nodes: {string.Join(", ", plugin.Reader.AutoPondNodeIds)}");
            }
            else if (plugin.Reader.LastAutoMap is { } map)
            {
                UiTheme.MutedText($"Pond detection: {map.Diagnostic}");
            }

            if (read.Profile is { } profile)
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    UiTheme.MutedText(
                        $"Meld nodes seat {seat}: {string.Join(", ", profile.MeldNodeIdsForSeat(seat))}");
                }

                UiTheme.MutedText($"Hand slot nodes: {string.Join(", ", profile.HandSlotNodeIds)} / {profile.DrawnTileNodeId}");
            }

            if (plugin.LastPrompt.Options.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(UiTheme.Accent, "Popup buttons");
                foreach (var option in plugin.LastPrompt.Options)
                    UiTheme.MutedText($"[11,{option.Option}]  {option.Label}");
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Show overlay"))
            plugin.ShowOverlay();
        ImGui.SameLine();
        if (ImGui.Button("Dump hand slots"))
            plugin.DumpHandSlots();
        ImGui.SameLine();
        if (ImGui.Button("Dump melds"))
            plugin.DumpMelds();
    }

    private void DrawCurrentAdvice(BoardRead read)
    {
        UiTheme.Section("Recommendation");

        if (plugin.LastCallAdvice is { Best: not null } call)
        {
            ImGui.TextColored(UiTheme.Warn, "CALL");
            ImGui.SameLine();
            ImGui.TextColored(UiTheme.Good, call.Best.Kind.ToString().ToUpperInvariant());
            UiTheme.Wrapped(call.Headline);
            return;
        }

        var advice = plugin.LastAdvice;
        if (advice?.Best is null)
        {
            int total = Tiles.Total(read.Table!.Hand);
            UiTheme.MutedText(total % 3 == 1
                ? "Waiting for your draw."
                : "No discard advice for this state.");
            return;
        }

        var best = advice.Best;
        ImGui.TextColored(advice.Folding ? UiTheme.Warn : UiTheme.Good,
            $"{Tiles.Describe(best.Tile)}  ({UiTheme.ShapeLabel(best.ResultingShanten)})");
        UiTheme.Wrapped(advice.Headline);
    }

    private static void DrawThreats(TableState table)
    {
        UiTheme.Section("Opponents");
        if (!ImGui.BeginTable("##mj-threats", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("Seat", ImGuiTableColumnFlags.WidthFixed, 86);
        ImGui.TableSetupColumn("Ready", ImGuiTableColumnFlags.WidthFixed, 74);
        ImGui.TableSetupColumn("Feeds", ImGuiTableColumnFlags.WidthFixed, 74);
        ImGui.TableSetupColumn("Riichi");
        ImGui.TableHeadersRow();

        string[] names = ["", "Shimocha", "Toimen", "Kamicha"];
        foreach (var threat in new ThreatModel().Evaluate(table))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(names[threat.Seat]);
            ImGui.TableNextColumn();
            ImGui.TextColored(threat.Riichi ? UiTheme.Bad : UiTheme.Muted,
                $"{threat.TenpaiProbability * 100:0}%");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{threat.ExpectedHandValue:0}");
            ImGui.TableNextColumn();
            ImGui.TextColored(threat.Riichi ? UiTheme.Bad : UiTheme.Muted,
                threat.Riichi ? "yes" : "-");
        }

        ImGui.EndTable();
    }

    private static void DrawSeats(TableState table)
    {
        if (!ImGui.BeginTable("##mj-seats", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("Seat", ImGuiTableColumnFlags.WidthFixed, 86);
        ImGui.TableSetupColumn("Riichi", ImGuiTableColumnFlags.WidthFixed, 54);
        ImGui.TableSetupColumn("Discards");
        ImGui.TableSetupColumn("Melds");
        ImGui.TableHeadersRow();

        string[] names = ["Self", "Shimocha", "Toimen", "Kamicha"];
        for (int i = 0; i < 4; i++)
        {
            var seat = table.Seats[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(names[i]);
            ImGui.TableNextColumn();
            ImGui.TextColored(seat.Riichi ? UiTheme.Warn : UiTheme.Muted, seat.Riichi ? "yes" : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.Join(" ", seat.Discards.Select(Tiles.Name)));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.Join(" ", seat.MeldTiles.Select(Tiles.Name)));
        }

        ImGui.EndTable();
    }

    // -------------------------------------------------------------- Automation

    private void DrawAutomationTab()
    {
        var config = plugin.Configuration;

        UiTheme.Section("Auto-play");
        UiTheme.WrappedColored(
            "Auto-play sends the same inputs the client sends when you click. "
            + "It is off by default. Off means suggestions only; nothing is clicked for you.",
            UiTheme.Muted);

        bool auto = config.AutoPlay;
        if (ImGui.Checkbox("Enable auto-play", ref auto))
            plugin.SetAutoPlay(auto ? "on" : "off");

        if (auto)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop now"))
                plugin.SetAutoPlay("off");

            ImGui.TextColored(UiTheme.Warn,
                $"Status: {plugin.AutoPlay.LastStatus}   Last: {plugin.AutoPlay.Stats.LastAction}");
            UiTheme.MutedText(
                $"Discards {plugin.AutoPlay.Stats.Discards}   "
                + $"Calls {plugin.AutoPlay.Stats.Calls}   "
                + $"Riichi {plugin.AutoPlay.Stats.Riichi}   "
                + $"Wins {plugin.AutoPlay.Stats.Wins}   "
                + $"Advances {plugin.AutoPlay.Stats.Advances}");
        }
        else
        {
            ImGui.TextColored(UiTheme.Muted, "Manual mode: the overlay only suggests.");
        }

        ImGui.Spacing();
        bool pon = config.AutoPlayAllowPon;
        if (ImGui.Checkbox("Take pon", ref pon))
        {
            config.AutoPlayAllowPon = pon;
            settingsDirty = true;
        }

        bool chi = config.AutoPlayAllowChi;
        if (ImGui.Checkbox("Take chi (experimental)", ref chi))
        {
            config.AutoPlayAllowChi = chi;
            settingsDirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "The chi shape picker is not mapped. If chi is enabled, auto-play "
                + "pauses on that screen and waits for you to choose the shape.");
        }

        bool kan = config.AutoPlayAllowKan;
        if (ImGui.Checkbox("Take kan (flips dora)", ref kan))
        {
            config.AutoPlayAllowKan = kan;
            settingsDirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "A kan reveals another dora indicator for every seat. "
                + "Leave this off unless you want the extra risk.");
        }

        bool advance = config.AutoPlayAdvanceResults;
        if (ImGui.Checkbox("Advance result screens", ref advance))
        {
            config.AutoPlayAdvanceResults = advance;
            settingsDirty = true;
        }

        if (ImGui.CollapsingHeader("Timing"))
        {
            int turnMin = config.AutoPlayTurnDelayMinMs;
            int turnMax = config.AutoPlayTurnDelayMaxMs;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt("Turn min (ms)", ref turnMin, 0, 5000))
            {
                config.AutoPlayTurnDelayMinMs = turnMin;
                settingsDirty = true;
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt("Turn max (ms)", ref turnMax, 0, 5000))
            {
                config.AutoPlayTurnDelayMaxMs = turnMax;
                settingsDirty = true;
            }

            if (turnMax < turnMin)
            {
                turnMax = turnMin;
                config.AutoPlayTurnDelayMaxMs = turnMax;
                settingsDirty = true;
            }

            int callMin = config.AutoPlayCallDelayMinMs;
            int callMax = config.AutoPlayCallDelayMaxMs;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt("Call min (ms)", ref callMin, 0, 5000))
            {
                config.AutoPlayCallDelayMinMs = callMin;
                settingsDirty = true;
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt("Call max (ms)", ref callMax, 0, 5000))
            {
                config.AutoPlayCallDelayMaxMs = callMax;
                settingsDirty = true;
            }

            if (callMax < callMin)
            {
                callMax = callMin;
                config.AutoPlayCallDelayMaxMs = callMax;
                settingsDirty = true;
            }

            int resultMin = config.AutoPlayResultDelayMinMs;
            int resultMax = config.AutoPlayResultDelayMaxMs;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt("Result min (ms)", ref resultMin, 0, 8000))
            {
                config.AutoPlayResultDelayMinMs = resultMin;
                settingsDirty = true;
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt("Result max (ms)", ref resultMax, 0, 8000))
            {
                config.AutoPlayResultDelayMaxMs = resultMax;
                settingsDirty = true;
            }

            if (resultMax < resultMin)
            {
                resultMax = resultMin;
                config.AutoPlayResultDelayMaxMs = resultMax;
                settingsDirty = true;
            }
        }

        ImGui.Spacing();
        UiTheme.WrappedColored(
            "Auto-play is for casual Doman Mahjong only. It never queues a match, "
            + "never leaves the table, and never advances the new-hand screen. "
            + "Use /mjadv stop or the Stop button to return to manual play immediately.",
            UiTheme.Muted);
    }

    // ------------------------------------------------------------------- Setup

    private void DrawSetupTab()
    {
        UiTheme.Section("Discard piles");
        var autoMap = plugin.Reader.LastAutoMap;
        int[] accepted = plugin.Reader.AutoPondNodeIds;

        if (accepted.Length == 4)
        {
            ImGui.TextColored(UiTheme.Good, $"Detected: {string.Join(", ", accepted)}");
            UiTheme.MutedText("self, shimocha, toimen, kamicha");
            if (ImGui.Button("Save detected piles"))
            {
                var target = plugin.Reader.ActiveProfile;
                if (target is not null)
                {
                    target.PondContainerNodeIds = (int[])accepted.Clone();
                    plugin.SaveLayoutProfile(target);
                    dumpStatus = "Saved. Automatic pond detection will be skipped.";
                }
            }
        }
        else if (autoMap is not null)
        {
            ImGui.TextColored(UiTheme.Warn, $"Detecting: {autoMap.Diagnostic}");
        }
        else
        {
            UiTheme.MutedText("Waiting for a table.");
        }

        UiTheme.Section("Called melds");
        var read = plugin.LastRead;
        bool meldsRead = read?.Table?.Observations.HasFlag(Observed.OpponentMelds) == true;
        ImGui.TextColored(meldsRead ? UiTheme.Good : UiTheme.Warn,
            meldsRead ? "Meld strips are being read." : "Meld strips are not read; readiness is conservative.");

        UiTheme.Section("Hand slots");
        bool slotsRead = read?.HandSlotsValid == true;
        ImGui.TextColored(slotsRead ? UiTheme.Good : UiTheme.Warn,
            slotsRead ? "Display slot order matches the hand array." : "Display slot order is not verified.");
        UiTheme.WrappedColored(
            "Auto-play needs the display slot order to know which tile the discard callback "
            + "addresses. If this is red, use /mjadv hand while at the table and send the log.",
            UiTheme.Muted);

        var profile = plugin.Reader.ActiveProfile;
        if (profile is not null)
        {
            UiTheme.MutedText($"Hand slot nodes: {string.Join(", ", profile.HandSlotNodeIds)} / drawn {profile.DrawnTileNodeId}");
            for (int seat = 0; seat < 4; seat++)
                UiTheme.MutedText($"Meld nodes seat {seat}: {string.Join(", ", profile.MeldNodeIdsForSeat(seat))}");
        }

        ImGui.Spacing();
        if (ImGui.Button("Dump node tree"))
            RunDump();
        ImGui.SameLine();
        if (ImGui.Button("Dump visible nodes"))
            plugin.DumpVisibleNodes();
        ImGui.SameLine();
        if (ImGui.Button("Dump hand slots"))
            plugin.DumpHandSlots();
        ImGui.SameLine();
        if (ImGui.Button("Dump melds"))
            plugin.DumpMelds();

        UiTheme.MutedText(dumpStatus);

        if (ImGui.CollapsingHeader("Manual override"))
        {
            UiTheme.WrappedColored(
                "Only needed if automatic detection fails. The node tree dump lists every "
                + "container with its tile count; the four piles are the ones whose counts "
                + "match the screen.",
                UiTheme.Muted);

            if (profile is null)
            {
                ImGui.TextColored(UiTheme.Warn, "Open a mahjong table first.");
            }
            else
            {
                if (loadedProfile != profile.AddonName)
                {
                    LoadInputs(profile);
                    loadedProfile = profile.AddonName;
                }

                ImGui.TextColored(UiTheme.Muted, "Pond nodes (self, shimocha, toimen, kamicha)");
                for (int i = 0; i < 4; i++)
                {
                    ImGui.SetNextItemWidth(90);
                    ImGui.InputInt($"##pond{i}", ref pondInput[i]);
                    if (i < 3)
                        ImGui.SameLine();
                }

                ImGui.TextColored(UiTheme.Muted, "Meld nodes (optional)");
                for (int i = 0; i < 4; i++)
                {
                    ImGui.SetNextItemWidth(90);
                    ImGui.InputInt($"##meld{i}", ref meldInput[i]);
                    if (i < 3)
                        ImGui.SameLine();
                }

                if (ImGui.Button("Save mapping"))
                {
                    profile.PondContainerNodeIds = pondInput.All(v => v > 0) ? (int[])pondInput.Clone() : [];
                    profile.MeldContainerNodeIds = meldInput.All(v => v > 0) ? (int[])meldInput.Clone() : [];
                    plugin.SaveLayoutProfile(profile);
                    dumpStatus = "Saved. Check the Advisor tab to confirm the reads.";
                }

                ImGui.SameLine();
                if (ImGui.Button("Clear"))
                {
                    Array.Clear(pondInput);
                    Array.Clear(meldInput);
                }
            }
        }

        if (lastDump.Count == 0)
        {
            var scan = plugin.NodeInspector.LastScan;
            if (scan.NodesVisited > 0)
            {
                ImGui.TextColored(UiTheme.Warn,
                    $"Visited {scan.NodesVisited} nodes, {scan.ImageNodes} textures, "
                    + $"{scan.TilesDecoded} decoded as tiles.");
                if (scan.UnrecognisedTextureIds.Count > 0)
                    UiTheme.WrappedColored(
                        "Unrecognised texture ids: " + string.Join(" ", scan.UnrecognisedTextureIds),
                        UiTheme.Muted);
            }

            return;
        }

        if (ImGui.BeginChild("##mjadv-dump", new Vector2(0, 220)))
        {
            if (ImGui.BeginTable("##mjadv-dumptable", 5,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Node", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("Tiles", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Sample textures");
                ImGui.TableHeadersRow();

                foreach (var node in lastDump)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(node.NodeId.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(node.TileCount.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{node.X:0}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{node.Y:0}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(string.Join(" ", node.SampleTextureIds));
                }

                ImGui.EndTable();
            }
        }

        ImGui.EndChild();
    }

    // ---------------------------------------------------------------- Settings

    private void DrawSettingsTab()
    {
        var config = plugin.Configuration;
        bool dirty = false;

        UiTheme.Section("Overlay");
        bool showOverlay = config.ShowOverlay;
        if (ImGui.Checkbox("Show overlay", ref showOverlay))
        {
            config.ShowOverlay = showOverlay;
            if (showOverlay)
                plugin.ShowOverlay();
            dirty = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset position"))
            plugin.ShowOverlay();

        bool onlyAtTable = config.OverlayOnlyAtTable;
        if (ImGui.Checkbox("Only while a table is open", ref onlyAtTable))
        {
            config.OverlayOnlyAtTable = onlyAtTable;
            dirty = true;
        }

        bool locked = config.LockOverlay;
        if (ImGui.Checkbox("Lock overlay position", ref locked))
        {
            config.LockOverlay = locked;
            dirty = true;
        }

        bool openOnLaunch = config.OpenSetupOnLaunch;
        if (ImGui.Checkbox("Open the control panel on launch", ref openOnLaunch))
        {
            config.OpenSetupOnLaunch = openOnLaunch;
            dirty = true;
        }

        bool showAccepted = config.ShowAcceptedTiles;
        if (ImGui.Checkbox("Show accepted tiles", ref showAccepted))
        {
            config.ShowAcceptedTiles = showAccepted;
            dirty = true;
        }

        int alternatives = config.AlternativesShown;
        ImGui.SetNextItemWidth(140);
        if (ImGui.SliderInt("Alternatives shown", ref alternatives, 0, 6))
        {
            config.AlternativesShown = alternatives;
            dirty = true;
        }

        UiTheme.Section("Logging");
        bool logging = config.LogGames;
        if (ImGui.Checkbox("Log every decision to file", ref logging))
        {
            config.LogGames = logging;
            plugin.GameLogger.Enabled = logging;
            if (logging)
                plugin.GameLogger.StartNewGame();
            else
                plugin.GameLogger.Flush();
            dirty = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("New game file"))
            plugin.GameLogger.StartNewGame();

        UiTheme.WrappedColored(plugin.GameLogger.Directory, UiTheme.Muted);
        UiTheme.WrappedColored(
            $"{plugin.GameLogger.RecordsWritten} records this file"
            + (plugin.GameLogger.CurrentFile is null
                ? " (no file yet)"
                : $" -> {Path.GetFileName(plugin.GameLogger.CurrentFile)}"),
            UiTheme.Muted);

        UiTheme.Section("Advisor tuning");
        UiTheme.WrappedColored(
            "The defaults are fitted to real payouts and top-lobby statistics. "
            + "Change them only to make the advisor deliberately more or less cautious.",
            UiTheme.Muted);

        if (ImGui.Button("Reset to recommended"))
        {
            config.AverageWinValue = 1300;
            config.AverageDealInCost = 5200;
            config.NotenPenaltyValue = 1500;
            config.OpponentHandValue = 4000;
            dirty = true;
        }

        float win = (float)config.AverageWinValue;
        ImGui.SetNextItemWidth(170);
        if (ImGui.SliderFloat("Base hand value", ref win, 500f, 4000f, "%.0f"))
        {
            config.AverageWinValue = win;
            dirty = true;
        }

        float dealIn = (float)config.AverageDealInCost;
        ImGui.SetNextItemWidth(170);
        if (ImGui.SliderFloat("Caution (deal-in cost)", ref dealIn, 1000f, 10000f, "%.0f"))
        {
            config.AverageDealInCost = dealIn;
            dirty = true;
        }

        float oppValue = (float)config.OpponentHandValue;
        ImGui.SetNextItemWidth(170);
        if (ImGui.SliderFloat("Opponent hand value", ref oppValue, 1000f, 9000f, "%.0f"))
        {
            config.OpponentHandValue = oppValue;
            dirty = true;
        }

        float noten = (float)config.NotenPenaltyValue;
        ImGui.SetNextItemWidth(170);
        if (ImGui.SliderFloat("Tenpai-at-draw value", ref noten, 0f, 4000f, "%.0f"))
        {
            config.NotenPenaltyValue = noten;
            dirty = true;
        }

        if (dirty)
            settingsDirty = true;
    }

    // ------------------------------------------------------------------- About

    private void DrawAboutTab()
    {
        UiTheme.Section("Mahjong Advisor");
        UiTheme.KeyValue("Build", GameLogger.BuildStamp);
        UiTheme.KeyValue("Author", "Akari");
        UiTheme.KeyValue("API", "Dalamud 15");
        UiTheme.KeyValue("Repo", "github.com/dexcss/MahjongAdvisor");

        ImGui.Spacing();
        UiTheme.WrappedColored(
            "A clean-room Doman Mahjong advisor. The engine is independent of the "
            + "reference solver; only observable client facts (node ids, offsets, "
            + "state codes and the FireCallback protocol) are shared.",
            UiTheme.Muted);

        UiTheme.Section("Commands");
        UiTheme.MutedText("/mjadv — show the overlay");
        UiTheme.MutedText("/mjadv setup — open this control panel");
        UiTheme.MutedText("/mjadv auto on|off — enable or disable auto-play");
        UiTheme.MutedText("/mjadv stop — immediately return to manual play");
        UiTheme.MutedText("/mjadv melds — log the per-seat meld readout");
        UiTheme.MutedText("/mjadv hand — log the display-slot order");
        UiTheme.MutedText("/mjadv options — log the current popup buttons");
        UiTheme.MutedText("/mjadv nodes — open the node-mapping setup");
        UiTheme.MutedText("/mjadv log — toggle JSONL decision logging");
        UiTheme.MutedText("/mjadv geometry / snap — diagnostic dumps");

        UiTheme.Section("Data");
        UiTheme.WrappedColored(
            "Win rates and readiness come from HououTables, measured across roughly "
            + "1.2 million Tenhou top-lobby games (chienshyong/houou-statistics).",
            UiTheme.Muted);
    }

    public override void OnClose()
    {
        if (settingsDirty)
        {
            plugin.SaveConfiguration();
            settingsDirty = false;
        }

        base.OnClose();
    }

    private void LoadInputs(LayoutProfile profile)
    {
        for (int i = 0; i < 4; i++)
        {
            pondInput[i] = profile.PondContainerNodeIds.Length == 4
                ? profile.PondContainerNodeIds[i]
                : 0;
            meldInput[i] = profile.MeldContainerNodeIds.Length == 4
                ? profile.MeldContainerNodeIds[i]
                : 0;
        }
    }

    private unsafe void RunDump()
    {
        var unit = plugin.Reader.TryGetAddon(out var profile);
        if (unit is null || profile is null)
        {
            dumpStatus = "No mahjong addon open.";
            lastDump = [];
            return;
        }

        plugin.NodeInspector.BeginRead(unit, profile.TileTextureBase);
        lastDump = plugin.NodeInspector.Dump(unit, profile.TileTextureBase);
        var scan = plugin.NodeInspector.LastScan;

        if (lastDump.Count > 0)
        {
            dumpStatus = $"{lastDump.Count} candidates from {scan.TilesDecoded} tiles.";
        }
        else if (scan.ImageNodes == 0)
        {
            dumpStatus = $"No tile textures at all ({scan.NodesVisited} nodes visited).";
        }
        else
        {
            dumpStatus = $"{scan.ImageNodes} textures, none decoded. "
                + $"Texture base is likely wrong. Seen: {string.Join(" ", scan.UnrecognisedTextureIds)}";
        }

        loadedProfile = "";
    }
}
