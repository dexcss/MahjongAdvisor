using Mahjong.Advisor;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using MahjongAdvisor.Reading;

namespace MahjongAdvisor.UI;

public sealed class AdvisorWindow : Window
{
    private readonly Plugin plugin;
    private bool positionResetPending;

    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.75f, 0.30f, 1f);
    private static readonly Vector4 Bad = new(0.92f, 0.42f, 0.42f, 1f);
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);

    public AdvisorWindow(Plugin plugin)
        : base("Mahjong Advisor##mjadv-main")
    {
        ArgumentNullException.ThrowIfNull(plugin);
        this.plugin = plugin;
        Size = new Vector2(340, 260);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(280, 140),
            MaximumSize = new Vector2(700, 900),
        };
    }

    public override bool DrawConditions()
    {
        var config = plugin.Configuration;
        if (!config.ShowOverlay)
            return false;
        return !config.OverlayOnlyAtTable || plugin.LastRead?.Ok == true;
    }

    /// <summary>Pull the window back to a visible spot next frame.</summary>
    public void RequestPositionReset() => positionResetPending = true;

    public override void PreDraw()
    {
        Flags = plugin.Configuration.LockOverlay
            ? ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
            : ImGuiWindowFlags.None;

        if (positionResetPending)
        {
            positionResetPending = false;
            Position = new Vector2(60, 60);
            PositionCondition = ImGuiCond.Always;
        }
        else
        {
            PositionCondition = ImGuiCond.FirstUseEver;
        }
    }

    public override void Draw()
    {
        var read = plugin.LastRead;
        if (read is null || !read.Ok || read.Table is null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Muted);
            ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
            ImGui.TextUnformatted(read?.Status ?? "Waiting for a mahjong table.");
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
            return;
        }

        // A call prompt must be handled BEFORE the waiting-for-a-draw case: during
        // a prompt the hand is 13 tiles, so that branch was swallowing every call.
        if (plugin.LastCallAdvice is { Best: not null } prompt)
        {
            DrawStandings(read.Table);
            DrawObservationBanner(read.Table);
            ImGui.Separator();
            DrawCallAdvice(prompt);
            return;
        }

        var advice = plugin.LastAdvice;
        if (advice is null)
        {
            // A legal 3n+1 hand means it is not our decision point right now.
            int total = Tiles.Total(read.Table.Hand);
            string msg = total % 3 == 1
                ? "Waiting for your draw..."
                : "Reading hand...";
            ImGui.TextColored(Muted, msg);
            return;
        }

        DrawObservationBanner(read.Table);
        ImGui.Separator();



        var best = advice.Best;
        if (best is null)
        {
            ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(Muted, advice.Headline);
            ImGui.PopTextWrapPos();
            return;
        }

        var headlineColour = advice.Folding ? Warn : Good;
        ImGui.PushStyleColor(ImGuiCol.Text, headlineColour);
        ImGui.SetWindowFontScale(1.35f);
        ImGui.TextUnformatted(Tiles.Describe(best.Tile));
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.TextColored(Muted, ShapeLabel(best.ResultingShanten));

        DrawHandPosition(best.Tile);
        DrawHandStrip(best.Tile);

        var barred = read.Table.ForbiddenDiscards;
        if (barred.Count > 0)
        {
            ImGui.TextColored(Muted,
                "Barred after your call: " + string.Join(", ", barred.Select(Tiles.DescribeShort)));
        }

        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(advice.Headline);
        ImGui.PopTextWrapPos();

        if (plugin.Configuration.ShowAcceptedTiles && best.Ukeire > 0)
            DrawAcceptedTiles(read.Table, best);

        int alternatives = plugin.Configuration.AlternativesShown;
        if (alternatives > 0 && advice.Options.Count > 1)
        {
            ImGui.Spacing();
            ImGui.TextColored(Muted, "Alternatives");
            if (ImGui.BeginTable("##mjadv-alts", 4, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Tile", ImGuiTableColumnFlags.WidthFixed, 68);
                ImGui.TableSetupColumn("Shape", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Tiles", ImGuiTableColumnFlags.WidthFixed, 46);
                ImGui.TableSetupColumn("Danger");

                foreach (var option in advice.Options.Skip(1).Take(alternatives))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(Tiles.DescribeShort(option.Tile));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Muted, ShapeLabel(option.ResultingShanten));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(option.Ukeire.ToString());
                    ImGui.TableNextColumn();
                    DrawDanger(read.Table, option);
                }

                ImGui.EndTable();
            }
        }
    }

    private void DrawCallAdvice(CallAdvice call)
    {
        var best = call.Best!;
        var colour = best.Kind == CallKind.Pass ? Muted : Good;

        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextUnformatted(best.Kind.ToString().ToUpperInvariant());
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleColor();

        if (plugin.LastPrompt.HasClaimedTile)
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, $"on {Tiles.Describe(plugin.LastPrompt.ClaimedTile)}");
        }

        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(call.Headline);
        ImGui.PopTextWrapPos();

        if (best.Kind == CallKind.Riichi && plugin.LastAdvice?.Best is { } cut)
        {
            ImGui.TextColored(Good, $"Then discard {Tiles.Describe(cut.Tile)}");
            DrawHandPosition(cut.Tile);
        }

        if (best.UsedFromHand.Count > 0)
        {
            ImGui.TextColored(Good,
                "Use your " + string.Join(" + ", best.UsedFromHand.Select(Tiles.Describe)));
            ImGui.TextColored(Muted, "If the game offers a choice of shapes, pick that one.");
        }

        ImGui.Spacing();
        ImGui.TextColored(Muted, "Options");
        foreach (var option in call.Options.Take(4))
        {
            var tint = option.YakuAtRisk ? Bad : Muted;
            string shape = option.UsedFromHand.Count > 0
                ? "  (" + string.Join("+", option.UsedFromHand.Select(Tiles.DescribeShort)) + ")"
                : "";
            ImGui.TextColored(tint,
                $"{option.Kind,-7} {option.ResultingShanten}-shanten  {option.Ukeire} tiles{shape}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(option.Rationale);
        }
    }

    /// <summary>
    /// Draws the whole hand in order with the suggested tile marked, so there is
    /// no counting or tile-name matching to do.
    /// </summary>
    private void DrawHandStrip(int suggested)
    {
        var order = plugin.LastRead?.HandOrder;
        if (order is null || order.Count == 0)
            return;

        bool marked = false;
        for (int i = 0; i < order.Count; i++)
        {
            bool isPick = !marked && order[i] == suggested;
            if (isPick)
                marked = true;

            if (i > 0)
                ImGui.SameLine(0, 4);

            if (isPick)
                ImGui.TextColored(Good, $"[{Tiles.DescribeShort(order[i])}]");
            else
                ImGui.TextColored(Muted, Tiles.DescribeShort(order[i]));
        }

        int melds = plugin.LastRead?.Table?.CalledMelds ?? 0;
        if (melds > 0)
            ImGui.TextColored(Muted, $"(plus {melds} called meld{(melds > 1 ? "s" : "")}, shown separately in game)");
    }

    /// <summary>
    /// Names where the tile sits in the on-screen hand. Unambiguous even if the
    /// suit wording does not match the client's exactly.
    /// </summary>
    private void DrawHandPosition(int tile)
    {
        var order = plugin.LastRead?.HandOrder;
        if (order is null || order.Count == 0)
            return;

        var slots = new List<int>();
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] == tile)
                slots.Add(i + 1);
        }

        if (slots.Count == 0)
            return;

        string where = slots.Count == 1
            ? $"position {slots[0]} of {order.Count}, counting from the left"
            : $"positions {string.Join(" or ", slots)} of {order.Count}, from the left";

        ImGui.TextColored(Good, where);
    }

    private static void DrawStandings(TableState table)
    {
        if (table.Scores.Count != 4)
            return;

        int rank = PlacementAdjuster.RankOf(table.Scores);
        string place = PlacementAdjuster.Describe(rank);
        int gap = PlacementAdjuster.GapToSecond(table.Scores);

        var colour = rank switch
        {
            1 => Good,
            4 => Bad,
            _ => Muted,
        };

        string stance = rank switch
        {
            1 when gap >= 8000 => "big lead - playing safe",
            1 => "in front - playing tighter",
            4 => "last - chasing value",
            _ => "middle of the pack",
        };

        ImGui.TextColored(colour, $"{table.Scores[0]:N0}  ({place}, {stance})");
    }

    private void DrawObservationBanner(TableState table)
    {
        bool safetyReady = table.Observations.HasFlag(Observed.Discards);
        if (safetyReady)
        {
            var threats = new ThreatModel().Evaluate(table);
            string[] names = ["", "Right", "Across", "Left"];

            var parts = threats
                .Where(t => t.Riichi || t.TenpaiProbability >= 0.2)
                .Select(t => t.Riichi
                    ? $"{names[t.Seat]}: RIICHI"
                    : $"{names[t.Seat]}: {t.TenpaiProbability * 100:0}% ready")
                .ToArray();

            if (parts.Length == 0)
                ImGui.TextColored(Good, "No one looks close yet");
            else
                ImGui.TextColored(threats.Any(t => t.Riichi) ? Bad : Warn, string.Join("   ", parts));

            if (!table.Observations.HasFlag(Observed.OpponentMelds))
            {
                ImGui.TextColored(Muted,
                    "Called melds not read - readiness may be underestimated. /mjadv melds to diagnose.");
            }

            // A terminal-hungry pond turns the usual safe tiles into the worst ones.
            for (int seat = 1; seat < table.Seats.Length; seat++)
            {
                var read = DiscardReader.Read(table.Seats[seat].Discards);
                string note = DiscardReader.Describe(read);
                if (note.Length > 0)
                    ImGui.TextColored(Bad, $"{names[seat]}: {note}");
            }

            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Warn);
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(
            "Efficiency only - no danger scoring yet. The discard piles are found automatically "
            + "once a few tiles are down.");
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private static void DrawAcceptedTiles(TableState table, DiscardOption best)
    {
        var work = (int[])table.Hand.Clone();
        work[best.Tile]--;
        var result = UkeireCalculator.Calculate(work, table.BuildVisible(), table.CalledMelds);
        if (result.Accepted.Count == 0)
            return;

        string label = best.ResultingShanten == 0 ? "Waiting on" : "Accepts";
        string list = string.Join(",  ", result.Accepted.Select(a => $"{Tiles.DescribeShort(a.Tile)} x{a.Live}"));

        ImGui.Spacing();
        ImGui.TextColored(Muted, $"{label} ({result.Width})");
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(list);
        ImGui.PopTextWrapPos();
    }

    private static void DrawDanger(TableState table, DiscardOption option)
    {
        // Danger is usable as soon as the ponds are readable. SafetyReady also
        // requires meld reading, but withholding the number until then hid a
        // real riichi risk behind a dash.
        if (!table.Observations.HasFlag(Observed.Discards)
            || option.Risk.Reason == SafetyReason.Unknown)
        {
            ImGui.TextColored(Muted, "-");
            return;
        }

        double rate = option.Risk.DealInRate;
        var colour = rate switch
        {
            <= 0.001 => Good,
            < 0.06 => Warn,
            _ => Bad,
        };

        ImGui.TextColored(colour, rate <= 0.001 ? "safe" : $"{rate * 100:0.#}%");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(option.Risk.Explain());
    }

    private static string ShapeLabel(int shanten) => shanten switch
    {
        < 0 => "win",
        0 => "tenpai",
        1 => "1-shanten",
        _ => $"{shanten}-shanten",
    };
}
