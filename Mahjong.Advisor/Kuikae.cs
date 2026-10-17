namespace Mahjong.Advisor;

/// <summary>
/// Swap-calling (kuikae): the discards the game forbids on the turn straight
/// after a call.
///
/// You may never throw back the tile you just claimed. After a chi you also may
/// not throw the tile that would have made the identical shape from the other
/// end — claiming 5 with 6 and 7 in hand rules out discarding 8, because 6-7-8
/// is the same run you just made.
///
/// Recommending one of these is not merely poor advice: the client greys the
/// tile out, so the suggestion cannot be followed at all.
/// </summary>
public static class Kuikae
{
    /// <summary>
    /// Tiles that cannot be discarded after claiming <paramref name="claimedTile"/>
    /// using <paramref name="usedFromHand"/>.
    /// </summary>
    public static IReadOnlyList<int> Forbidden(
        CallKind kind, int claimedTile, IReadOnlyList<int> usedFromHand)
    {
        ArgumentNullException.ThrowIfNull(usedFromHand);

        if (claimedTile is < 0 or >= Tiles.Kinds)
            return [];

        var forbidden = new List<int> { claimedTile };

        if (kind != CallKind.Chi || Tiles.IsHonor(claimedTile) || usedFromHand.Count != 2)
            return forbidden;

        int rank = Tiles.Rank(claimedTile);
        int suitBase = Tiles.SuitBase(claimedTile);

        int a = Tiles.Rank(usedFromHand[0]);
        int b = Tiles.Rank(usedFromHand[1]);
        int low = Math.Min(a, b);
        int high = Math.Max(a, b);

        // Both hand tiles above the claim: the run extends upward, so the tile
        // one past the top makes the same shape without it.
        if (low == rank + 1 && high == rank + 2 && rank + 3 <= 9)
            forbidden.Add(suitBase + rank + 3 - 1);

        // Both below: the mirror case.
        else if (high == rank - 1 && low == rank - 2 && rank - 3 >= 1)
            forbidden.Add(suitBase + rank - 3 - 1);

        // Claim in the middle (hand holds rank-1 and rank+1): no swap exists.
        return forbidden;
    }

    /// <summary>
    /// The barred set, worked out purely from which tiles left the hand.
    ///
    /// No claimed tile is needed, which matters because identifying it from the
    /// board is unreliable and getting it wrong bars the wrong suit. For a chi
    /// the two tiles you contributed settle it on their own:
    ///   two in a row (A, A+1) -> both ends are barred, A-1 and A+2, because
    ///     whichever end was claimed, the other end makes the same run;
    ///   a gap (A, A+2)        -> only the tile that filled it, A+1.
    /// </summary>
    public static IReadOnlyList<int> ForbiddenFromHandDelta(IReadOnlyList<int> tilesRemovedFromHand)
    {
        ArgumentNullException.ThrowIfNull(tilesRemovedFromHand);

        if (tilesRemovedFromHand.Count is < 2 or > 3)
            return [];

        // Identical tiles leaving means pon or kan: only that tile is barred.
        if (tilesRemovedFromHand.Distinct().Count() == 1)
            return [tilesRemovedFromHand[0]];

        if (tilesRemovedFromHand.Count != 2)
            return [];

        int first = tilesRemovedFromHand[0];
        int second = tilesRemovedFromHand[1];
        if (Tiles.IsHonor(first) || Tiles.IsHonor(second))
            return [];
        if (Tiles.SuitBase(first) != Tiles.SuitBase(second))
            return [];

        int suitBase = Tiles.SuitBase(first);
        int low = Math.Min(Tiles.Rank(first), Tiles.Rank(second));
        int high = Math.Max(Tiles.Rank(first), Tiles.Rank(second));

        var barred = new List<int>();

        if (high - low == 1)
        {
            if (low - 1 >= 1)
                barred.Add(suitBase + low - 1 - 1);
            if (high + 1 <= 9)
                barred.Add(suitBase + high + 1 - 1);
        }
        else if (high - low == 2)
        {
            barred.Add(suitBase + low + 1 - 1);
        }

        return barred;
    }
}
