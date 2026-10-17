using Mahjong.Advisor;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using MahjongAdvisor.Logging;
using MahjongAdvisor.Reading;

namespace MahjongAdvisor.UI;

public sealed class AdvisorWindow : Window
{
    private readonly Plugin plugin;
    private bool positionResetPending;

    public AdvisorWindow(Plugin plugin)
        : base("Mahjong Advisor##mjadv-main")
    {
        ArgumentNullException.ThrowIfNull(plugin);
        this.plugin = plugin;
        Size = new Vector2(380, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(310, 190),
            MaximumSize = new Vector2(720, 900),
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
            Position = new Vector2(70, 70);
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
            DrawNoTable(read);
            return;
        }

        DrawHeader(read);

        if (plugin.LastCallAdvice is { Best: not null } call)
        {
            DrawCallAdvice(read, call);
            DrawFooter(read);
            return;
        }

        var advice = plugin.LastAdvice;
        if (advice is null)
        {
            DrawWaiting(read);
            DrawFooter(read);
            return;
        }

        DrawDiscardAdvice(read, advice);
        DrawFooter(read);
    }

    private void DrawHeader(BoardRead read)
    {
        bool auto = plugin.Configuration.AutoPlay;

        ImGui.TextColored(auto ? UiTheme.Warn : UiTheme.Muted, auto ? "AUTO-PLAY" : "MANUAL");
        ImGui.SameLine();
        ImGui.TextColored(UiTheme.Muted, "| " + StateLabel(read));

        ImGui.SameLine();
        if (auto)
        {
            if (ImGui.Button("Stop##overlay-auto"))
                plugin.SetAutoPlay("off");
        }
        else if (ImGui.Button("Enable auto##overlay-auto"))
        {
            plugin.SetAutoPlay("on");
        }

        if (auto)
        {
            ImGui.SameLine();
            ImGui.TextColored(UiTheme.Muted, plugin.AutoPlay.LastStatus);
        }

        ImGui.Separator();
    }

    private void DrawDiscardAdvice(BoardRead read, Advice advice)
    {
        var best = advice.Best;
        if (best is null)
        {
            UiTheme.Wrapped(advice.Headline);
            return;
        }

        ImGui.TextColored(UiTheme.Muted, advice.Folding ? "BEST DEFENCE" : "BEST DISCARD");

        var colour = advice.Folding ? UiTheme.Warn : UiTheme.Good;
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.SetWindowFontScale(1.55f);
        ImGui.TextUnformatted(Tiles.Describe(best.Tile));
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.TextColored(UiTheme.Muted, UiTheme.ShapeLabel(best.ResultingShanten));

        DrawHandStrip(read, best.Tile);
        DrawHandPosition(read, best.Tile);

        ImGui.Spacing();
        UiTheme.Wrapped(advice.Headline);

        var table = read.Table!;
        var barred = table.ForbiddenDiscards;
        if (barred.Count > 0)
        {
            ImGui.TextColored(UiTheme.Muted,
                "Barred after your call: " + string.Join(", ", barred.Select(Tiles.DescribeShort)));
        }

        if (plugin.Configuration.ShowAcceptedTiles && best.Ukeire > 0)
            DrawAcceptedTiles(table, best);

        DrawAlternatives(table, advice);
    }

