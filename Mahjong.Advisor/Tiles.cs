using System.Text;

namespace Mahjong.Advisor;

/// <summary>
/// Tile indexing: 0-8 = 1m..9m, 9-17 = 1p..9p, 18-26 = 1s..9s,
/// 27-33 = East, South, West, North, Haku, Hatsu, Chun.
/// A "hand" is always a 34-length count array.
/// </summary>
public static class Tiles
{
    public const int Kinds = 34;
    public const int FirstHonor = 27;

    public static readonly int[] Terminals =
        [0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33];

    public static bool IsHonor(int t) => t >= FirstHonor;

    public static bool IsTerminal(int t) => Array.IndexOf(Terminals, t) >= 0;

    /// <summary>1-9 for suited tiles; 0 for honors.</summary>
    public static int Rank(int t) => t >= FirstHonor ? 0 : (t % 9) + 1;

    /// <summary>Index of rank 1 in this tile's suit; -1 for honors.</summary>
    public static int SuitBase(int t) => t >= FirstHonor ? -1 : t - (t % 9);

    /// <summary>Parses "123m456p11z" notation into a 34-count array.</summary>
    public static int[] Parse(string notation)
    {
        ArgumentNullException.ThrowIfNull(notation);
        var counts = new int[Kinds];
        var pending = new List<int>();
        foreach (char ch in notation)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            if (char.IsDigit(ch))
            {
                int d = ch - '0';
                if (d == 0)
                    throw new FormatException("Red-five notation '0' is not handled here; pass aka separately.");
                pending.Add(d);
                continue;
            }

            int suitBase = ch switch
            {
                'm' => 0,
                'p' => 9,
                's' => 18,
                'z' => 27,
                _ => throw new FormatException($"Unexpected character '{ch}' in tile notation."),
            };

            foreach (int d in pending)
            {
                if (ch == 'z' && d > 7)
                    throw new FormatException($"Honor tile {d}z does not exist.");
                counts[suitBase + d - 1]++;
            }

            pending.Clear();
        }

        if (pending.Count > 0)
            throw new FormatException("Tile notation ended with digits that have no suit.");

        return counts;
    }

    /// <summary>
    /// The dora is the tile AFTER the indicator, wrapping within its own group:
    /// 9 goes back to 1, North back to East, Red Dragon back to White.
    /// </summary>
    public static int DoraFromIndicator(int indicator)
    {
        if (indicator is < 0 or >= Kinds)
            throw new ArgumentOutOfRangeException(nameof(indicator));

        if (indicator < FirstHonor)
        {
            int suitBase = SuitBase(indicator);
            int rank = Rank(indicator);
            return suitBase + (rank % 9);
        }

        // Winds cycle among themselves; dragons among themselves.
        if (indicator <= 30)
            return 27 + ((indicator - 27 + 1) % 4);
        return 31 + ((indicator - 31 + 1) % 3);
    }

    public static string Name(int t)
    {
        if (t is < 0 or >= Kinds)
            throw new ArgumentOutOfRangeException(nameof(t));
        if (t >= FirstHonor)
            return $"{t - FirstHonor + 1}z";
        return $"{(t % 9) + 1}{"mps"[t / 9]}";
    }

    /// <summary>
    /// Plain-English tile name matching what the client draws, so nobody has to
    /// learn riichi shorthand to read a suggestion.
    /// Dots are the dotted tiles, Bamboo the green sticks, Characters the
    /// numbered Chinese-numeral tiles.
    /// </summary>
    public static string Describe(int t) => t switch
    {
        27 => "East Wind",
        28 => "South Wind",
        29 => "West Wind",
        30 => "North Wind",
        31 => "White Dragon",
        32 => "Green Dragon",
        33 => "Red Dragon",
        _ => $"{Rank(t)} {SuitName(t)}",
    };

    /// <summary>Shorter form for dense lists: "6 Bam", "3 Circ", "S Wind".</summary>
    public static string DescribeShort(int t) => t switch
    {
        27 => "E Wind",
        28 => "S Wind",
        29 => "W Wind",
        30 => "N Wind",
        31 => "Wh Drgn",
        32 => "Gr Drgn",
        33 => "Rd Drgn",
        _ => $"{Rank(t)} {SuitShort(t)}",
    };

    public static string SuitName(int t) => t >= FirstHonor
        ? "Honour"
        : (t / 9) switch
        {
            0 => "Characters",
            1 => "Dots",
            _ => "Bamboo",
        };

    public static string SuitShort(int t) => t >= FirstHonor
        ? "Hon"
        : (t / 9) switch
        {
            0 => "Char",
            1 => "Dots",
            _ => "Bam",
        };

    public static string Format(IReadOnlyList<int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var sb = new StringBuilder();
        for (int suit = 0; suit < 4; suit++)
        {
            int start = suit * 9;
            int len = suit == 3 ? 7 : 9;
            bool any = false;
            for (int r = 0; r < len; r++)
            {
                for (int n = 0; n < counts[start + r]; n++)
                {
                    sb.Append((char)('1' + r));
                    any = true;
                }
            }

            if (any)
                sb.Append("mpsz"[suit]);
        }

        return sb.ToString();
    }

    public static int Total(IReadOnlyList<int> counts)
    {
        int sum = 0;
        for (int i = 0; i < Kinds; i++)
            sum += counts[i];
        return sum;
    }
}
