using Mahjong.Advisor;
using MahjongAdvisor.Reading;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace MahjongAdvisor.Automation;

public enum AutoActionKind
{
    Discard,
    Call,
    Riichi,
    Win,
    Pass,
    Advance,
    Kan,
}

public sealed class AutoPlayStats
{
    public int Discards { get; private set; }
    public int Calls { get; private set; }
    public int Riichi { get; private set; }
    public int Wins { get; private set; }
    public int Passes { get; private set; }
    public int Advances { get; private set; }
    public int Kans { get; private set; }

    public string LastAction { get; private set; } = "idle";

    public void Reset()
    {
        Discards = Calls = Riichi = Wins = Passes = Advances = Kans = 0;
        LastAction = "idle";
    }

    public void Record(AutoActionKind kind, string label)
    {
        switch (kind)
        {
            case AutoActionKind.Discard: Discards++; break;
            case AutoActionKind.Call: Calls++; break;
            case AutoActionKind.Riichi: Riichi++; break;
            case AutoActionKind.Win: Wins++; break;
            case AutoActionKind.Pass: Passes++; break;
            case AutoActionKind.Advance: Advances++; break;
            case AutoActionKind.Kan: Kans++; break;
        }

        LastAction = label;
    }
}

/// <summary>
/// Opt-in autonomous play. Replays the addon's own FireCallback protocol:
///
///   discard : [15, iconId] then [7, slot]
///   popup   : [11, option]
///   advance : [14] at the result modal, [17] at the end-of-hand transition
///
/// All of the timing and state guards here come from the reference solver's live
/// runs: settle after the table appears, never advance state 2 (the game does it
/// itself), retry swallowed popup fires, and wait for the full hand before acting.
/// Manual mode is simply this controller not running.
/// </summary>
public sealed unsafe class AutoPlayController
{
    private const long TickIntervalMs = 120;
    private const int MatchStartGraceMs = 6000;
    private const int RiichiFollowupDelayMs = 450;
    private const int PopupRetryDelayMs = 700;
    private const int MaxPopupAttempts = 4;
    private const int StuckWarnMs = 12000;
    private const int ChiPickDelayMs = 600;

    private readonly Plugin plugin;
    private readonly Random rng = new();

    private bool wasEnabled;
    private bool addonAbsent = true;
    private long graceUntil;
    private long nextTickAllowed;
    private int prevState = int.MinValue;
    private string lastTurnKey = "";
    private string lastPromptKey = "";
    private long stateSince;
    private long actAt;
    private bool acted;
    private bool riichiFired;
    private bool riichiDiscardPending;
    private int riichiDiscardTile = -1;
    private int popupAttempts;
    private bool stuckWarned;
    private bool chiPickPending;
    private int chiPickAttempts;
    private long chiPickAt;
    private bool chiPickerDumped;

    public AutoPlayController(Plugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        this.plugin = plugin;
    }

    public AutoPlayStats Stats { get; } = new();

    public string LastStatus { get; private set; } = "off";

