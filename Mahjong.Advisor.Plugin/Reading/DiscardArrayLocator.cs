using Mahjong.Advisor;

namespace MahjongAdvisor.Reading;

/// <summary>
/// Finds where the discard piles live in addon memory.
///
/// The shipped solver declares per-seat discard arrays but never filled in the
/// offsets, so it reads discard counts and nothing else. The counts are enough to
/// find the tiles: the per-seat data is a uniform struct repeating every 0x2E0
/// bytes, so the arrays sit at one fixed offset inside that block. Searching for
/// an offset where all four seats decode to exactly their reported number of
/// valid tiles pins it down on its own.
///
/// Reading discards from memory is far steadier than inferring them from node
/// positions, which is where every pile-detection bug so far has come from.
/// </summary>
public sealed unsafe class DiscardArrayLocator
{
    /// <summary>Per-seat blocks repeat at this stride, derived from the score offsets.</summary>
    private const int SeatStride = 0x2E0;

    private const int SearchLo = -0x2D0;
    private const int SearchHi = 0x2D0;
    private const int MaxDiscards = 25;
    private const int AddonSizeGuess = 0x1400;

    /// <summary>Offset of a seat's discard array relative to that seat's score field.</summary>
    public int? RelativeOffset { get; private set; }

    public string Status { get; private set; } = "not searched yet";

    public void Reset()
    {
        RelativeOffset = null;
        Status = "not searched yet";
    }

    /// <summary>
    /// Hunts for the array offset using piles already read off screen as the
    /// answer key. Matching four known tile sequences is a far stronger test than
    /// "these bytes decode to plausible tiles", which matched nothing.
    /// </summary>
    public bool TryLocateAgainst(
        byte* addon, LayoutProfile profile, IReadOnlyList<int>[] knownPonds, int textureBase)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(knownPonds);

        if (RelativeOffset is not null || addon is null)
            return RelativeOffset is not null;

        int informative = knownPonds.Count(p => p is { Count: >= 4 });
        if (informative < 3)
        {
            Status = $"waiting for piles ({string.Join("/", knownPonds.Select(p => p?.Count ?? 0))})";
            return false;
        }

        var scores = profile.Offsets.SeatScores;
        if (scores.Length != 4)
            return false;

        var matches = new List<int>();

        for (int rel = SearchLo; rel <= SearchHi; rel += 4)
        {
            if (Overlaps(rel))
                continue;

            bool ok = true;
            for (int seat = 0; seat < 4 && ok; seat++)
            {
                var known = knownPonds[seat];
                if (known is null || known.Count == 0)
                    continue;

                int start = scores[seat] + rel;
                if (start < 0 || start + ((known.Count + 1) * 4) > AddonSizeGuess)
                {
                    ok = false;
                    break;
                }

                for (int i = 0; i < known.Count && ok; i++)
                {
                    int raw = *(int*)(addon + start + (i * 4));
                    var decoded = TileDecoder.Decode(raw, textureBase);
                    ok = decoded.IsValid && decoded.Tile == known[i];
                }
            }

            if (ok)
                matches.Add(rel);
        }

        if (matches.Count != 1)
        {
            Status = matches.Count == 0
                ? "no offset matches the piles on screen"
                : $"{matches.Count} offsets match - need more discards";
            return false;
        }

        RelativeOffset = matches[0];
        Status = $"discard arrays at score{(matches[0] >= 0 ? "+" : "-")}0x{Math.Abs(matches[0]):X}";
        return true;
    }

    /// <summary>
    /// Hunts for the array offset. Needs a couple of seats to have discarded a few
    /// tiles, otherwise there is not enough signal to be sure.
    /// </summary>
    public bool TryLocate(byte* addon, LayoutProfile profile, int[] discardCounts, int textureBase)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(discardCounts);

        if (RelativeOffset is not null)
            return true;
        if (addon is null)
            return false;

        int informative = discardCounts.Count(c => c >= 3);
        if (informative < 2)
        {
            Status = $"waiting for discards ({string.Join("/", discardCounts)})";
            return false;
        }

        var scores = profile.Offsets.SeatScores;
        if (scores.Length != 4)
        {
            Status = "layout has no per-seat score offsets";
            return false;
        }

        var matches = new List<int>();

        for (int rel = SearchLo; rel <= SearchHi; rel += 4)
        {
            if (Overlaps(rel))
                continue;

            bool ok = true;
            for (int seat = 0; seat < 4 && ok; seat++)
            {
                int count = discardCounts[seat];
                if (count <= 0)
                    continue;
                if (count > MaxDiscards)
                {
                    ok = false;
                    break;
                }

                int start = scores[seat] + rel;
                if (start < 0 || start + ((count + 1) * 4) > AddonSizeGuess)
                {
                    ok = false;
                    break;
                }

                ok = SeatDecodes(addon, start, count, textureBase);
            }

            if (ok)
                matches.Add(rel);
        }

        if (matches.Count == 0)
        {
            Status = "no offset decodes all four piles";
            return false;
        }

        if (matches.Count > 1)
        {
            // More than one candidate means the evidence is not yet decisive.
            Status = $"{matches.Count} candidate offsets - need more discards";
            return false;
        }

        RelativeOffset = matches[0];
        Status = $"discard arrays at score{(matches[0] >= 0 ? "+" : "-")}0x{Math.Abs(matches[0]):X}";
        return true;
    }

    /// <summary>Reads one seat's discards once the offset is known.</summary>
    public List<int>? ReadSeat(
        byte* addon, LayoutProfile profile, int seat, int count, int textureBase)
    {
        if (RelativeOffset is not int rel || addon is null)
            return null;
        if (seat is < 0 or > 3 || count < 0 || count > MaxDiscards)
            return null;

        var scores = profile.Offsets.SeatScores;
        if (scores.Length != 4)
            return null;

        int start = scores[seat] + rel;
        var tiles = new List<int>(count);

        for (int i = 0; i < count; i++)
        {
            int raw = *(int*)(addon + start + (i * 4));
            if (raw == 0)
                break;

            var decoded = TileDecoder.Decode(raw, textureBase);
            if (!decoded.IsValid)
                return null;

            tiles.Add(decoded.Tile);
        }

        return tiles;
    }

    /// <summary>A candidate must not run into the neighbouring seat's block.</summary>
    private static bool Overlaps(int rel) => Math.Abs(rel) >= SeatStride;

    private static bool SeatDecodes(byte* addon, int start, int count, int textureBase)
    {
        for (int i = 0; i < count; i++)
        {
            int raw = *(int*)(addon + start + (i * 4));
            if (raw == 0)
                return false;
            if (!TileDecoder.Decode(raw, textureBase).IsValid)
                return false;
        }

        // The slot past the end should be empty; a run that keeps going is some
        // other array that happens to hold tile-shaped numbers.
        if (count < MaxDiscards)
        {
            int next = *(int*)(addon + start + (count * 4));
            if (next != 0 && TileDecoder.Decode(next, textureBase).IsValid)
                return false;
        }

        return true;
    }
}
