using Mahjong.Advisor;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using MahjongAdvisor.Reading;

namespace MahjongAdvisor.UI;

public sealed class DebugWindow : Window
{
    private readonly Plugin plugin;
    private List<NodeDump> lastDump = [];
    private string dumpStatus = "Sit at a table with a few tiles discarded, then dump.";
    private readonly int[] pondInput = new int[4];
    private readonly int[] meldInput = new int[4];
    private bool inputsLoaded;
    private bool settingsDirty;

    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.75f, 0.30f, 1f);

    public DebugWindow(Plugin plugin)
        : base("Mahjong Advisor - Setup##mjadv-debug")
    {
        ArgumentNullException.ThrowIfNull(plugin);
        this.plugin = plugin;
        Size = new Vector2(560, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        // Sliders fire every frame while dragging. Values apply to the config
        // object immediately, but the disk write waits until the widget is
        // released -- a save per drag-frame is the Consolidator database-locked
        // bug all over again.
        if (settingsDirty && !ImGui.IsAnyItemActive())
        {
            plugin.SaveConfiguration();
            settingsDirty = false;
        }

        if (!ImGui.BeginTabBar("##mjadv-tabs"))
            return;

        if (ImGui.BeginTabItem("Board"))
        {
            DrawBoardTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Node mapping"))
        {
            DrawNodeTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Settings"))
        {
            DrawSettingsTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawBoardTab()
    {
        var read = plugin.LastRead;
        if (read is null)
        {
            ImGui.TextColored(Muted, "No read yet.");
            return;
        }

        ImGui.TextColored(Muted, $"Build {MahjongAdvisor.Logging.GameLogger.BuildStamp}");
        ImGui.TextUnformatted($"Profile: {read.Profile?.Name ?? "none"}  ({read.Profile?.AddonName ?? "-"})");
        ImGui.TextUnformatted($"State code: {read.StateCode}");
        ImGui.TextUnformatted($"Status: {read.Status}");
        if (read.RejectedTiles > 0)
            ImGui.TextColored(Warn, $"{read.RejectedTiles} hand-array entries did not decode.");
        if (read.TextureBaseShifted)
            ImGui.TextColored(Warn, "Tile texture base shifted and was retuned automatically.");

        var table = read.Table;
        if (table is null)
            return;

        ImGui.Separator();
        ImGui.TextUnformatted($"Hand: {Tiles.Format(table.Hand)}");
        var order = plugin.LastRead?.HandOrder;
        if (order is { Count: > 0 })
        {
            ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(Muted,
                "In order: " + string.Join(", ", order.Select((t, i) => $"{i + 1}:{Tiles.DescribeShort(t)}")));
            ImGui.PopTextWrapPos();
        }
        ImGui.TextUnformatted($"Melds called: {table.CalledMelds}   Wall: {table.WallRemaining}");
        bool meldsRead = table.Observations.HasFlag(Observed.OpponentMelds);
        ImGui.TextColored(meldsRead ? Good : Warn,
            $"Opponent melds: {(meldsRead ? "read" : "not read")}");
        if (table.Scores.Count == 4)
        {
            int rank = PlacementAdjuster.RankOf(table.Scores);
            ImGui.TextUnformatted(
                $"Scores: {string.Join("  ", table.Scores.Select(x => x.ToString("N0")))}"
                + $"   (you are {PlacementAdjuster.Describe(rank)})");
        }
        else
        {
            ImGui.TextColored(Warn, "Scores: not read");
        }
        ImGui.TextUnformatted($"Dora indicators: {string.Join(" ", table.DoraIndicators.Select(Tiles.Name))}");

        ImGui.Separator();
        DrawPromptDiagnostics();
        DrawSuggestion();

        ImGui.Spacing();
        var locator = plugin.Reader.DiscardArrays;
        if (locator.RelativeOffset is not null)
            ImGui.TextColored(Good, $"Discards read from memory - {locator.Status}");
        else
            ImGui.TextColored(Muted, $"Memory search: {locator.Status}");

        var mine = table.Seats.Select(x => x.Discards.Count).ToArray();
        var clientCounts = plugin.Reader.DiscardCountBytes;
        if (clientCounts.Length == 4)
        {
            bool agrees = mine.Zip(clientCounts).All(p => p.First == p.Second || p.First == p.Second - 1);
            ImGui.TextColored(agrees ? Good : Warn,
                $"Piles read {string.Join("/", mine)} - client says {string.Join("/", clientCounts)}");
        }

        int[] autoIds = plugin.Reader.AutoPondNodeIds;
        if (autoIds.Length == 4)
            ImGui.TextColored(Good, $"Discard piles found automatically ({string.Join(", ", autoIds)})");
        else if (plugin.Reader.LastAutoMap is { } m)
            ImGui.TextColored(Muted, $"Finding discard piles: {m.Diagnostic}");

        ImGui.Spacing();
        ImGui.TextColored(Muted, "Observed fields");
        foreach (Observed flag in Enum.GetValues<Observed>())
        {
            if (flag == Observed.None || flag == Observed.SafetyReady)
                continue;
            bool set = table.Observations.HasFlag(flag);
            ImGui.TextColored(set ? Good : Muted, $"{(set ? "yes" : " no")}  {flag}");
        }

        ImGui.Spacing();
        ImGui.TextColored(Muted, "Opponent readiness");
        foreach (var threat in new ThreatModel().Evaluate(table))
        {
            string[] names = ["", "Shimocha", "Toimen", "Kamicha"];
            ImGui.TextColored(threat.Riichi ? Warn : Muted,
                $"  {names[threat.Seat],-9} {threat.TenpaiProbability * 100:0}% ready"
                + $"   feeds ~{threat.ExpectedHandValue:0}"
                + (threat.Riichi ? "   RIICHI" : ""));
        }

        ImGui.Spacing();
        if (ImGui.BeginTable("##mjadv-seats", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Seat", ImGuiTableColumnFlags.WidthFixed, 60);
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
                ImGui.TextColored(seat.Riichi ? Warn : Muted, seat.Riichi ? "yes" : "-");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Join(" ", seat.Discards.Select(Tiles.Name)));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Join(" ", seat.MeldTiles.Select(Tiles.Name)));
            }

            ImGui.EndTable();
        }
    }

    private void DrawPromptDiagnostics()
    {
        var prompt = plugin.LastPrompt;
        if (prompt.Available.Count == 0)
            return;

        ImGui.TextColored(Good,
            $"Call prompt: {string.Join(" / ", prompt.Available)}");
        bool selfOnly = prompt.Available.All(a => a is CallKind.Riichi or CallKind.Tsumo or CallKind.Pass);
        ImGui.TextColored(Muted,
            prompt.HasClaimedTile
                ? $"Claimed tile: {Tiles.Describe(prompt.ClaimedTile)}"
                : selfOnly
                    ? "Claimed tile: n/a (declared on your own turn)"
                    : "Claimed tile: not decoded");
        ImGui.TextColored(Muted, $"Labels seen: {string.Join(" | ", prompt.RawLabels)}");
        ImGui.TextColored(Muted, $"Raw claim slots: {string.Join(" ", prompt.RawClaimCandidates)}");

        var call = plugin.LastCallAdvice;
        if (call?.Best is not null)
        {
            ImGui.TextColored(Good, $"Call advice: {call.Best.Kind}");
            ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
            ImGui.TextUnformatted(call.Headline);
            ImGui.PopTextWrapPos();
        }

        ImGui.Separator();
    }

    private void DrawSuggestion()
    {
        var advice = plugin.LastAdvice;
        if (advice?.Best is null)
        {
            var table = plugin.LastRead?.Table;
            int total = table is null ? -1 : Tiles.Total(table.Hand);
            ImGui.TextColored(Muted,
                total >= 0 && total % 3 == 1
                    ? $"No suggestion: waiting for a draw ({total} tiles)."
                    : $"No suggestion (hand total {total}).");
            return;
        }

        var best = advice.Best;
        var t = plugin.LastRead?.Table;
        if (t is not null)
        {
            int dora = t.CountDora();
            ImGui.TextColored(Muted,
                $"Hand worth about {plugin.Configuration.Advisor.EstimateWinValue(t):0} "
                + $"({dora} dora{(t.CalledMelds == 0 ? ", closed" : ", open")})");
        }

        ImGui.TextColored(advice.Folding ? Warn : Good,
            $"Suggested discard: {Tiles.Describe(best.Tile)}");
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(advice.Headline);
        ImGui.PopTextWrapPos();

        string alts = string.Join("   ", advice.Options.Skip(1).Take(3)
            .Select(o => $"{Tiles.DescribeShort(o.Tile)} ({o.Ukeire})"));
        if (alts.Length > 0)
            ImGui.TextColored(Muted, $"Alternatives: {alts}");

        if (!plugin.Configuration.ShowOverlay)
        {
            ImGui.TextColored(Warn, "Overlay window is hidden.");
            ImGui.SameLine();
            if (ImGui.Button("Show it"))
                plugin.ShowOverlay();
        }
    }

    private void DrawNodeTab()
    {
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(
            "The discard piles are found automatically once a few tiles are down. " +
            "You only need anything on this tab if that fails.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        var auto = plugin.Reader.LastAutoMap;
        int[] accepted = plugin.Reader.AutoPondNodeIds;

        if (accepted.Length == 4)
        {
            ImGui.TextColored(Good, $"Detected automatically: {string.Join(", ", accepted)}");
            ImGui.TextColored(Muted, "self, shimocha, toimen, kamicha");
            if (ImGui.Button("Save these to the profile"))
            {
                var target = plugin.Reader.ActiveProfile;
                if (target is not null)
                {
                    target.PondContainerNodeIds = (int[])accepted.Clone();
                    plugin.SaveLayoutProfile(target);
                    dumpStatus = "Saved. Detection will be skipped from now on.";
                }
            }
        }
        else if (auto is not null)
        {
            ImGui.TextColored(Warn, $"Detecting: {auto.Diagnostic}");
        }
        else
        {
            ImGui.TextColored(Muted, "Waiting for a table.");
        }

        var activeProfile = plugin.Reader.ActiveProfile;
        if (activeProfile is not null)
        {
            ImGui.Spacing();
            ImGui.TextColored(Muted, "Meld node families (self, shimocha, toimen, kamicha):");
            string[] seatNames = ["Self", "Shimocha", "Toimen", "Kamicha"];
            for (int seat = 0; seat < 4; seat++)
            {
                ImGui.TextColored(Muted,
                    $"  {seatNames[seat],-9} {string.Join(", ", activeProfile.MeldNodeIdsForSeat(seat))}");
            }
        }

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Manual override"))
            return;

        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(Muted,
            "Only needed if detection cannot find the piles. Dump the tree and look for the " +
            "four nodes whose tile counts match the piles on screen.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        if (ImGui.Button("Dump node tree"))
            RunDump();

        ImGui.SameLine();
        ImGui.TextColored(Muted, dumpStatus);

        var profile = plugin.Reader.ActiveProfile;
        if (profile is null)
        {
            ImGui.TextColored(Warn, "No addon detected - open a mahjong table first.");
            return;
        }

        if (!inputsLoaded)
        {
            LoadInputs(profile);
            inputsLoaded = true;
        }

        ImGui.Separator();
        ImGui.TextColored(Muted, "Pond container node ids (self, shimocha, toimen, kamicha)");
        for (int i = 0; i < 4; i++)
        {
            ImGui.SetNextItemWidth(90);
            ImGui.InputInt($"##pond{i}", ref pondInput[i]);
            if (i < 3)
                ImGui.SameLine();
        }

        ImGui.TextColored(Muted, "Meld container node ids (optional)");
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
            dumpStatus = "Saved. Watch the Board tab to confirm discards appear.";
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            Array.Clear(pondInput);
            Array.Clear(meldInput);
        }

        ImGui.Separator();
        if (lastDump.Count == 0)
        {
            var scan = plugin.NodeInspector.LastScan;
            if (scan.NodesVisited == 0)
            {
                ImGui.TextColored(Muted, "No dump yet.");
            }
            else
            {
                ImGui.TextColored(Warn,
                    $"Visited {scan.NodesVisited} nodes, {scan.ImageNodes} with textures, {scan.TilesDecoded} decoded as tiles.");
                if (scan.UnrecognisedTextureIds.Count > 0)
                {
                    ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
                    ImGui.TextColored(Muted,
                        "Texture ids seen but not recognised: " + string.Join(" ", scan.UnrecognisedTextureIds));
                    ImGui.PopTextWrapPos();
                }
            }

            return;
        }

        if (ImGui.BeginChild("##mjadv-dump", new Vector2(0, 220)))
        {
            if (ImGui.BeginTable("##mjadv-dumptable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Node", ImGuiTableColumnFlags.WidthFixed, 56);
                ImGui.TableSetupColumn("Tiles", ImGuiTableColumnFlags.WidthFixed, 46);
                ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthFixed, 56);
                ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 56);
                ImGui.TableSetupColumn("Sample texture ids");
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

        // EndChild is unconditional.
        ImGui.EndChild();
    }

    private void DrawSettingsTab()
    {
        var config = plugin.Configuration;
        bool dirty = false;

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

        bool openOnLaunch = config.OpenSetupOnLaunch;
        if (ImGui.Checkbox("Open this setup window on launch", ref openOnLaunch))
        {
            config.OpenSetupOnLaunch = openOnLaunch;
            dirty = true;
        }

        bool locked = config.LockOverlay;
        if (ImGui.Checkbox("Lock overlay position", ref locked))
        {
            config.LockOverlay = locked;
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

        ImGui.Separator();
        ImGui.TextColored(Muted, "Game logging");

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

        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(Muted, plugin.GameLogger.Directory);
        ImGui.TextColored(Muted,
            $"{plugin.GameLogger.RecordsWritten} records this file"
            + (plugin.GameLogger.CurrentFile is null
                ? " (no file yet)"
                : $" -> {Path.GetFileName(plugin.GameLogger.CurrentFile)}"));
        ImGui.PopTextWrapPos();

        ImGui.Separator();
        ImGui.TextColored(Muted, "Advisor tuning");
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(Muted,
            "These defaults are fitted to real payouts and top-lobby statistics, and are what " +
            "the advisor plays best with. Change them only to make it deliberately more or less " +
            "cautious than the data says.");
        ImGui.PopTextWrapPos();

        if (ImGui.Button("Reset to recommended"))
        {
            config.AverageWinValue = 1300;
            config.AverageDealInCost = 5200;
            config.NotenPenaltyValue = 1500;
            config.OpponentHandValue = 4000;
            dirty = true;
        }
        ImGui.PopTextWrapPos();

        float win = (float)config.AverageWinValue;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Base hand value", ref win, 500f, 4000f, "%.0f"))
        {
            config.AverageWinValue = win;
            dirty = true;
        }

        float dealIn = (float)config.AverageDealInCost;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Caution (deal-in cost)", ref dealIn, 1000f, 10000f, "%.0f"))
        {
            config.AverageDealInCost = dealIn;
            dirty = true;
        }

        float oppValue = (float)config.OpponentHandValue;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Opponent hand value", ref oppValue, 1000f, 9000f, "%.0f"))
        {
            config.OpponentHandValue = oppValue;
            dirty = true;
        }

        float noten = (float)config.NotenPenaltyValue;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Tenpai-at-draw value", ref noten, 0f, 4000f, "%.0f"))
        {
            config.NotenPenaltyValue = noten;
            dirty = true;
        }

        if (dirty)
            settingsDirty = true;
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
            pondInput[i] = profile.PondContainerNodeIds.Length == 4 ? profile.PondContainerNodeIds[i] : 0;
            meldInput[i] = profile.MeldContainerNodeIds.Length == 4 ? profile.MeldContainerNodeIds[i] : 0;
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
            string sample = string.Join(" ", scan.UnrecognisedTextureIds);
            dumpStatus =
                $"{scan.ImageNodes} textures, none decoded. Texture base is likely wrong. Seen: {sample}";
        }

        inputsLoaded = false;
    }
}