    /// <summary>Called once per board read, after advice has been recomputed.</summary>
    public void Tick(BoardRead read, CallPrompt prompt, Advice? advice, CallAdvice? callAdvice)
    {
        var config = plugin.Configuration;

        if (!config.AutoPlay)
        {
            if (wasEnabled)
            {
                wasEnabled = false;
                ResetSession(resetStats: false);
                LastStatus = "off";
                Log("auto-play off (session stats preserved for the diagnostic bundle)");
            }

            return;
        }

        if (!wasEnabled)
        {
            wasEnabled = true;
            ResetSession();
            LastStatus = "enabled";
            Log("auto-play ENABLED (casual tables only)");
        }

        long now = Environment.TickCount64;
        if (now < nextTickAllowed)
            return;
        nextTickAllowed = now + TickIntervalMs;

        if (read.Profile is null)
        {
            if (!addonAbsent)
            {
                addonAbsent = true;
                prevState = int.MinValue;
                acted = false;
                riichiFired = false;
            }

            LastStatus = "waiting for a table";
            return;
        }

        if (addonAbsent)
        {
            addonAbsent = false;
            graceUntil = now + MatchStartGraceMs;
            prevState = int.MinValue;
            acted = false;
            riichiFired = false;
            LastStatus = "settling";
            Log($"table detected (state {read.StateCode}); settling for {MatchStartGraceMs / 1000}s");
        }

        if (now < graceUntil)
            return;

        var profile = read.Profile;
        int state = read.StateCode;

        // The chi shape picker: after firing chi, the client may ask which
        // variant to take. Pick the first option; the controller re-evaluates
        // the hand afterwards, so any variant keeps the game moving.
        if (state == profile.StateCodes.ChiVariantSelect)
        {
            HandleChiPicker(profile, now);
            return;
        }

        // 6 and 30 are the same "our turn"; our own highlight moves 6 -> 30.
        bool sameTurn = state is 6 or 30 && prevState is 6 or 30;

        // Also re-arm when the hand changes while the state code stays the
        // same (fast draws can skip the intermediate surface). The key excludes
        // the prompt so 6 -> 30 and a Riichi option appearing do not reset an
        // in-flight declaration.
        string turnKey = BuildTurnKey(read, state);
        if ((state != prevState || turnKey != lastTurnKey) && !sameTurn)
        {
            lastTurnKey = turnKey;
            acted = false;
            riichiFired = false;
            riichiDiscardPending = false;
            riichiDiscardTile = -1;
            popupAttempts = 0;
            chiPickAttempts = 0;
            actAt = now + DelayFor(state, profile, config);
            stateSince = now;
            stuckWarned = false;
        }

        string promptKey = string.Join(",",
            prompt.Options.Select(o => $"{o.Option}:{o.Label}"));
        if (promptKey != lastPromptKey)
        {
            lastPromptKey = promptKey;
            popupAttempts = 0;
        }

        prevState = state;

        if (!stuckWarned && IsActionable(state, profile) && now - stateSince > StuckWarnMs)
        {
            stuckWarned = true;
            Log($"state {state} unresolved for {(now - stateSince) / 1000}s "
                + $"(acted={acted}, attempts={popupAttempts})");
        }

        if (acted || now < actAt)
            return;

        if (state == profile.StateCodes.HandResult || state == profile.StateCodes.EndOfHand)
        {
            if (config.AutoPlayAdvanceResults)
                Advance(profile, state);
            return;
        }

        // State 27 is the result/score screen between hands. The game advances
        // it itself; do not guess an opcode. Keep the overlay honest while it
        // is up instead of showing stale discard advice.
        if (state == profile.StateCodes.BetweenHands)
        {
            LastStatus = "result screen (game continues)";
            return;
        }

        // Result screens are the one action that does not need a readable hand;
        // everything below is a decision that does.
        if (read.Table is null)
        {
            LastStatus = "waiting for a readable hand";
            return;
        }

        if (state == profile.StateCodes.CallPrompt)
        {
            // 15 is also the generic "other players are acting" state. Only a
            // readable option set is actually a call prompt for us.
            if (prompt.Available.Count == 0)
            {
                LastStatus = "waiting for other players";
                return;
            }

            DoCall(read, prompt, callAdvice, profile);
            return;
        }

        if (state == profile.StateCodes.OurTurnDiscard
            || state == profile.StateCodes.SelfDeclareList
            || state == profile.StateCodes.CallPromptList
            || state == profile.StateCodes.RiichiDiscard)
        {
            DoTurn(
                read,
                prompt,
                advice,
                callAdvice,
                profile,
                afterCall: state == profile.StateCodes.CallPromptList);
        }
    }

