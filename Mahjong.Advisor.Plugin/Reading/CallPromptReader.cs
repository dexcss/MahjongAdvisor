using Mahjong.Advisor;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace MahjongAdvisor.Reading;

/// <summary>One clickable popup button: its label and the option number `[11, n]` fires.</summary>
public readonly record struct ActionOption(string Label, int Option);

public sealed record CallPrompt(
    int StateCode,
    IReadOnlyList<CallKind> Available,
    int ClaimedTile,
    IReadOnlyList<ActionOption> Options,
    IReadOnlyList<string> RawLabels,
    IReadOnlyList<int> RawClaimCandidates)
{
    public bool HasClaimedTile => ClaimedTile is >= 0 and < Tiles.Kinds;

    public static readonly CallPrompt None =
        new(-1, [], -1, [], [], []);
}

/// <summary>
/// Works out whether a call prompt is up, which options it offers, and which
/// tile is being claimed.
///
/// Everything here is read-only. The consecutive button-label run and its
/// `[11, position]` fire values were verified live by the reference solver; the
/// claim-band AtkValues are kept only for diagnostics because this client put
/// the tail of our own hand in them. Auto-play never fires without a readable
/// option list.
/// </summary>
public sealed unsafe class CallPromptReader
{
    /// <param name="pondClaimedTile">
    /// The tile most recently added to a pond, or -1. This is the reliable source:
    /// the AtkValue band the shipped solver documents turned out to hold the tail
    /// of our own hand on this client, verified against a captured game log.
    /// </param>
    public CallPrompt Read(
        AtkUnitBase* unit,
        LayoutProfile profile,
        int stateCode,
        int pondClaimedTile = -1,
        int[]? handCounts = null,
        IReadOnlyList<int>? recentAdditions = null)
    {
        if (unit is null || profile is null)
            return CallPrompt.None;

        var codes = profile.StateCodes;

        // 15 is the call prompt; 6 and 30 are the self-action/discard surfaces
        // (30 carries a Riichi/Pass prompt when one is offered). 28 is the
        // post-call discard surface, not a prompt, despite the old name.
        bool isPrompt = stateCode == codes.CallPrompt
            || stateCode == codes.SelfDeclareList
            || stateCode == codes.OurTurnDiscard;

        if (!isPrompt)
            return CallPrompt.None;

        if (unit->AtkValues is null || unit->AtkValuesCount == 0)
            return CallPrompt.None;

        // The button labels live in one consecutive string run. Do not fall back
        // to scanning unrelated AtkValues: a player name containing "chi" or
        // "kan" is not a call prompt, and auto-play must never fire on it.
        var options = ReadOptions(unit, codes);
        var labels = options.Select(o => o.Label).ToList();
        var available = MatchLabels(labels, profile.CallLabels);

        // The AtkValue claim band is still captured for the log, but it is NOT a
        // candidate for the decision: these slots were proved to hold the tail of
        // our own hand on this client. The pond's newest tile, the chi slot and
        // the recent board additions are the reliable sources.
        var (_, candidates) = ScanClaimedTile(unit, profile, codes);

        if (available.Count == 0)
            return CallPrompt.None;

        // Prefer a candidate that is actually legal for what the prompt offers.
        // Pon needs two in hand, chi needs a run partner -- that constraint is far
        // stronger than any single reading of the tile.
        var candidateTiles = new List<int>();
        if (pondClaimedTile is >= 0 and < Tiles.Kinds)
            candidateTiles.Add(pondClaimedTile);

        // The shipped solver reads the chi claim from one specific slot, noting
        // that nearby slots "often decode as unrelated hand tiles" -- which is
        // exactly what the captured logs showed.
        int chiSlot = ReadTileAt(unit, profile, codes.ChiClaimedTile);
        if (chiSlot >= 0 && !candidateTiles.Contains(chiSlot))
            candidateTiles.Add(chiSlot);

        // A tick can land between two board changes, so keep a few recent ones.
        if (recentAdditions is not null)
        {
            foreach (int tile in recentAdditions)
            {
                if (tile is >= 0 and < Tiles.Kinds && !candidateTiles.Contains(tile))
                    candidateTiles.Add(tile);
            }
        }

        int claimed = ChooseLegalClaim(candidateTiles, available, handCounts);

        return new CallPrompt(stateCode, available, claimed, options, labels, candidates);
    }

