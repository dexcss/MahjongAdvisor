namespace Mahjong.Advisor;

/// <summary>
/// Estimates what a hand would actually be worth if it completes, using exact
/// scoring instead of a dora multiplier. A ready hand is priced over its live
/// winning tiles, so a pinfu ryanmen and a yakuhai tanki stop looking alike.
/// </summary>
public static class HandValueEstimator
{
    public static double ExpectedTenpaiValue(
        TableState table,
        int[] thirteen,
        IReadOnlyList<AcceptedTile> accepted,
        bool riichi)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(thirteen);
        ArgumentNullException.ThrowIfNull(accepted);

        var melds = table.Seats.Length > 0
            ? HandValue.ParseMelds(table.Seats[0].MeldTiles)
            : [];

        // Without readable meld tiles an open hand cannot be priced exactly;
        // return zero and let the caller keep its heuristic value.
        if (melds.Count != table.CalledMelds)
            return 0;

        // `thirteen` is the hand after the candidate discard: 13 - 3 per meld.
        if (Tiles.Total(thirteen) != 13 - (melds.Count * 3))
            return 0;

        bool closed = melds.Count == 0;
        double weighted = 0;
        double liveTotal = 0;

        foreach (var option in accepted)
        {
            if (option.Live <= 0)
                continue;

            var hand = (int[])thirteen.Clone();
            hand[option.Tile]++;

            int dora = HandValueEstimator.DoraCount(hand, table.DoraIndicators) + table.AkaDora;

            var value = HandValue.Evaluate(new HandValueRequest
            {
                Concealed = hand,
                Melds = melds,
                Method = WinMethod.Ron,
                WinningTile = option.Tile,
                Riichi = riichi && closed,
                Dora = dora,
            });

            if (value is null)
                continue;

            weighted += value.RonPoints * option.Live;
            liveTotal += option.Live;
        }

        return liveTotal > 0 ? weighted / liveTotal : 0;
    }

    public static int DoraCount(int[] hand, IReadOnlyList<int> indicators)
    {
        int total = 0;
        foreach (int indicator in indicators)
            total += hand[Tiles.DoraFromIndicator(indicator)];

        return total;
    }
}