    /// <summary>
    /// Handles the chi shape-selection surface. The controller fires the first
    /// option the client offers; any chi variant completes the call, and the
    /// discard advisor re-evaluates the modified hand afterwards. The picker's
    /// raw layout is dumped once per session so the shape choice can be refined
    /// against real captures later.
    /// </summary>
    private void HandleChiPicker(LayoutProfile profile, long now)
    {
        if (!chiPickerDumped)
        {
            chiPickerDumped = true;
            DumpChiPicker();
        }

        if (!chiPickPending)
        {
            chiPickPending = true;
            chiPickAttempts = 0;
            chiPickAt = now + ChiPickDelayMs;
            LastStatus = "chi shape picker";
            return;
        }

        if (now < chiPickAt)
        {
            LastStatus = "chi shape picker (waiting)";
            return;
        }

        if (chiPickAttempts >= MaxPopupAttempts)
        {
            acted = true;
            LastStatus = "chi picker stalled";
            Log("chi picker: no acceptance after "
                + $"{chiPickAttempts} attempts; yielding to the game timer");
            return;
        }

        if (FireCall(0))
        {
            chiPickAttempts++;
            if (chiPickAttempts == 1)
                RecordAction(AutoActionKind.Call, "picked chi shape");

            LastStatus = "chi shape picked";
            chiPickAt = Environment.TickCount64 + PopupRetryDelayMs;
        }
    }