    /// <summary>
    /// Reads the consecutive string run that holds popup button labels. The
    /// fire value is the button's position in that run, not its AtkValue index.
    /// Returning no options is safer than inventing indices; auto-play waits.
    /// </summary>
    public static List<ActionOption> ReadOptions(AtkUnitBase* unit, LayoutStateCodes codes)
    {
        var options = new List<ActionOption>(6);
        if (unit is null || unit->AtkValues is null || unit->AtkValuesCount == 0)
            return options;

        int start = Math.Max(0, codes.ActionButtonBaseIndex);
        int end = Math.Min((int)unit->AtkValuesCount, start + Math.Max(1, codes.ButtonLabelScanLimit));

        for (int i = start; i < end; i++)
        {
            var value = unit->AtkValues[i];
            if (value.Type is not (ValueType.String or ValueType.ConstString or ValueType.ManagedString))
                break;
            if (value.String.Value is null)
                break;

            string text = value.String.ToString().Trim();
            if (text.Length == 0)
                break;

            options.Add(new ActionOption(text, i - start));
        }

        return options;
    }

    /// <summary>
    /// Option number for a semantic call kind, or -1 when the popup does not
    /// offer it. Uses the same label table as advice matching.
    /// </summary>
    public static int FindOption(
        IReadOnlyList<ActionOption> options, CallKind kind, LayoutCallLabels map)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(map);

        string[] candidates = kind switch
        {
            CallKind.Ron => map.Ron,
            CallKind.Tsumo => map.Tsumo,
            CallKind.Riichi => map.Riichi,
            CallKind.Kan => map.Kan,
            CallKind.Pon => map.Pon,
            CallKind.Chi => map.Chi,
            CallKind.Pass => map.Pass,
            _ => [],
        };

        foreach (var option in options)
        {
            foreach (string candidate in candidates)
            {
                if (option.Label.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                    return option.Option;
            }
        }

        return -1;
    }

    /// <summary>First option whose label contains any of the supplied words.</summary>
    public static int FindOptionContaining(
        IReadOnlyList<ActionOption> options, params string[] words)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(words);

        foreach (var option in options)
        {
            foreach (string word in words)
            {
                if (option.Label.Contains(word, StringComparison.OrdinalIgnoreCase))
                    return option.Option;
            }
        }