    private void DrawCallAdvice(BoardRead read, CallAdvice call)
    {
        var best = call.Best!;
        var colour = best.Kind switch
        {
            CallKind.Pass => UiTheme.Muted,
            CallKind.Ron or CallKind.Tsumo => UiTheme.Good,
            _ => UiTheme.Warn,
        };

        ImGui.TextColored(UiTheme.Muted, "CALL DECISION");

        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.SetWindowFontScale(1.45f);
        ImGui.TextUnformatted(best.Kind.ToString().ToUpperInvariant());
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleColor();

        if (plugin.LastPrompt.HasClaimedTile)
        {
            ImGui.SameLine();
            ImGui.TextColored(UiTheme.Muted, $"on {Tiles.Describe(plugin.LastPrompt.ClaimedTile)}");
        }

        ImGui.Spacing();
        UiTheme.Wrapped(call.Headline);

        if (best.Kind == CallKind.Riichi && plugin.LastAdvice?.Best is { } cut)
        {
            ImGui.TextColored(UiTheme.Good, $"Then discard {Tiles.Describe(cut.Tile)}");
            DrawHandPosition(read, cut.Tile);
        }

        if (best.UsedFromHand.Count > 0)
        {
            ImGui.TextColored(UiTheme.Good,
                "Use your " + string.Join(" + ", best.UsedFromHand.Select(Tiles.Describe)));
            ImGui.TextColored(UiTheme.Muted, "If the game offers a choice of shapes, pick that one.");
        }

        ImGui.Spacing();
        ImGui.TextColored(UiTheme.Muted, "Options");
        if (ImGui.BeginTable("##mj-call-options", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Call", ImGuiTableColumnFlags.WidthFixed, 64);
            ImGui.TableSetupColumn("Shape", ImGuiTableColumnFlags.WidthFixed, 68);
            ImGui.TableSetupColumn("Tiles", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Yaku");
            ImGui.TableHeadersRow();

            foreach (var option in call.Options.Take(5))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(option.YakuAtRisk ? UiTheme.Bad : UiTheme.Text,
                    option.Kind.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(UiTheme.Muted, UiTheme.ShapeLabel(option.ResultingShanten));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(option.Ukeire.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(option.YakuAtRisk ? UiTheme.Bad : UiTheme.Good,
                    option.YakuAtRisk ? "at risk" : "ok");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(option.Rationale);
            }

            ImGui.EndTable();
        }

        if (plugin.LastPrompt.Options.Count > 0 && ImGui.CollapsingHeader("Popup buttons"))
        {
            foreach (var option in plugin.LastPrompt.Options)
                ImGui.TextColored(UiTheme.Muted, $"[11,{option.Option}]  {option.Label}");
        }
    }

    /// <summary>
    /// Draws the hand in display order with the recommended tile marked. The
    /// display order is what the client's discard callback uses, so it is also
    /// the safest thing to show the player.
    /// </summary>
    private static void DrawHandStrip(BoardRead read, int suggested)
    {
        if (read.HandSlotsValid && read.HandSlots.Count > 0)
        {
            ImGui.TextColored(UiTheme.Muted, "Your hand (left to right)");
            if (ImGui.BeginTable("##mj-hand", 7, ImGuiTableFlags.SizingFixedFit))
            {
                for (int i = 0; i < 7; i++)
                    ImGui.TableSetupColumn("##tile", ImGuiTableColumnFlags.WidthFixed, 46);

                bool marked = false;
                foreach (var slot in read.HandSlots)
                {
                    if (!slot.IsValid)
                        continue;

                    ImGui.TableNextColumn();
                    bool isPick = !marked && slot.Tile == suggested;
                    if (isPick)
                        marked = true;

                    var colour = isPick ? UiTheme.Good : slot.IsRed ? UiTheme.Red : UiTheme.Muted;
                    string label = Tiles.DescribeShort(slot.Tile)
                        + (slot.IsRed ? "*" : "");
                    if (isPick)
                        label = $"[{label}]";

                    ImGui.TextColored(colour, label);
                    if (ImGui.IsItemHovered())
                    {
                        string red = slot.IsRed ? ", red five" : "";
                        ImGui.SetTooltip($"{Tiles.Describe(slot.Tile)} - slot {slot.Slot + 1}{red}");
                    }
                }

                ImGui.EndTable();
            }

            int melds = read.Table?.CalledMelds ?? 0;
            if (melds > 0)
            {
                ImGui.TextColored(UiTheme.Muted,
                    $"(plus {melds} called meld{(melds > 1 ? "s" : "")}, shown separately in game)");
            }

            return;
        }

        var order = read.HandOrder;
        if (order.Count == 0)
            return;

        ImGui.TextColored(UiTheme.Muted, "Your hand");
        bool fallbackMarked = false;
        var parts = new List<string>(order.Count);
        foreach (int tile in order)
        {
            bool isPick = !fallbackMarked && tile == suggested;
            if (isPick)
                fallbackMarked = true;
            parts.Add(isPick ? $"[{Tiles.DescribeShort(tile)}]" : Tiles.DescribeShort(tile));
        }

        UiTheme.Wrapped(string.Join("  ", parts));
    }

    private static void DrawHandPosition(BoardRead read, int tile)
    {
        if (read.HandSlotsValid && read.HandSlots.Count > 0)
        {
            var slots = read.HandSlots
                .Where(s => s.IsValid && s.Tile == tile)
                .Select(s => s.Slot + 1)
                .ToList();

            if (slots.Count == 0)
                return;

            string where = slots.Count == 1
                ? $"Slot {slots[0]} of 14, counting from the left"
                : $"Slots {string.Join(" or ", slots)} of 14, counting from the left";
            ImGui.TextColored(UiTheme.Good, where);
            return;
        }

        var order = read.HandOrder;
        if (order.Count == 0)
            return;

        var fallback = new List<int>();
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] == tile)
                fallback.Add(i + 1);
        }

        if (fallback.Count == 0)
            return;

        string fallbackWhere = fallback.Count == 1
            ? $"Position {fallback[0]} of {order.Count}, counting from the left"
            : $"Positions {string.Join(" or ", fallback)} of {order.Count}, from the left";
        ImGui.TextColored(UiTheme.Good, fallbackWhere);
    }

    private static void DrawAcceptedTiles(TableState table, DiscardOption best)
    {
        var work = (int[])table.Hand.Clone();
        work[best.Tile]--;
        var result = UkeireCalculator.Calculate(work, table.BuildVisible(), table.CalledMelds);
        if (result.Accepted.Count == 0)
            return;

        string label = best.ResultingShanten == 0 ? "Waiting on" : "Accepts";
        string list = string.Join(",  ",
            result.Accepted.Select(a => $"{Tiles.DescribeShort(a.Tile)} x{a.Live}"));

        ImGui.Spacing();
        ImGui.TextColored(UiTheme.Muted, $"{label} ({result.Width})");
        UiTheme.Wrapped(list);
    }