    /// <summary>Writes the chi picker's AtkValues and tile layout for offline mapping.</summary>
    private void DumpChiPicker()
    {
        try
        {
            var unit = plugin.Reader.TryGetAddon(out var profile);
            if (unit is null || profile is null)
                return;

            string dir = Path.Combine(
                Plugin.PluginInterface.GetPluginConfigDirectory(), "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"chi-picker-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var lines = new List<string> { $"state={ReadState(unit, profile)}" };

            if (unit->AtkValues is not null)
            {
                for (int i = 0; i < unit->AtkValuesCount; i++)
                {
                    var value = unit->AtkValues[i];
                    string extra = "";
                    if (value.Type is ValueType.String or ValueType.ConstString or ValueType.ManagedString
                        && value.String.Value is not null)
                    {
                        extra = $" '{value.String.ToString()}'";
                    }

                    lines.Add($"atk[{i}] {value.Type}={value.Int}{extra}");
                }
            }

            lines.Add("-- tiles (name, x, y, rotation, ancestry) --");
            plugin.NodeInspector.BeginRead(unit, profile.TileTextureBase);
            lines.AddRange(plugin.NodeInspector.DumpGeometry(unit, profile.TileTextureBase));

            File.WriteAllLines(path, lines);
            Log($"chi picker dump: {path}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[MahjongAdvisor][auto] chi picker dump failed");
        }
    }

    private void DoTurn(
        BoardRead read,
        CallPrompt prompt,
        Advice? advice,
        CallAdvice? callAdvice,
        LayoutProfile profile,
        bool afterCall)
    {
        var table = read.Table!;

        // If we called chi and the client completed it without a picker, the
        // pending pick is moot; the discard below is a normal turn again.
        chiPickPending = false;

        int expected = 14 - (table.CalledMelds * 3);
        if (Tiles.Total(table.Hand) != expected)
        {
            LastStatus = "waiting for a full hand";
            return;
        }

        var config = plugin.Configuration;

        // A win is never missed because of a state-code quirk. This must run
        // before the riichi lock: a drawn winning tile is still a tsumo.
        if (!afterCall
            && callAdvice?.Best is { Kind: CallKind.Ron or CallKind.Tsumo } win)
        {
            var winOptions = OptionsFor(read, prompt);
            int winOption = CallPromptReader.FindOption(
                winOptions, win.Kind, profile.CallLabels);
            if (winOption >= 0)
            {
                FirePopup(winOption, win.Kind.ToString(), AutoActionKind.Win);
                return;
            }
        }

        // A complete hand has a tsumo panel; some panels carry no labels, in
        // which case the affirmative button is option 0 (verified live).
        // An OPEN complete hand can still be yaku-less, in which case the game
        // correctly shows no tsumo prompt. Firing the unlabelled option 0 there
        // was the second live freeze: never treat "complete" as "can win".
        bool complete = !afterCall
            && Shanten.Calculate(table.Hand, table.CalledMelds) < 0;
        bool canWin = complete && HasWinningYaku(table);

        if (complete && !canWin)
            Log("turn: complete hand has no yaku; continuing to discard instead of tsumo.");

        if (canWin)
        {
            var options = OptionsFor(read, prompt);
            int tsumo = CallPromptReader.FindOption(options, CallKind.Tsumo, profile.CallLabels);

            // Only an unlabelled panel falls back to option 0. If the popup has
            // labels but no Tsumo, this complete shape is not a legal win (no
            // yaku), and pressing the first button would be a different action.
            if (tsumo < 0 && options.Count == 0)
                tsumo = 0;

            if (tsumo >= 0)
            {
                FirePopup(tsumo, "tsumo", AutoActionKind.Win);
                return;
            }
        }

        // Nine terminals / honours abort prompt. Keep a real kokushi attempt;
        // otherwise take the redeal.
        if (!afterCall && TryResolveNineTerminals(read, prompt, profile))
            return;

        // If riichi was just declared, the next action is the tenpai-keeping
        // discard chosen at declaration time. Do not use the generic advice
        // here: it can prefer a safer tile that breaks tenpai, which the client
        // will refuse and the bot will stall.
        if (riichiDiscardPending)
        {
            // Wait until the declaration prompt has actually closed. Firing the
            // discard while Riichi/Pass is still on screen can be swallowed or
            // treated as another prompt choice.
            if (prompt.Available.Contains(CallKind.Riichi))
            {
                actAt = Environment.TickCount64 + 200;
                return;
            }

            bool discarded = riichiDiscardTile >= 0
                && TryDiscardTile(riichiDiscardTile, read);
            riichiDiscardPending = false;
            riichiDiscardTile = -1;
            if (!discarded)
                acted = true; // do not spin on a failed riichi cut
            return;
        }

        // Riichi lock: the drawn tile is the only legal move. The win checks
        // above already had their chance.
        if (table.SelfRiichi && !table.RiichiDeclarationPending)
        {
            if (!DiscardDrawn(read))
                acted = true; // no legal/readable draw; let the player take over
            return;
        }

        // Declare riichi first; the chosen discard is cut on the next eligible
        // tick once the prompt has closed.
        if (!afterCall
            && !riichiFired
            && callAdvice?.Best?.Kind == CallKind.Riichi
            && prompt.Available.Contains(CallKind.Riichi))
        {
            var options = OptionsFor(read, prompt);
            int riichi = CallPromptReader.FindOption(options, CallKind.Riichi, profile.CallLabels);
            int riichiDiscard = BestTenpaiDiscard(advice);
            if (riichi >= 0 && riichiDiscard >= 0)
            {
                if (FireCall(riichi))
                {
                    riichiFired = true;
                    riichiDiscardPending = true;
                    riichiDiscardTile = riichiDiscard;
                    RecordAction(AutoActionKind.Riichi, "declared riichi");
                    LastStatus = "riichi declared";
                    actAt = Environment.TickCount64 + RiichiFollowupDelayMs;
                    Log($"riichi -> [11,{riichi}]; cutting {Tiles.Name(riichiDiscard)} next tick");
                }

                return;
            }

            if (riichi < 0)
                Log("riichi: option not readable; declining instead of stalling.");
            else
                Log("riichi: no tenpai-keeping discard found; declining instead of stalling.");
            // No readable Riichi option: fall through and discard. That simply
            // declines riichi instead of stalling the turn.
        }

        // If the advisor prefers Pass on a Riichi/Pass prompt, answer it. A
        // prompt left open blocks the discard surface and looks like a stall.
        if (!afterCall
            && (callAdvice is null || callAdvice.Best?.Kind == CallKind.Pass)
            && prompt.Available.Contains(CallKind.Riichi))
        {
            var options = OptionsFor(read, prompt);
            int pass = CallPromptReader.FindOption(options, CallKind.Pass, profile.CallLabels);
            if (pass >= 0)
            {
                FirePopup(pass, "declined riichi", AutoActionKind.Pass);
                return;
            }
        }

        // A self-kan is optional. Take it only when explicitly enabled, and
        // otherwise pass the prompt so it cannot block the discard surface.
        if (!afterCall && callAdvice?.Best?.Kind == CallKind.Kan)
        {
            var options = OptionsFor(read, prompt);
            int kan = CallPromptReader.FindOption(options, CallKind.Kan, profile.CallLabels);
            if (config.AutoPlayAllowKan && kan >= 0)
            {
                FirePopup(kan, "kan", AutoActionKind.Kan);
                return;
            }

            int pass = CallPromptReader.FindOption(options, CallKind.Pass, profile.CallLabels);
            if (pass >= 0)
            {
                FirePopup(pass, "declined kan", AutoActionKind.Pass);
                return;
            }
        }

        // A self-kan prompt where the advisor already prefers Pass still has to
        // be answered, or the discard surface can stay blocked.
        if (!afterCall
            && callAdvice?.Best?.Kind != CallKind.Kan
            && prompt.Available.Contains(CallKind.Kan)
            && !prompt.Available.Contains(CallKind.Riichi))
        {
            var options = OptionsFor(read, prompt);
            int pass = CallPromptReader.FindOption(options, CallKind.Pass, profile.CallLabels);
            if (pass >= 0)
            {
                FirePopup(pass, "declined kan", AutoActionKind.Pass);
                return;
            }
        }

        if (advice?.Best is null)
        {
            LastStatus = "no discard advice";
            return;
        }

        if (!TryDiscardTile(advice.Best.Tile, read))
            acted = true; // failed read/slot: wait for the next turn instead of spinning
    }

    private void DoCall(
        BoardRead read,
        CallPrompt prompt,
        CallAdvice? callAdvice,
        LayoutProfile profile)
    {
        var options = OptionsFor(read, prompt);
        if (options.Count == 0)
        {
            LastStatus = "call popup not readable";
            return;
        }

        // A win is never passed, even if the advisor was not computed.
        int ron = CallPromptReader.FindOption(options, CallKind.Ron, profile.CallLabels);
        if (ron >= 0)
        {
            FirePopup(ron, "ron", AutoActionKind.Win);
            return;
        }

        int tsumo = CallPromptReader.FindOption(options, CallKind.Tsumo, profile.CallLabels);
        if (tsumo >= 0)
        {
            FirePopup(tsumo, "tsumo", AutoActionKind.Win);
            return;
        }

        if (TryResolveNineTerminals(read, prompt, profile))
            return;

        var config = plugin.Configuration;
        CallKind kind = callAdvice?.Best?.Kind ?? CallKind.Pass;
        string passLabel = "pass";

        if (kind == CallKind.Pon && !config.AutoPlayAllowPon)
            kind = CallKind.Pass;
        if (kind == CallKind.Chi && !config.AutoPlayAllowChi)
            kind = CallKind.Pass;
        if (kind == CallKind.Chi)
        {
            // The shape-selection dialog (state 25) ignores [11,n] call picks —
            // the picker dump confirms it is its own multi-variant surface. A
            // single-shape chi completes straight through with no picker, so
            // only fire those; multi-shape chi passes instead of stalling.
            int chiShapes = callAdvice?.Options.Count(o => o.Kind == CallKind.Chi) ?? 0;
            if (chiShapes != 1)
            {
                passLabel = $"pass ({chiShapes} chi shapes)";
                Log($"chi: {chiShapes} shapes would open the picker; passing");
                kind = CallKind.Pass;
            }
        }
        if (kind == CallKind.Kan && !config.AutoPlayAllowKan)
            kind = CallKind.Pass;

        int option = kind switch
        {
            CallKind.Ron => CallPromptReader.FindOption(options, CallKind.Ron, profile.CallLabels),
            CallKind.Tsumo => CallPromptReader.FindOption(options, CallKind.Tsumo, profile.CallLabels),
            CallKind.Pon => CallPromptReader.FindOption(options, CallKind.Pon, profile.CallLabels),
            CallKind.Chi => CallPromptReader.FindOption(options, CallKind.Chi, profile.CallLabels),
            CallKind.Kan => CallPromptReader.FindOption(options, CallKind.Kan, profile.CallLabels),
            _ => CallPromptReader.FindOption(options, CallKind.Pass, profile.CallLabels),
        };

        // If the advisor wanted something the popup does not offer, decline.
        if (option < 0)
            option = CallPromptReader.FindOption(options, CallKind.Pass, profile.CallLabels);

        if (option < 0)
        {
            LastStatus = "no usable call option";
            return;
        }

        AutoActionKind action = kind switch
        {
            CallKind.Ron or CallKind.Tsumo => AutoActionKind.Win,
            CallKind.Riichi => AutoActionKind.Riichi,
            CallKind.Kan => AutoActionKind.Kan,
            CallKind.Pon or CallKind.Chi => AutoActionKind.Call,
            _ => AutoActionKind.Pass,
        };

        bool fired = FirePopup(option, kind == CallKind.Pass ? passLabel : kind.ToString(), action);
        if (fired && kind == CallKind.Chi)
        {
            // The client may show the shape-selection surface next; the picker
            // handler takes over from there.
            chiPickPending = true;
            chiPickAt = Environment.TickCount64 + ChiPickDelayMs;
        }
    }

    /// <summary>
    /// Answer the nine-terminals/honours prompt. A strong kokushi start
    /// (thirteen-orphans shanten 2 or better) is worth keeping; anything else is
    /// a 4-5 shanten hand and should take the redeal.
    /// </summary>
    private bool TryResolveNineTerminals(
        BoardRead read, CallPrompt prompt, LayoutProfile profile)
    {
        var options = OptionsFor(read, prompt);
        int nine = CallPromptReader.FindOptionContaining(
            options, "nine", "kyuushu", "kokushi");
        if (nine < 0)
            return false;

        int pass = CallPromptReader.FindOption(options, CallKind.Pass, profile.CallLabels);
        int shanten = Shanten.ThirteenOrphans(read.Table!.Hand);
        bool keep = shanten <= 2 && pass >= 0;
        int option = keep ? pass : nine;

        FirePopup(option, keep ? "kept nine terminals" : "took the redeal", AutoActionKind.Pass);
        return true;
    }

    private void Advance(LayoutProfile profile, int state)
    {
        var unit = plugin.Reader.TryGetAddon(out var active);
        if (unit is null || active is null)
            return;

        if (ReadState(unit, active) != state)
            return;

        int op = state == active.StateCodes.HandResult ? 14 : 17;
        FireOne(unit, op);
        acted = true;
        RecordAction(AutoActionKind.Advance, $"advanced state {state}");
        LastStatus = $"advanced state {state}";
        Log($"advance -> [{op}] at state {state}");
    }

    private bool DiscardDrawn(BoardRead read)
    {
        var slots = read.HandSlots;
        if (!read.HandSlotsValid || !SlotsMatchHand(slots, read.Table!.Hand))
        {
            LastStatus = "hand slot order does not match the hand array";
            Log("riichi: hand slot order does not match the hand array; refusing to discard");
            return false;
        }

        HandSlot? drawn = null;
        foreach (var slot in slots)
        {
            if (slot.Slot == 13)
            {
                drawn = slot;
                break;
            }
        }

        if (drawn is null)
        {
            LastStatus = "drawn tile slot not readable";
            Log("riichi: drawn slot 13 not readable; refusing to guess");
            return false;
        }

        return FireDiscard(drawn.Value, "tsumogiri (riichi)");
    }

    private bool TryDiscardTile(int tile, BoardRead read)
    {
        var slots = read.HandSlots;
        if (!read.HandSlotsValid || !SlotsMatchHand(slots, read.Table!.Hand))
        {
            LastStatus = "hand slot order does not match the hand array";
            Log("discard: hand slot order does not match the hand array; refusing to discard");
            return false;
        }

        var slot = HandSlotReader.FindForTile(slots, tile);
        if (slot is null)
        {
            LastStatus = $"tile {Tiles.Name(tile)} is not in the visible hand";
            Log($"discard: {Tiles.Name(tile)} not found among {slots.Count} visible hand slots");
            return false;
        }

        return FireDiscard(slot.Value, $"discard {Tiles.Name(tile)}");
    }

    /// <summary>
    /// Whether a complete hand is actually allowed to win. A closed hand always
    /// has menzen tsumo, so it can trust the game's prompt. An open hand needs a
    /// real yaku; if the meld tiles are unreadable, fail closed and keep
    /// discarding rather than firing an option the client will ignore.
    /// </summary>
    private static bool HasWinningYaku(TableState table)
    {
        if (table.CalledMelds == 0)
            return true; // menzen tsumo

        if (table.Seats[0].MeldTiles.Count == 0)
            return false;

        var full = (int[])table.Hand.Clone();
        foreach (int tile in table.Seats[0].MeldTiles)
            full[tile]++;

        return CallAdvisor.HasOpenYakuPath(full, 0, CallKind.Pass);
    }

    /// <summary>
    /// A riichi declaration is only legal if the follow-up discard keeps the
    /// hand ready. Pick the highest-scoring tenpai option, never the generic
    /// best cut (which can be a deliberate fold).
    /// </summary>
    private static int BestTenpaiDiscard(Advice? advice)
    {
        if (advice is null)
            return -1;

        DiscardOption? best = null;
        foreach (var option in advice.Options)
        {
            if (option.ResultingShanten != 0)
                continue;
            if (best is null || option.Score > best.Score)
                best = option;
        }

        return best?.Tile ?? -1;
    }

    /// <summary>
    /// The callback addresses display slots, not the memory hand array. Before
    /// trusting a slot, require the visible slot tiles to be exactly the hand
    /// counts. A stale or shifted node id must fail closed, never discard a
    /// tile the player does not hold.
    /// </summary>
    private static bool SlotsMatchHand(IReadOnlyList<HandSlot> slots, int[] hand)
    {
        if (slots.Count == 0)
            return false;

        var counts = new int[Tiles.Kinds];
        foreach (var slot in slots)
        {
            if (!slot.IsValid)
                return false;
            counts[slot.Tile]++;
        }

        for (int t = 0; t < Tiles.Kinds; t++)
        {
            if (counts[t] != hand[t])
                return false;
        }

        return true;
    }

    private bool FireDiscard(HandSlot slot, string label)
    {
        var unit = plugin.Reader.TryGetAddon(out var profile);
        if (unit is null || profile is null)
            return false;

        int state = ReadState(unit, profile);
        if (state != profile.StateCodes.OurTurnDiscard
            && state != profile.StateCodes.SelfDeclareList
            && state != profile.StateCodes.CallPromptList
            && state != profile.StateCodes.RiichiDiscard)
        {
            Log($"discard: not a discard state (state {state})");
            return false;
        }

        if (slot.IconId <= 0)
        {
            Log($"discard: slot {slot.Slot} has no readable icon; refusing the commit");
            return false;
        }

        FireInts(unit, 15, slot.IconId);
        FireInts(unit, 7, slot.Slot);

        acted = true;
        RecordAction(AutoActionKind.Discard, label);
        LastStatus = label;
        Log($"{label} -> [15,{slot.IconId}] + [7,{slot.Slot}] (state {state})");
        return true;
    }

    private bool FirePopup(int option, string label, AutoActionKind kind)
    {
        if (option < 0)
        {
            LastStatus = "no popup option";
            return false;
        }

        if (popupAttempts >= MaxPopupAttempts)
        {
            acted = true;
            LastStatus = "popup stalled";
            Log($"{label}: no acceptance after {popupAttempts} attempts; yielding to the game timer");
            return false;
        }

        if (!FireCall(option))
            return false;

        popupAttempts++;
        if (popupAttempts == 1)
            RecordAction(kind, label);

        LastStatus = label;
        actAt = Environment.TickCount64 + PopupRetryDelayMs;
        return true;
    }

    private bool FireCall(int option)
    {
        var unit = plugin.Reader.TryGetAddon(out var profile);
        if (unit is null || profile is null)
            return false;

        int state = ReadState(unit, profile);
        if (state is not (6 or 15 or 25 or 30))
        {
            Log($"call: not a call/self-action state (state {state})");
            return false;
        }

        FireInts(unit, 11, option);
        Log($"popup -> [11,{option}] (state {state})");
        return true;
    }

    private List<ActionOption> OptionsFor(BoardRead read, CallPrompt prompt)
    {
        if (prompt.Options.Count > 0)
            return [.. prompt.Options];

        if (read.Profile is null)
            return [];

        var unit = plugin.Reader.TryGetAddon(out var profile);
        if (unit is null || profile is null)
            return [];

        return CallPromptReader.ReadOptions(unit, profile.StateCodes);
    }

    private static int ReadState(AtkUnitBase* unit, LayoutProfile profile)
    {
        if (unit->AtkValues is null)
            return -1;

        int index = profile.StateCodes.StateCodeValueIndex;
        if (index < 0 || index >= unit->AtkValuesCount)
            return -1;

        var value = unit->AtkValues[index];
        return value.Type switch
        {
            ValueType.Int => value.Int,
            ValueType.UInt => (int)value.UInt,
            _ => -1,
        };
    }

    private static void FireInts(AtkUnitBase* addon, int op, int arg)
    {
        AtkValue* values = stackalloc AtkValue[2];
        values[0].SetInt(op);
        values[1].SetInt(arg);
        addon->FireCallback(2, values, true);
    }

    private static void FireOne(AtkUnitBase* addon, int op)
    {
        AtkValue* values = stackalloc AtkValue[1];
        values[0].SetInt(op);
        addon->FireCallback(1, values, true);
    }

    private static string BuildTurnKey(BoardRead read, int state)
    {
        // 6 and 30 are the same discard turn; everything else is its own group.
        string group = state is 6 or 30 ? "turn" : state.ToString();
        if (read.Table is null)
            return $"{group}|no-hand";

        return $"{group}|{Tiles.Format(read.Table.Hand)}|{read.Table.CalledMelds}";
    }

    private int DelayFor(int state, LayoutProfile profile, Configuration config)
    {
        var codes = profile.StateCodes;

        if (state == codes.OurTurnDiscard
            || state == codes.SelfDeclareList
            || state == codes.CallPromptList
            || state == codes.RiichiDiscard)
        {
            return Roll(config.AutoPlayTurnDelayMinMs, config.AutoPlayTurnDelayMaxMs);
        }

        if (state == codes.CallPrompt)
            return Roll(config.AutoPlayCallDelayMinMs, config.AutoPlayCallDelayMaxMs);

        if (state == codes.HandResult || state == codes.EndOfHand)
            return Roll(config.AutoPlayResultDelayMinMs, config.AutoPlayResultDelayMaxMs);

        return 0;
    }

    private int Roll(int min, int max) => max <= min ? min : rng.Next(min, max);

    private static bool IsActionable(int state, LayoutProfile profile)
    {
        var codes = profile.StateCodes;
        return state == codes.HandResult
            || state == codes.EndOfHand
            || state == codes.CallPrompt
            || state == codes.OurTurnDiscard
            || state == codes.SelfDeclareList
            || state == codes.CallPromptList
            || state == codes.RiichiDiscard
            || state == codes.ChiVariantSelect;
    }

    private void RecordAction(AutoActionKind kind, string label)
    {
        Stats.Record(kind, label);
        plugin.GameLogger.RecordAutoAction(kind.ToString(), label);
    }

    private void ResetSession(bool resetStats = true)
    {
        if (resetStats)
            Stats.Reset();

        addonAbsent = true;
        graceUntil = 0;
        nextTickAllowed = 0;
        prevState = int.MinValue;
        lastTurnKey = "";
        lastPromptKey = "";
        stateSince = 0;
        actAt = 0;
        acted = false;
        riichiFired = false;
        riichiDiscardPending = false;
        riichiDiscardTile = -1;
        popupAttempts = 0;
        stuckWarned = false;
        chiPickPending = false;
        chiPickAttempts = 0;
        chiPickAt = 0;
        chiPickerDumped = false;
    }

    private static void Log(string message) =>
        Plugin.Log.Information($"[MahjongAdvisor][auto] {message}");
}