        return -1;
    }

    private static List<CallKind> MatchLabels(List<string> labels, LayoutCallLabels map)
    {
        var found = new List<CallKind>();

        foreach (string label in labels)
        {
            string key = label.ToLowerInvariant();
            CallKind? kind =
                Matches(key, map.Ron) ? CallKind.Ron
                : Matches(key, map.Tsumo) ? CallKind.Tsumo
                : Matches(key, map.Riichi) ? CallKind.Riichi
                : Matches(key, map.Kan) ? CallKind.Kan
                : Matches(key, map.Pon) ? CallKind.Pon
                : Matches(key, map.Chi) ? CallKind.Chi
                : Matches(key, map.Pass) ? CallKind.Pass
                : null;

            if (kind is not null && !found.Contains(kind.Value))
                found.Add(kind.Value);
        }

        // A prompt always lets you decline, whether or not the label was matched.
        if (found.Count > 0 && !found.Contains(CallKind.Pass))
            found.Add(CallKind.Pass);

        return found;
    }

    /// <summary>
    /// Picks the candidate consistent with the offered calls. If none fits, the
    /// whole set of tiles that would fit is considered; a unique answer there is
    /// still a certainty. Otherwise returns -1 so the UI can admit it cannot tell.
    /// </summary>
    private static int ReadTileAt(AtkUnitBase* unit, LayoutProfile profile, int index)
    {
        if (index < 0 || index >= unit->AtkValuesCount)
            return -1;

        var value = unit->AtkValues[index];
        int raw = value.Type switch
        {
            ValueType.Int => value.Int,
            ValueType.UInt => (int)value.UInt,
            _ => 0,
        };

        if (raw <= 0)
            return -1;

        var decoded = TileDecoder.Decode(raw, profile.TileTextureBase);
        return decoded.IsValid ? decoded.Tile : -1;
    }

    /// <summary>Every tile that could legally be the one on offer, given our hand.</summary>
    public static List<int> LegalClaims(int[] hand, IReadOnlyCollection<CallKind> available)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(available);

        bool pon = available.Contains(CallKind.Pon);
        bool kan = available.Contains(CallKind.Kan);
        bool chi = available.Contains(CallKind.Chi);

        var possible = new List<int>();
        for (int t = 0; t < Tiles.Kinds; t++)
        {
            if (IsLegalClaim(t, hand, pon, kan, chi))
                possible.Add(t);
        }

        return possible;
    }

    private static int ChooseLegalClaim(
        List<int> candidates, List<CallKind> available, int[]? hand)
    {
        if (hand is null)
            return candidates.Count > 0 ? candidates[0] : -1;

        bool pon = available.Contains(CallKind.Pon);
        bool kan = available.Contains(CallKind.Kan);
        bool chi = available.Contains(CallKind.Chi);

        foreach (int tile in candidates)
        {
            if (IsLegalClaim(tile, hand, pon, kan, chi))
                return tile;
        }

        // Nothing offered fits, so work out which tiles could have been claimed.
        var possible = LegalClaims(hand, available);
        return possible.Count == 1 ? possible[0] : -1;
    }

    private static bool IsLegalClaim(int tile, int[] hand, bool pon, bool kan, bool chi)
    {
        if (kan && hand[tile] >= 3)
            return true;
        if (pon && hand[tile] >= 2)
            return true;

        if (chi && !Tiles.IsHonor(tile))
        {
            int rank = Tiles.Rank(tile);
            int suitBase = Tiles.SuitBase(tile);
            (int A, int B)[] shapes = [(rank - 2, rank - 1), (rank - 1, rank + 1), (rank + 1, rank + 2)];
            foreach (var (a, b) in shapes)
            {
                if (a is < 1 or > 9 || b is < 1 or > 9)
                    continue;
                if (hand[suitBase + a - 1] > 0 && hand[suitBase + b - 1] > 0)
                    return true;
            }
        }

        return false;
    }

    private static bool Matches(string key, string[] candidates) =>
        candidates.Any(c => key.Contains(c, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The claimed tile sits somewhere in a narrow band of AtkValue slots. Collect
    /// every int in that band that decodes to a real tile, and take the one that
    /// repeats — the claim is usually written to more than one slot.
    /// </summary>
    private static (int Tile, List<int> Candidates) ScanClaimedTile(
        AtkUnitBase* unit, LayoutProfile profile, LayoutStateCodes codes)
    {
        var raw = new List<int>();
        int lo = Math.Max(0, codes.ClaimScanLo);
        int hi = Math.Min((int)unit->AtkValuesCount - 1, codes.ClaimScanHi);

        var decoded = new List<int>();
        for (int i = lo; i <= hi; i++)
        {
            var value = unit->AtkValues[i];
            int number = value.Type switch
            {
                ValueType.Int => value.Int,
                ValueType.UInt => (int)value.UInt,
                _ => 0,
            };

            if (number <= 0)
                continue;

            raw.Add(number);
            var tile = TileDecoder.Decode(number, profile.TileTextureBase);
            if (tile.IsValid)
                decoded.Add(tile.Tile);
        }

        if (decoded.Count == 0)
            return (-1, raw);

        // A pon claim is published as a duplicate within the band; a single
        // appearance is usually just one of our own hand tiles.


        // Prefer a value that appears more than once, else the first plausible one.
        int repeated = decoded
            .GroupBy(t => t)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .FirstOrDefault(-1);

        return (repeated >= 0 ? repeated : decoded[0], raw);
    }
}
