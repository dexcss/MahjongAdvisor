using Xunit;

namespace Mahjong.Advisor.Tests;

public class HandValueTests
{
    private static HandValueResult? Score(
        string concealed,
        string melds = "",
        WinMethod method = WinMethod.Ron,
        string? win = null,
        bool riichi = false,
        int dora = 0,
        bool dealer = false,
        int seatWind = -1,
        int roundWind = -1)
    {
        var meldList = melds
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => ToTiles(Tiles.Parse(m)))
            .ToList();

        return HandValue.Evaluate(new HandValueRequest
        {
            Concealed = Tiles.Parse(concealed),
            Melds = meldList,
            Method = method,
            WinningTile = win is null ? -1 : ToTiles(Tiles.Parse(win))[0],
            Riichi = riichi,
            Dora = dora,
            Dealer = dealer,
            SeatWind = seatWind,
            RoundWind = roundWind,
        });
    }

    private static int[] ToTiles(int[] counts)
    {
        var tiles = new List<int>();
        for (int tile = 0; tile < counts.Length; tile++)
        {
            for (int n = 0; n < counts[tile]; n++)
                tiles.Add(tile);
        }

        return [.. tiles];
    }

    [Fact]
    public void PinfuTsumoIsTwentyFu()
    {
        // 123m 567m 234p 567p 88s, winning on a ryanmen 7m. The 1m keeps
        // tanyao out of the picture, so pinfu is the whole story.
        var value = Score(
            "123m567m234p567p88s",
            method: WinMethod.Tsumo,
            win: "7m");

        Assert.NotNull(value);
        Assert.Equal(2, value!.Han); // pinfu + menzen tsumo
        Assert.Equal(20, value.Fu);
        Assert.Equal(700, value.TsumoDealerPays);
        Assert.Equal(400, value.TsumoOthersPay);
        Assert.Equal(1500, value.TotalPoints(WinMethod.Tsumo));
    }

    [Fact]
    public void PinfuRonIsOneThousand()
    {
        var value = Score("123m567m234p567p88s", win: "7m");

        Assert.NotNull(value);
        Assert.Equal(1, value!.Han);
        Assert.Equal(30, value.Fu);
        Assert.Equal(1000, value.RonPoints);
    }

    [Fact]
    public void YakuhaiTankiPaysFortyFu()
    {
        // 123m 456p 789s 555z + 55m pair, waiting on the pair.
        var value = Score("123m456p789s55m555z", win: "5m");

        Assert.NotNull(value);
        Assert.Contains(value!.Yaku, y => y.Contains("Yakuhai"));
        Assert.Equal(1, value.Han);
        Assert.Equal(40, value.Fu);
        Assert.Equal(1300, value.RonPoints);
    }

    [Fact]
    public void ChiitoitsuIsTwentyFiveFu()
    {
        var value = Score("1122m3344p5566s77z", win: "7z");

        Assert.NotNull(value);
        Assert.Equal(2, value!.Han);
        Assert.Equal(25, value.Fu);
        Assert.Equal(1600, value.RonPoints);
    }

    [Fact]
    public void KokushiIsAYakuman()
    {
        var value = Score("119m19p19s1234567z", win: "1m");

        Assert.NotNull(value);
        Assert.Equal(1, value!.YakumanCount);
        Assert.Contains(value.Yaku, y => y.Contains("Kokushi"));
        Assert.Equal(32000, value.RonPoints);
    }

    [Fact]
    public void IttsuuCountsAndPenchanFuApplies()
    {
        // 123 456 789m + 123p + 55s, winning on 3p with the 12p edge wait.
        var value = Score("123456789m123p55s", win: "3p");

        Assert.NotNull(value);
        Assert.Contains(value!.Yaku, y => y.Contains("Ittsuu"));
        Assert.Equal(2, value.Han);
        Assert.Equal(40, value.Fu);
        Assert.Equal(2600, value.RonPoints);
    }

    [Fact]
    public void OpenTanyaoScores()
    {
        var value = Score(
            "567m345p678s66p",
            melds: "234m",
            win: "6p");

        Assert.NotNull(value);
        Assert.Contains(value!.Yaku, y => y.Contains("Tanyao"));
        Assert.Equal(1, value.Han);
        Assert.Equal(1000, value.RonPoints);
    }

    [Fact]
    public void OpenHandWithNoYakuCannotWin()
    {
        // Open 123m with 456m 789p 234s and a 55z pair: no yaku at all.
        Assert.Null(Score("456m789p234s55z", melds: "123m", win: "3s"));
        Assert.Null(Score("456m789p234s55z", melds: "123m", win: "3s", dora: 3));
    }

    [Fact]
    public void ToitoiAndSanankouStack()
    {
        // Ron on 1m completes that triplet, so only three remain concealed.
        var value = Score("111333m555s777z99m", win: "1m");

        Assert.NotNull(value);
        Assert.Contains(value!.Yaku, y => y.Contains("Toitoi"));
        Assert.Contains(value.Yaku, y => y.Contains("Sanankou"));
        Assert.Contains(value.Yaku, y => y.Contains("Yakuhai (Chun)"));
        Assert.Equal(0, value.YakumanCount);
        Assert.Equal(5, value.Han);
        Assert.Equal(8000, value.RonPoints);
    }

    [Fact]
    public void RonOnThePairKeepsSuuankou()
    {
        var value = Score("111333m555s777z99m", win: "9m");

        Assert.NotNull(value);
        Assert.Equal(1, value!.YakumanCount);
        Assert.Contains(value.Yaku, y => y.Contains("Suuankou"));
    }

    [Fact]
    public void RiichiPinfuTsumoIsThreeHan()
    {
        var value = Score(
            "123m567m234p567p88s",
            method: WinMethod.Tsumo,
            win: "7m",
            riichi: true);

        Assert.NotNull(value);
        Assert.Equal(3, value!.Han);
        Assert.Equal(20, value.Fu);
        Assert.Equal(2700, value.TotalPoints(WinMethod.Tsumo));
    }

    [Fact]
    public void ChuurenIsAYakuman()
    {
        var value = Score("11123456789995m", win: "5m");

        Assert.NotNull(value);
        Assert.Equal(1, value!.YakumanCount);
        Assert.Contains(value.Yaku, y => y.Contains("Chuuren"));
    }

    [Fact]
    public void HonitsuCountsThreeClosed()
    {
        // 123 456 789m + East triplet + South pair: honitsu and ittsuu stack.
        var value = Score("123456789m111z22z", win: "9m");

        Assert.NotNull(value);
        Assert.Contains(value!.Yaku, y => y.Contains("Honitsu"));
        Assert.Contains(value.Yaku, y => y.Contains("Ittsuu"));
        Assert.Equal(5, value.Han);
    }

    [Fact]
    public void ClosedRonWithoutYakuIsNotAWin()
    {
        // Kanchan 4s in 345s, no pinfu, no tanyao, no riichi: no yaku.
        Assert.Null(Score("123m456p345789s22z", win: "4s"));
        Assert.Null(Score("123m456p345789s22z", win: "4s", dora: 2));
    }

    [Fact]
    public void DealerRonIsOneAndAHalfTimesNonDealer()
    {
        var nonDealer = Score("123m567m234p567p88s", win: "7m");
        var dealer = Score("123m567m234p567p88s", win: "7m", dealer: true);

        Assert.NotNull(nonDealer);
        Assert.NotNull(dealer);
        Assert.Equal(1000, nonDealer!.RonPoints);
        Assert.Equal(1500, dealer!.RonPoints);
    }

    [Fact]
    public void WindsOnlyCountWhenKnown()
    {
        // An East triplet is worth nothing until the wind is actually known.
        var unknown = Score("123m456p789s111z55m", win: "5m");
        var eastRound = Score("123m456p789s111z55m", win: "5m", roundWind: 27);

        Assert.Null(unknown);
        Assert.NotNull(eastRound);
        Assert.Equal(1, eastRound!.Han);
        Assert.Contains(eastRound.Yaku, y => y.Contains("East"));
    }

    [Fact]
    public void ReadyHandValueDependsOnTheWait()
    {
        static TableState Table() => new()
        {
            Hand = Tiles.Parse("123m567m234p567p88s"),
            Seats = [new SeatView(), new SeatView(), new SeatView(), new SeatView()],
            Observations = Observed.OwnHand | Observed.Discards,
            WallRemaining = 40,
        };

        // 23m waiting 1m/4m: the 1m side keeps the 1m, so it is pinfu alone
        // (1 han 30 fu). The 4m side leaves only simples, adding tanyao
        // (2 han 30 fu). Same wait, different money.
        var onlyOne = HandValueEstimator.ExpectedTenpaiValue(
            Table(),
            Tiles.Parse("23m567m234p567p88s"),
            [new AcceptedTile(Build.Tile("1m"), 4)],
            riichi: false);
        var onlyFour = HandValueEstimator.ExpectedTenpaiValue(
            Table(),
            Tiles.Parse("23m567m234p567p88s"),
            [new AcceptedTile(Build.Tile("4m"), 4)],
            riichi: false);
        var both = HandValueEstimator.ExpectedTenpaiValue(
            Table(),
            Tiles.Parse("23m567m234p567p88s"),
            [new AcceptedTile(Build.Tile("1m"), 4), new AcceptedTile(Build.Tile("4m"), 4)],
            riichi: false);

        Assert.Equal(1000, onlyOne);
        Assert.Equal(2000, onlyFour);
        Assert.Equal(1500, both);

        // A Haku triplet with a tanki pair on 5m: one han but forty fu.
        var yakuhai = HandValueEstimator.ExpectedTenpaiValue(
            Table(),
            Tiles.Parse("123m456p789s555z5m"),
            [new AcceptedTile(Build.Tile("5m"), 3)],
            riichi: false);

        Assert.Equal(1300, yakuhai);
        Assert.True(onlyOne < yakuhai, "a bare pinfu must be worth less than a yakuhai tanki");
    }
}