    private static void DrawAlternatives(TableState table, Advice advice)
    {
        int alternatives = Math.Clamp(advice.Options.Count - 1, 0, 5);
        if (alternatives <= 0)
            return;

        ImGui.Spacing();
        ImGui.TextColored(UiTheme.Muted, "Alternatives");
        if (!ImGui.BeginTable("##mj-alternatives", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("Tile", ImGuiTableColumnFlags.WidthFixed, 68);
        ImGui.TableSetupColumn("Shape", ImGuiTableColumnFlags.WidthFixed, 68);
        ImGui.TableSetupColumn("Tiles", ImGuiTableColumnFlags.WidthFixed, 46);
        ImGui.TableSetupColumn("Danger");
        ImGui.TableHeadersRow();

        foreach (var option in advice.Options.Skip(1).Take(alternatives))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Tiles.DescribeShort(option.Tile));
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.Muted, UiTheme.ShapeLabel(option.ResultingShanten));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(option.Ukeire.ToString());
            ImGui.TableNextColumn();
            DrawDanger(table, option);
        }

        ImGui.EndTable();
    }

    private static void DrawDanger(TableState table, DiscardOption option)
    {
        if (!table.Observations.HasFlag(Observed.Discards)
            || option.Risk.Reason == SafetyReason.Unknown)
        {
            ImGui.TextColored(UiTheme.Muted, "-");
            return;
        }

        double rate = option.Risk.DealInRate;
        var colour = rate switch
        {
            <= 0.001 => UiTheme.Good,
            < 0.06 => UiTheme.Warn,
            _ => UiTheme.Bad,
        };

        ImGui.TextColored(colour, rate <= 0.001 ? "safe" : $"{rate * 100:0.#}%");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(option.Risk.Explain());
    }

    private void DrawFooter(BoardRead read)
    {
        ImGui.Separator();

        bool auto = plugin.Configuration.AutoPlay;
        ImGui.TextColored(auto ? UiTheme.Warn : UiTheme.Muted,
            auto
                ? $"AUTO-PLAY ON - {plugin.AutoPlay.Stats.LastAction}"
                : "Manual play - suggestions only");

        if (auto)
            UiTheme.WrappedColored("Casual tables only. The Stop button or /mjadv stop returns to manual play.", UiTheme.Muted);

        var table = read.Table!;
        if (!table.Observations.HasFlag(Observed.OpponentMelds))
            ImGui.TextColored(UiTheme.Muted, "Meld count not read - readiness may be underestimated.");
        if (!read.HandSlotsValid)
            ImGui.TextColored(UiTheme.Muted, "Hand slot order not read - auto-play will not discard until it matches.");

        ImGui.TextColored(UiTheme.Muted, $"Build {GameLogger.BuildStamp}");
    }

    private void DrawNoTable(BoardRead? read)
    {
        ImGui.TextColored(UiTheme.Accent, "MAHJONG ADVISOR");
        ImGui.Spacing();
        UiTheme.Wrapped(read?.Status ?? "Waiting for a Doman Mahjong table.");
        ImGui.Spacing();
        if (ImGui.Button("Open control panel"))
            plugin.OpenSettings();
    }

    private void DrawWaiting(BoardRead read)
    {
        int total = Tiles.Total(read.Table!.Hand);
        string label = StateLabel(read);
        string message = label switch
        {
            "result screen" => "Result screen - the game will continue automatically.",
            "result" => "Hand result - waiting for the next hand.",
            "end of hand" => "End of hand - waiting for the result.",
            "waiting for other players" => "Waiting for other players...",
            "call prompt" => "Call prompt...",
            _ when total % 3 == 1 => "Waiting for your draw...",
            _ => "Reading the board...",
        };

        ImGui.TextColored(UiTheme.Muted, message);
        ImGui.Spacing();
        DrawHandStrip(read, -1);
    }

    private string StateLabel(BoardRead read)
    {
        var codes = read.Profile?.StateCodes;
        if (codes is null)
            return $"state {read.StateCode}";

        int state = read.StateCode;
        if (state == codes.RiichiDiscard)
            return "riichi discard";
        if (state == codes.BetweenHands)
            return "result screen";
        if (state == codes.HandResult)
            return "result";
        if (state == codes.EndOfHand)
            return "end of hand";
        if (state == codes.CallPrompt)
        {
            return plugin.LastCallAdvice?.Best is not null
                ? "call prompt"
                : "waiting for other players";
        }

        if (state == codes.ChiVariantSelect)
            return "choose chi shape";
        if (state == codes.SelfDeclareList
            || state == codes.OurTurnDiscard
            || state == codes.CallPromptList)
        {
            return "your turn";
        }

        return $"state {state}";
    }
}
