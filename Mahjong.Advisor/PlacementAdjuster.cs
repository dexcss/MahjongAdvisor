namespace Mahjong.Advisor;

/// <summary>
/// How hard to push, given where we sit in the standings.
/// Values above 1 mean "weight this more heavily".
/// </summary>
public readonly record struct PlacementStance(double Danger, double Speed, double HandValue)
{
    public static readonly PlacementStance Neutral = new(1.0, 1.0, 1.0);
}

/// <summary>
/// Mahjong pays for finishing position, not for raw points. Being second by 100
/// and second by 20,000 are the same result, so the right amount of risk depends
/// on the standings rather than on the hand alone.
///
/// In front, a deal-in costs you the lead and winning barely helps, so tighten
/// up. In last, a cheap safe hand does nothing for you, so chase value and
/// accept danger.
/// </summary>
public sealed class PlacementAdjuster
{
    /// <summary>Leading: protect it. Danger matters more, extra value matters less.</summary>
    public PlacementStance Leading { get; init; } = new(Danger: 1.3, Speed: 0.9, HandValue: 0.9);

    /// <summary>A commanding lead. Fold almost anything.</summary>
    public PlacementStance CommandingLead { get; init; } = new(Danger: 2.0, Speed: 0.5, HandValue: 0.5);

    public PlacementStance Middle { get; init; } = PlacementStance.Neutral;

    /// <summary>Last: a cheap safe hand does not fix the standings.</summary>
    public PlacementStance Trailing { get; init; } = new(Danger: 0.7, Speed: 1.1, HandValue: 1.3);

    /// <summary>Gap over second place that counts as commanding.</summary>
    public int CommandingLeadGap { get; init; } = 8000;

    /// <summary>1 = highest score. Seat 0 is us.</summary>
    public static int RankOf(IReadOnlyList<int> scores, int seat = 0)
    {
        ArgumentNullException.ThrowIfNull(scores);
        if (seat < 0 || seat >= scores.Count)
            return 0;

        int ours = scores[seat];
        int rank = 1;
        for (int i = 0; i < scores.Count; i++)
        {
            if (i != seat && scores[i] > ours)
                rank++;
        }

        return rank;
    }

    public PlacementStance ComputeFor(TableState table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var scores = table.Scores;
        if (scores.Count != 4)
            return PlacementStance.Neutral;

        int rank = RankOf(scores);

        return rank switch
        {
            1 => GapToSecond(scores) >= CommandingLeadGap ? CommandingLead : Leading,
            4 => Trailing,
            _ => Middle,
        };
    }

    /// <summary>How far clear of the next player down we are.</summary>
    public static int GapToSecond(IReadOnlyList<int> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        if (scores.Count < 2)
            return 0;

        int ours = scores[0];
        int best = int.MinValue;
        for (int i = 1; i < scores.Count; i++)
        {
            if (scores[i] < ours && scores[i] > best)
                best = scores[i];
        }

        return best == int.MinValue ? 0 : ours - best;
    }

    public static string Describe(int rank) => rank switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        4 => "last",
        _ => "unknown",
    };
}
