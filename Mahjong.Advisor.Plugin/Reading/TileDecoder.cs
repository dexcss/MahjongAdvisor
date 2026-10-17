using Mahjong.Advisor;

namespace MahjongAdvisor.Reading;

public readonly record struct DecodedTile(int Tile, bool IsRed, int Raw)
{
    public bool IsValid => Tile is >= 0 and < 34;

    public static DecodedTile Invalid(int raw) => new(-1, false, raw);
}

/// <summary>
/// Converts raw hand-array / texture values into 0-33 tile indices.
/// </summary>
public static class TileDecoder
{
    /// <summary>
    /// Ids 34/35/36 are the red fives (5m/5p/5s). Confirmed against the shipped
    /// solver's decoder, which maps exactly these three aliases.
    /// </summary>
    private const int RedFiveManRaw = 34;
    private const int RedFivePinRaw = 35;
    private const int RedFiveSouRaw = 36;

    public static DecodedTile Decode(int rawValue, int textureBase)
    {
        // Some captures store the red-five alias itself rather than the absolute
        // texture id. Accept both forms: the relative id after subtracting the
        // base, and the bare 34/35/36 alias. Missing the second form silently
        // drops every red five from the hand.
        if (rawValue is RedFiveManRaw or RedFivePinRaw or RedFiveSouRaw)
        {
            int redTile = rawValue switch
            {
                RedFiveManRaw => 4,
                RedFivePinRaw => 13,
                _ => 22,
            };
            return new DecodedTile(redTile, true, rawValue);
        }

        int id = rawValue - textureBase;

        return id switch
        {
            >= 0 and < 34 => new DecodedTile(id, false, rawValue),
            RedFiveManRaw => new DecodedTile(4, true, rawValue),   // 5m
            RedFivePinRaw => new DecodedTile(13, true, rawValue),  // 5p
            RedFiveSouRaw => new DecodedTile(22, true, rawValue),  // 5s
            _ => DecodedTile.Invalid(rawValue),
        };
    }

    /// <summary>
    /// Decodes a run of raw values, stopping at the first sentinel. Invalid entries
    /// are reported rather than silently dropped so the caller can refuse to score
    /// a half-written hand.
    /// </summary>
    public static (List<DecodedTile> Tiles, int Rejected) DecodeMany(
        ReadOnlySpan<int> raw, int textureBase)
    {
        var tiles = new List<DecodedTile>(raw.Length);
        int rejected = 0;

        foreach (int value in raw)
        {
            // Empty slots read as zero or a large sentinel depending on state.
            if (value <= 0)
                continue;

            var decoded = Decode(value, textureBase);
            if (decoded.IsValid)
                tiles.Add(decoded);
            else
                rejected++;
        }

        return (tiles, rejected);
    }

    /// <summary>
    /// The configured base, unless it fails to decode populated slots and a nearby
    /// one decodes them all. Client tile ids have shifted before, and a silent
    /// shift turns every reading into nonsense.
    /// </summary>
    public static int ResolveTextureBase(ReadOnlySpan<int> rawSlots, int configuredBase, out bool shifted)
    {
        shifted = false;
        if (CountUndecodable(rawSlots, configuredBase) == 0)
            return configuredBase;

        int populated = 0;
        foreach (int raw in rawSlots)
        {
            if (raw > 0)
                populated++;
        }

        // Too little evidence to retune safely.
        if (populated < 5)
            return configuredBase;

        for (int radius = 1; radius <= SearchRadius; radius++)
        {
            foreach (int candidate in (int[])[configuredBase - radius, configuredBase + radius])
            {
                if (CountUndecodable(rawSlots, candidate) == 0)
                {
                    shifted = true;
                    return candidate;
                }
            }
        }

        return configuredBase;
    }

    private const int SearchRadius = 8;

    private static int CountUndecodable(ReadOnlySpan<int> rawSlots, int textureBase)
    {
        int bad = 0;
        foreach (int raw in rawSlots)
        {
            if (raw > 0 && !Decode(raw, textureBase).IsValid)
                bad++;
        }

        return bad;
    }

    /// <summary>Builds a 34-count array. Red fives count as their normal tile.</summary>
    public static int[] ToCounts(IEnumerable<DecodedTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var counts = new int[Tiles.Kinds];
        foreach (var t in tiles)
        {
            if (t.IsValid)
                counts[t.Tile]++;
        }

        return counts;
    }
}
