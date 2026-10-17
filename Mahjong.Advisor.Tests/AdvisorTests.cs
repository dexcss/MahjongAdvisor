using Xunit;

namespace Mahjong.Advisor.Tests;

internal static class Build
{
    public static List<int> Pond(string notation)
    {
        var parsed = Tiles.Parse(notation);
        var pond = new List<int>();
        for (int t = 0; t < Tiles.Kinds; t++)
            for (int n = 0; n < parsed[t]; n++)
                pond.Add(t);
        return pond;
    }

    public static SeatView[] EmptySeats() =>
        [new SeatView(), new SeatView(), new SeatView(), new SeatView()];

    public static int Tile(string single)
    {
        var parsed = Tiles.Parse(single);
        for (int t = 0; t < Tiles.Kinds; t++)
            if (parsed[t] > 0)
                return t;
        throw new ArgumentException("no tile in notation", nameof(single));
    }
}

public class UkeireTests
{
    [Fact]
    public void TankiWaitAcceptsOneKind()
    {
        var hand = Tiles.Parse("123456789m123p1s");
        var result = UkeireCalculator.Calculate(hand, UkeireCalculator.VisibleFromHandOnly(hand));

        Assert.Equal(0, result.Shanten);
        var accepted = Assert.Single(result.Accepted);
        Assert.Equal(Build.Tile("1s"), accepted.Tile);
        Assert.Equal(3, accepted.Live); // we hold the fourth
    }

    [Fact]
    public void OneShantenAcceptsOnlyRealImprovements()
    {
        var hand = Tiles.Parse("123456789m12p1s3s");
        var result = UkeireCalculator.Calculate(hand, UkeireCalculator.VisibleFromHandOnly(hand));

        Assert.Equal(1, result.Shanten);
        var names = result.Accepted
            .Select(a => Tiles.Name(a.Tile))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "1p", "1s", "2p", "2s", "3p", "3s" }, names);
    }

    [Fact]
    public void DeadTilesAreNotCounted()
    {
        var hand = Tiles.Parse("123456789m123p1s");
        var visible = UkeireCalculator.VisibleFromHandOnly(hand);
        visible[Build.Tile("1s")] = 4; // every copy already accounted for

        var result = UkeireCalculator.Calculate(hand, visible);
        Assert.Empty(result.Accepted);
        Assert.Equal(0, result.Width);
    }
}

public class VisibleTileTests
{
    [Fact]
    public void OwnCalledTilesCountAsSeen()
    {
        var seats = Build.EmptySeats();
        seats[0].MeldTiles = [Build.Tile("5p"), Build.Tile("5p"), Build.Tile("5p")];

        var table = new TableState
        {
            Hand = Tiles.Parse("123456789m12p"),
            CalledMelds = 1,
            Seats = seats,
            Observations = Observed.OwnHand,
        };

        // Our own meld contributes three visible copies. Leaving them out made
        // every ukeire count treat those tiles as live.
        Assert.Equal(3, table.BuildVisible()[Build.Tile("5p")]);
    }

    [Fact]
    public void MeldCountHandlesMixedPonsAndKans()
    {
        // Three tiles per pon, four per kan; integer division is correct for
        // every legal combination.
        var seat = new SeatView
        {
            MeldTiles = [0, 0, 0, 1, 1, 1, 2, 2, 2, 2],
        };

        Assert.Equal(3, seat.MeldCount);
    }
}

public class SafetyTests
{
    [Fact]
    public void GenbutsuIsZeroRisk()
    {
        int fourMan = Build.Tile("4m");
        var risk = Safety.Evaluate(fourMan, new HashSet<int> { fourMan }, new int[Tiles.Kinds]);
        Assert.Equal(0.0, risk.DealInRate);
        Assert.Equal(SafetyReason.Genbutsu, risk.Reason);
    }

    [Theory]
    [InlineData("1m", "4m", true)]
    [InlineData("7m", "4m", true)]
    [InlineData("5m", "2m", false)]
    [InlineData("3p", "6p", true)]
    public void SujiRequiresTheMatchingOuterTile(string tile, string discarded, bool expected)
    {
        Assert.Equal(expected, Safety.IsSuji(Build.Tile(tile), new HashSet<int> { Build.Tile(discarded) }));
    }

    [Fact]
    public void FiveIsSujiOnlyWhenBothSidesAreDead()
    {
        Assert.True(Safety.IsSuji(Build.Tile("5m"), new HashSet<int> { Build.Tile("2m"), Build.Tile("8m") }));
        Assert.False(Safety.IsSuji(Build.Tile("5m"), new HashSet<int> { Build.Tile("2m") }));
    }

    [Fact]
    public void HonoursAreNeverSuji()
    {
        Assert.False(Safety.IsSuji(Build.Tile("1z"), new HashSet<int> { Build.Tile("4z") }));
    }

    [Fact]
    public void NoChanceWhenBlockerIsFullyVisible()
    {
        var visible = new int[Tiles.Kinds];
        visible[Build.Tile("3m")] = 4;

        Assert.Equal(2, Safety.ChanceLevel(Build.Tile("1m"), visible));

        var safe = Safety.Evaluate(Build.Tile("1m"), new HashSet<int>(), visible);
        var risky = Safety.Evaluate(Build.Tile("5m"), new HashSet<int>(), new int[Tiles.Kinds]);
        Assert.Equal(SafetyReason.NoChance, safe.Reason);
        Assert.True(safe.DealInRate < risky.DealInRate);
    }

    [Fact]
    public void HonoursGetSaferAsCopiesAppear()
    {
        var visible = new int[Tiles.Kinds];
        double previous = double.MaxValue;
        for (int seen = 0; seen <= 3; seen++)
        {
            visible[Build.Tile("1z")] = seen;
            double rate = Safety.Evaluate(Build.Tile("1z"), new HashSet<int>(), visible).DealInRate;
            Assert.True(rate < previous, $"{seen} copies visible should be safer than {seen - 1}");
            previous = rate;
        }
    }

    [Fact]
    public void TilesPassedAfterRiichiBecomeGenbutsu()
    {
        var seats = Build.EmptySeats();
        seats[1].Discards = Build.Pond("1z2z");
        seats[1].Riichi = true;
        seats[1].RiichiDiscardIndex = 0;
        seats[2].Discards = Build.Pond("5m9p");

        var table = new TableState
        {
            Hand = Tiles.Parse("123456789m1122p3z"),
            Seats = seats,
            Observations = Observed.OwnHand | Observed.SafetyReady,
        };

        var safe = table.GenbutsuAgainst(1);
        Assert.Contains(Build.Tile("1z"), safe); // their own discards
        Assert.Contains(Build.Tile("2z"), safe);
        Assert.Contains(Build.Tile("9p"), safe); // passed after the declaration
    }
}

public class TileNamingTests
{
    [Theory]
    [InlineData("6s", "6 Bamboo")]
    [InlineData("3p", "3 Dots")]
    [InlineData("5m", "5 Characters")]
    [InlineData("2z", "South Wind")]
    [InlineData("7z", "Red Dragon")]
    [InlineData("6z", "Green Dragon")]
    [InlineData("5z", "White Dragon")]
    public void DescribeUsesPlainEnglish(string notation, string expected)
    {
        Assert.Equal(expected, Tiles.Describe(Build.Tile(notation)));
    }

    [Theory]
    [InlineData("6s", "6 Bam")]
    [InlineData("3p", "3 Dots")]
    [InlineData("5m", "5 Char")]
    [InlineData("2z", "S Wind")]
    public void DescribeShortStaysCompact(string notation, string expected)
    {
        Assert.Equal(expected, Tiles.DescribeShort(Build.Tile(notation)));
    }

    [Fact]
    public void NotationStillRoundTrips()
    {
        // The short form is what the log stores, so it must stay stable.
        Assert.Equal("6s", Tiles.Name(Build.Tile("6s")));
        Assert.Equal("2z", Tiles.Name(Build.Tile("2z")));
    }
}

public class DoraTests
{
    [Theory]
    [InlineData("5p", "6p")]   // dora is the tile after the indicator
    [InlineData("9m", "1m")]   // suits wrap 9 -> 1
    [InlineData("9s", "1s")]
    [InlineData("4z", "1z")]   // North wraps back to East
    [InlineData("1z", "2z")]
    [InlineData("7z", "5z")]   // Red Dragon wraps to White
    [InlineData("5z", "6z")]
    public void DoraFollowsTheIndicator(string indicator, string expected)
    {
        Assert.Equal(Build.Tile(expected), Tiles.DoraFromIndicator(Build.Tile(indicator)));
    }

    [Fact]
    public void DoraInHandIsCounted()
    {
        var table = new TableState
        {
            Hand = Tiles.Parse("666m123p456s11z"),
            DoraIndicators = [Build.Tile("5m")], // so 6m is dora
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand,
        };

        Assert.Equal(3, table.CountDora());
    }

    [Fact]
    public void RedFivesCountAsDora()
    {
        var table = new TableState
        {
            Hand = Tiles.Parse("555m123p456s11z"),
            AkaDora = 1,
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand,
        };

        Assert.Equal(1, table.CountDora());
    }

    [Fact]
    public void DoraHeavyHandsAreWorthMore()
    {
        var advisor = new DiscardAdvisor();

        var plain = new TableState
        {
            Hand = Tiles.Parse("234m567p234s11z"),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand,
        };

        var rich = new TableState
        {
            Hand = Tiles.Parse("234m567p234s11z"),
            DoraIndicators = [Build.Tile("1m")], // 2m is dora
            AkaDora = 2,
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand,
        };

        Assert.True(advisor.EstimateWinValue(rich) > advisor.EstimateWinValue(plain) * 2);
    }

    [Fact]
    public void ValueIsCappedAtMangan()
    {
        var advisor = new DiscardAdvisor();
        var huge = new TableState
        {
            Hand = Tiles.Parse("222m222p222s22z"),
            DoraIndicators = [Build.Tile("1m"), Build.Tile("1p"), Build.Tile("1s")],
            AkaDora = 3,
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand,
        };

        Assert.Equal(8000, advisor.EstimateWinValue(huge));
    }

    [Fact]
    public void OpenHandsLoseTheRiichiHan()
    {
        var advisor = new DiscardAdvisor();
        var closed = new TableState
        {
            Hand = Tiles.Parse("234m567p234s11z"),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand,
        };
        var open = new TableState
        {
            Hand = Tiles.Parse("234m567p234s"),
            CalledMelds = 1,
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand,
        };

        Assert.True(advisor.EstimateWinValue(closed) > advisor.EstimateWinValue(open));
    }
}

public class HououTableTests
{
    [Fact]
    public void TenpaiBeatsOneShantenAtEveryTurn()
    {
        foreach (int turn in (int[])[4, 8, 12, 16])
        {
            Assert.True(
                HououTables.WinRate(0, 6, turn, closed: true) >
                HououTables.WinRate(1, 20, turn, closed: true),
                $"turn {turn}");
        }
    }

    [Fact]
    public void WiderShapesWinMoreOften()
    {
        // Same shanten, different acceptance: the wide hand must rate higher.
        Assert.True(
            HououTables.WinRate(1, 30, 8, closed: true) >
            HououTables.WinRate(1, 10, 8, closed: true));
    }

    [Fact]
    public void LaterTurnsAreWorse()
    {
        Assert.True(
            HououTables.WinRate(1, 20, 4, closed: true) >
            HououTables.WinRate(1, 20, 12, closed: true));
    }

    [Fact]
    public void OpenHandsCompleteFasterThanClosedOnes()
    {
        Assert.True(
            HououTables.WinRate(2, 30, 6, closed: false) >
            HououTables.WinRate(2, 30, 6, closed: true));
    }

    [Fact]
    public void CompletedHandIsCertain()
    {
        Assert.Equal(1.0, HououTables.WinRate(-1, 0, 8, closed: true));
    }

    [Fact]
    public void NoLiveTilesMeansNoWinChance()
    {
        // The old code clamped shanten but still picked a normal table row, so a
        // hand with every wait dead rated like a live one.
        Assert.Equal(0.0, HououTables.WinRate(0, 0, 8, closed: true));
        Assert.Equal(0.0, HououTables.WinRate(2, 0, 8, closed: true));
    }

    [Theory]
    [InlineData(0, 8, 0.04)]    // a quiet closed hand is rarely ready early
    [InlineData(2, 8, 0.50)]    // two melds by turn 8 is a different story
    [InlineData(3, 8, 0.85)]    // three melds is nearly always tenpai
    public void CallsAreTheStrongestReadinessSignal(int melds, int turn, double atLeast)
    {
        Assert.True(HououTables.TenpaiChance(melds, turn) >= atLeast * 0.9,
            $"{melds} melds at turn {turn}");
    }

    [Fact]
    public void MoreCallsAlwaysMeansMoreDanger()
    {
        Assert.True(HououTables.TenpaiChance(3, 8) > HououTables.TenpaiChance(2, 8));
        Assert.True(HououTables.TenpaiChance(2, 8) > HououTables.TenpaiChance(1, 8));
        Assert.True(HououTables.TenpaiChance(1, 8) > HououTables.TenpaiChance(0, 8));
    }

    [Fact]
    public void TurnIsDerivedFromTheWall()
    {
        Assert.Equal(1, HououTables.TurnFromWall(70));
        Assert.True(HououTables.TurnFromWall(34) > HououTables.TurnFromWall(66));
    }
}

public class DiscardReaderTests
{
    private static List<int> Pond(params string[] tiles) =>
        [.. tiles.Select(Build.Tile)];

    [Fact]
    public void AllSimplesLooksLikeTerminalCollecting()
    {
        // The pond that cost 32,000: nothing but middle tiles, because every
        // terminal and honour is being kept for a thirteen orphans.
        var read = DiscardReader.Read(Pond("4m", "5m", "6m", "3p", "5p", "7p", "4s", "6s"));
        Assert.True(read.ChasingTerminals);
        Assert.True(read.Confidence > 0);
    }

    [Fact]
    public void ANormalPondReadsAsNothing()
    {
        var read = DiscardReader.Read(Pond("1z", "9m", "4p", "2z", "5s", "1p", "3z", "8m"));
        Assert.False(read.ChasingTerminals);
    }

    [Fact]
    public void TerminalsBecomeDangerousAgainstThatRead()
    {
        var read = DiscardReader.Read(Pond("4m", "5m", "6m", "3p", "5p", "7p", "4s", "6s"));

        // Honours are normally the safest thing you can throw. Not here.
        Assert.True(DiscardReader.DangerMultiplier(Build.Tile("1z"), read) > 2.0);
        Assert.True(DiscardReader.DangerMultiplier(Build.Tile("9m"), read) > 2.0);

        // Middle tiles are unaffected by this particular read.
        Assert.Equal(1.0, DiscardReader.DangerMultiplier(Build.Tile("5m"), read));
    }

    [Fact]
    public void SheddingTwoSuitsSuggestsAFlush()
    {
        // Everything thrown is Characters or Dots, so Bamboo is being kept.
        var read = DiscardReader.Read(Pond("1m", "4m", "7m", "9m", "2p", "5p", "8p", "3p"));
        Assert.True(read.MissingSuit >= 0);
        Assert.True(DiscardReader.DangerMultiplier(Build.Tile("5s"), read) > 1.0);
    }

    [Fact]
    public void TooFewDiscardsMeansNoRead()
    {
        Assert.Equal(DiscardRead.None, DiscardReader.Read(Pond("4m", "5m", "6m")));
        Assert.Equal(1.0, DiscardReader.DangerMultiplier(Build.Tile("1z"), DiscardRead.None));
    }
}

public class SelfRiichiTests
{
    [Fact]
    public void RiichiLeavesNoChoiceButTheDrawnTile()
    {
        // Declared riichi: the thirteen tiles are frozen, so recommending any of
        // them sends the player after a tile the client greys out.
        var table = new TableState
        {
            Hand = Tiles.Parse("4588m3456778p999s"),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand | Observed.Discards,
            WallRemaining = 28,
            SelfRiichi = true,
            DrawnTile = Build.Tile("7p"),
        };

        var advice = new DiscardAdvisor().Recommend(table);

        Assert.Single(advice.Options);
        Assert.Equal(Build.Tile("7p"), advice.Best!.Tile);
        Assert.Contains("riichi", advice.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDrawnTileIsNotSimplyTheLastSlot()
    {
        // From play: hand 456m 34599p 56788s 4z while locked by riichi. The
        // advisor named 4 Characters, the lowest tile, because it read the final
        // array slot. Slot order does not match the sorted display.
        var table = new TableState
        {
            Hand = Tiles.Parse("456m34599p56788s4z"),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand | Observed.Discards,
            WallRemaining = 55,
            SelfRiichi = true,
            DrawnTile = Build.Tile("4z"),
        };

        var advice = new DiscardAdvisor().Recommend(table);
        Assert.Equal(Build.Tile("4z"), advice.Best!.Tile);
        Assert.NotEqual(Build.Tile("4m"), advice.Best.Tile);
    }

    [Fact]
    public void UnknownDrawnTileDoesNotSuggestAnIllegalCut()
    {
        // The hand is locked by riichi, but the read layer could not identify the
        // draw. Returning a normal ranking here would name a tile the client greys
        // out, so the advisor must refuse to name one.
        var table = new TableState
        {
            Hand = Tiles.Parse("456m34599p56788s4z"),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand | Observed.Discards,
            WallRemaining = 55,
            SelfRiichi = true,
            DrawnTile = -1,
        };

        var advice = new DiscardAdvisor().Recommend(table);

        Assert.Empty(advice.Options);
        Assert.Contains("riichi", advice.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithoutRiichiTheHandIsStillOurs()
    {
        var table = new TableState
        {
            Hand = Tiles.Parse("123456789m1122p3z"),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand | Observed.Discards,
            WallRemaining = 28,
            DrawnTile = Build.Tile("3z"),
        };

        Assert.True(new DiscardAdvisor().Recommend(table).Options.Count > 1);
    }
}

public class PushFoldTests
{
    private static TableState AgainstRiichi(string hand, int wall, int riichiSeat = 1)
    {
        var seats = Build.EmptySeats();
        for (int i = 0; i < 4; i++)
        {
            var pond = new List<int>();
            for (int d = 0; d < 8; d++)
                pond.Add((d * 3) % 27);
            seats[i].Discards = pond;
        }

        seats[riichiSeat].Riichi = true;
        seats[riichiSeat].RiichiDiscardIndex = 4;

        return new TableState
        {
            Hand = Tiles.Parse(hand),
            Seats = seats,
            Observations = Observed.OwnHand | Observed.Discards
                | Observed.OpponentRiichi | Observed.RiichiTurn,
            WallRemaining = wall,
            Scores = [25000, 25000, 25000, 25000],
        };
    }

    [Fact]
    public void PushHorizonCountsTheDiscardsStillToCome()
    {
        // Pushing now commits to the tiles after it, not just this one.
        Assert.True(DiscardAdvisor.PushHorizon(2, 46) > 1);
        Assert.Equal(1, DiscardAdvisor.PushHorizon(2, 4));
        Assert.True(DiscardAdvisor.PushHorizon(2, 70) <= DiscardAdvisor.MaxPushHorizon);
    }

    [Fact]
    public void FarFromReadyAgainstRiichiIsAFold()
    {
        // The hand from play: two away, riichi out, half the wall left. Pushing
        // this was worth -1343 once the later discards were counted.
        var advice = new DiscardAdvisor().Recommend(AgainstRiichi("119m2467p13579s55z", 46));
        Assert.True(advice.Folding, "a two-away hand should not race a declared riichi");
    }

    [Fact]
    public void GoodTenpaiStillPushes()
    {
        // 123m 456m 789m + 1122p: tenpai on a shanpon, worth racing for.
        var advice = new DiscardAdvisor().Recommend(AgainstRiichi("123456789m1122p3z", 46));
        Assert.False(advice.Folding, "a ready hand should take the race");
    }

    [Fact]
    public void NoThreatMeansNoFolding()
    {
        var table = new TableState
        {
            Hand = Tiles.Parse("119m2467p13579s55z"),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand | Observed.Discards,
            WallRemaining = 46,
        };

        Assert.False(new DiscardAdvisor().Recommend(table).Folding);
    }
}

public class PlacementTests
{
    private static TableState WithScores(params int[] scores) => new()
    {
        Hand = Tiles.Parse("123456789m1122p3z"),
        Seats = Build.EmptySeats(),
        Observations = Observed.OwnHand | Observed.SafetyReady,
        WallRemaining = 40,
        Scores = scores,
    };

    [Theory]
    [InlineData(1, 40000, 20000, 20000, 20000)]
    [InlineData(4, 10000, 30000, 30000, 30000)]
    [InlineData(2, 25000, 30000, 20000, 20000)]
    public void RankReflectsTheStandings(int expected, int us, int b, int c, int d)
    {
        Assert.Equal(expected, PlacementAdjuster.RankOf(new[] { us, b, c, d }));
    }

    [Fact]
    public void LeadingPlaysTighterThanTrailing()
    {
        var adjuster = new PlacementAdjuster();
        var leading = adjuster.ComputeFor(WithScores(40000, 20000, 20000, 20000));
        var trailing = adjuster.ComputeFor(WithScores(10000, 30000, 30000, 30000));

        Assert.True(leading.Danger > trailing.Danger, "a leader should fear deal-ins more");
        Assert.True(trailing.HandValue > leading.HandValue, "last place should chase value");
    }

    [Fact]
    public void CommandingLeadLocksDown()
    {
        var adjuster = new PlacementAdjuster();
        var normal = adjuster.ComputeFor(WithScores(28000, 25000, 24000, 23000));
        var huge = adjuster.ComputeFor(WithScores(45000, 20000, 18000, 17000));

        Assert.True(huge.Danger > normal.Danger);
        Assert.True(huge.HandValue < normal.HandValue);
    }

    [Fact]
    public void GapIsMeasuredToTheNextPlayerDown()
    {
        Assert.Equal(15000, PlacementAdjuster.GapToSecond(new[] { 40000, 25000, 20000, 15000 }));
    }

    [Fact]
    public void NoScoresMeansNoAdjustment()
    {
        var adjuster = new PlacementAdjuster();
        var table = new TableState
        {
            Hand = Tiles.Parse("123456789m1122p3z"),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand,
        };

        Assert.Equal(PlacementStance.Neutral, adjuster.ComputeFor(table));
    }
}

public class ThreatModelTests
{
    private static TableState Late(
        int discardsEach, bool riichiOnRight = false, int melds = 0, bool meldsKnown = true)
    {
        var seats = Build.EmptySeats();
        for (int i = 0; i < 4; i++)
        {
            var pond = new List<int>();
            for (int d = 0; d < discardsEach; d++)
                pond.Add(d % 34);
            seats[i].Discards = pond;
        }

        for (int m = 0; m < melds * 3; m++)
            seats[1].MeldTiles.Add(0);

        seats[1].Riichi = riichiOnRight;

        var observations = Observed.OwnHand | Observed.Discards;
        if (meldsKnown)
            observations |= Observed.OpponentMelds;

        return new TableState
        {
            Hand = Tiles.Parse("123456789m1122p3z"),
            Seats = seats,
            Observations = observations,
            WallRemaining = 70 - (discardsEach * 4),
        };
    }

    [Fact]
    public void RiichiIsCertainlyReady()
    {
        var threat = new ThreatModel().Evaluate(Late(6, riichiOnRight: true))
            .Single(t => t.Seat == 1);
        Assert.Equal(1.0, threat.TenpaiProbability);
        Assert.True(threat.ExpectedHandValue > 4000, "riichi hands cost more to feed");
    }

    [Fact]
    public void ReadinessRisesAsTheHandGoesOn()
    {
        var early = new ThreatModel().Evaluate(Late(2)).Max(t => t.TenpaiProbability);
        var late = new ThreatModel().Evaluate(Late(14)).Max(t => t.TenpaiProbability);
        Assert.True(late > early, "more discards and fewer tiles left means more likely ready");
    }

    [Fact]
    public void CallingMeldsLooksMoreThreatening()
    {
        var quiet = new ThreatModel().Evaluate(Late(8)).Single(t => t.Seat == 1);
        var calling = new ThreatModel().Evaluate(Late(8, melds: 2)).Single(t => t.Seat == 1);
        Assert.True(calling.TenpaiProbability > quiet.TenpaiProbability);
    }

    [Fact]
    public void UnknownMeldsAreNotTreatedAsZero()
    {
        // Without meld data, the quiet-hand rate is a lower bound. The old model
        // used it verbatim, which made every unread call invisible.
        var known = new ThreatModel()
            .Evaluate(Late(8))
            .Single(t => t.Seat == 1)
            .TenpaiProbability;
        var unknown = new ThreatModel()
            .Evaluate(Late(8, meldsKnown: false))
            .Single(t => t.Seat == 1)
            .TenpaiProbability;

        Assert.True(unknown > known);
    }

    [Fact]
    public void SilentOpponentsStillCarryRisk()
    {
        // Nobody has declared riichi, but late in the hand the danger is not zero.
        var table = Late(14);
        Assert.DoesNotContain(table.Seats, s => s.Riichi);

        // Not 5m: the helper fills every pond with tiles 0-13, so 5m is genbutsu
        // against everyone and correctly carries no risk at all.
        var risk = Safety.EvaluateAgainstTable(Build.Tile("9s"), table);
        Assert.True(risk.DealInRate > 0, "a silent tenpai is still a tenpai");
        Assert.True(risk.ExpectedCost > 0);
    }

    [Fact]
    public void GenbutsuStaysSafeAgainstEveryone()
    {
        var table = Late(14);
        int everyonesDiscard = table.Seats[1].Discards[0];
        var risk = Safety.EvaluateAgainstTable(everyonesDiscard, table);
        Assert.Equal(0.0, risk.DealInRate);
    }
}

public class KuikaeTests
{
    [Fact]
    public void ClaimedTileCanNeverBeThrownBack()
    {
        var forbidden = Kuikae.Forbidden(CallKind.Pon, Build.Tile("5m"), []);
        Assert.Equal([Build.Tile("5m")], forbidden);
    }

    [Fact]
    public void ChiFromAboveBarsTheTileBeyondTheRun()
    {
        // The case from play: claimed 5 Char holding 6 and 7. The client greyed
        // out 5 Char and 8 Char, and 6-7-8 is why.
        var forbidden = Kuikae.Forbidden(
            CallKind.Chi, Build.Tile("5m"), [Build.Tile("6m"), Build.Tile("7m")]);

        Assert.Equal(2, forbidden.Count);
        Assert.Contains(Build.Tile("5m"), forbidden);
        Assert.Contains(Build.Tile("8m"), forbidden);
    }

    [Fact]
    public void ChiFromBelowBarsTheMirrorTile()
    {
        // Claimed 7 holding 5 and 6, so 4-5-6 is the same shape.
        var forbidden = Kuikae.Forbidden(
            CallKind.Chi, Build.Tile("7p"), [Build.Tile("5p"), Build.Tile("6p")]);

        Assert.Contains(Build.Tile("7p"), forbidden);
        Assert.Contains(Build.Tile("4p"), forbidden);
    }

    [Fact]
    public void ChiInTheMiddleHasNoSwap()
    {
        // Claimed 5 holding 4 and 6: there is no alternative run, so only the
        // claimed tile is barred.
        var forbidden = Kuikae.Forbidden(
            CallKind.Chi, Build.Tile("5s"), [Build.Tile("4s"), Build.Tile("6s")]);

        Assert.Single(forbidden);
        Assert.Contains(Build.Tile("5s"), forbidden);
    }

    [Fact]
    public void RunsAtTheEdgeDoNotWrap()
    {
        // Claimed 7 holding 8 and 9: there is no 10, so nothing extra is barred.
        var forbidden = Kuikae.Forbidden(
            CallKind.Chi, Build.Tile("7m"), [Build.Tile("8m"), Build.Tile("9m")]);
        Assert.Single(forbidden);
    }

    [Fact]
    public void AdjacentPairBarsBothEnds()
    {
        // Contributed 6 and 7 Char: whichever end was claimed, the other end
        // makes the same run, so both are barred. Matches what the client greyed.
        var forbidden = Kuikae.ForbiddenFromHandDelta([Build.Tile("6m"), Build.Tile("7m")]);
        Assert.Equal(2, forbidden.Count);
        Assert.Contains(Build.Tile("5m"), forbidden);
        Assert.Contains(Build.Tile("8m"), forbidden);
    }

    [Fact]
    public void GappedPairBarsOnlyTheFiller()
    {
        var forbidden = Kuikae.ForbiddenFromHandDelta([Build.Tile("3s"), Build.Tile("5s")]);
        Assert.Single(forbidden);
        Assert.Contains(Build.Tile("4s"), forbidden);
    }

    [Fact]
    public void SuitIsTakenFromTheTilesThatLeftTheHand()
    {
        // The old code took the suit from a board reading and barred Dots for a
        // call made in Characters. The hand delta cannot get this wrong.
        var forbidden = Kuikae.ForbiddenFromHandDelta([Build.Tile("3m"), Build.Tile("4m")]);
        Assert.All(forbidden, t => Assert.Equal("Characters", Tiles.SuitName(t)));
        Assert.Contains(Build.Tile("2m"), forbidden);
        Assert.Contains(Build.Tile("5m"), forbidden);
    }

    [Fact]
    public void PonBarsOnlyTheTileItself()
    {
        var forbidden = Kuikae.ForbiddenFromHandDelta([Build.Tile("7z"), Build.Tile("7z")]);
        Assert.Single(forbidden);
        Assert.Contains(Build.Tile("7z"), forbidden);
    }

    [Fact]
    public void EdgeRunsDoNotWrapAroundTheSuit()
    {
        var forbidden = Kuikae.ForbiddenFromHandDelta([Build.Tile("8p"), Build.Tile("9p")]);
        Assert.Single(forbidden);
        Assert.Contains(Build.Tile("7p"), forbidden);
    }

    [Fact]
    public void AdvisorNeverSuggestsABarredTile()
    {
        // 11 tiles with one meld called, and the best cut barred by kuikae.
        var table = new TableState
        {
            Hand = Tiles.Parse("2258m3p244456s"),
            CalledMelds = 1,
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand,
            WallRemaining = 13,
            ForbiddenDiscards = [Build.Tile("8m"), Build.Tile("5m")],
        };

        var advice = new DiscardAdvisor().Recommend(table);

        Assert.NotNull(advice.Best);
        Assert.DoesNotContain(advice.Options, o => o.Tile == Build.Tile("8m"));
        Assert.DoesNotContain(advice.Options, o => o.Tile == Build.Tile("5m"));
    }
}

public class DiscardAdvisorTests
{
    // 123m 456m 789m + 1122p + 3z. Cutting 3z is the only tenpai.
    private const string TenpaiOnCut3z = "123456789m1122p3z";

    [Fact]
    public void DropsTheIsolatedHonourWithNoThreat()
    {
        var table = new TableState
        {
            Hand = Tiles.Parse(TenpaiOnCut3z),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand | Observed.SafetyReady,
            WallRemaining = 60,
        };

        var advice = new DiscardAdvisor().Recommend(table);

        Assert.NotNull(advice.Best);
        Assert.Equal("3z", Tiles.Name(advice.Best!.Tile));
        Assert.Equal(0, advice.Best.ResultingShanten);
        Assert.Equal(4, advice.Best.Ukeire); // shanpon on 1p/2p, two of each live
        Assert.False(advice.Folding);
    }

    [Fact]
    public void FoldingPicksTheSafestTileNotTheFastestOne()
    {
        var seats = Build.EmptySeats();
        seats[1].Discards = Build.Pond("3z");
        seats[1].Riichi = true;
        seats[1].RiichiDiscardIndex = 0;

        var table = new TableState
        {
            // Three away with a riichi out: nothing here is worth racing for, and
            // 3z is already in their pond so it cannot deal in.
            Hand = Tiles.Parse("119m2467p13579s55z"),
            Seats = seats,
            Observations = Observed.OwnHand | Observed.SafetyReady,
            WallRemaining = 20,
        };

        // Folding is decided by expected value against a real threat now, not by
        // a tunable threshold, so the hand has to actually be hopeless.
        var advice = new DiscardAdvisor().Recommend(table);

        Assert.True(advice.Folding);
        Assert.Contains("Fold", advice.Headline);
    }

    [Fact]
    public void GoodTenpaiPushesEvenAgainstRiichi()
    {
        var seats = Build.EmptySeats();
        seats[1].Discards = Build.Pond("3z");
        seats[1].Riichi = true;
        seats[1].RiichiDiscardIndex = 0;

        var table = new TableState
        {
            Hand = Tiles.Parse(TenpaiOnCut3z),
            Seats = seats,
            Observations = Observed.OwnHand | Observed.SafetyReady,
            WallRemaining = 60,
        };

        var advice = new DiscardAdvisor().Recommend(table);
        Assert.False(advice.Folding, "a ready hand should take the race");
    }

    [Fact]
    public void RefusesMidTransitionHands()
    {
        var table = new TableState
        {
            Hand = Tiles.Parse("123456789m"), // not a legal shape for zero melds
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand,
        };

        var ex = Assert.Throws<ArgumentException>(() => new DiscardAdvisor().Recommend(table));
        Assert.Contains("mid-transition", ex.Message);
    }

    [Fact]
    public void AcceptsCalledMeldHandShapes()
    {
        var table = new TableState
        {
            Hand = Tiles.Parse("123456789m12p"), // eleven tiles, one meld called
            CalledMelds = 1,
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand | Observed.SafetyReady,
            WallRemaining = 50,
        };

        var advice = new DiscardAdvisor().Recommend(table);
        Assert.NotNull(advice.Best);
    }

    [Fact]
    public void SaysSoWhenSafetyDataIsMissing()
    {
        var table = new TableState
        {
            Hand = Tiles.Parse(TenpaiOnCut3z),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand, // no discard, meld or riichi feed
            WallRemaining = 60,
        };

        var advice = new DiscardAdvisor().Recommend(table);
        Assert.Contains("efficiency only", advice.Headline);
    }

    [Theory]
    [InlineData(0, 8, 60, 1, 8, 60)]   // tenpai beats one-shanten
    [InlineData(1, 16, 60, 1, 4, 60)]  // wide ukeire beats narrow
    [InlineData(1, 8, 60, 1, 8, 12)]   // more wall left beats less
    public void WinProbabilityIsMonotone(
        int shantenA, int ukeireA, int wallA, int shantenB, int ukeireB, int wallB)
    {
        Assert.True(
            DiscardAdvisor.WinProbability(shantenA, ukeireA, wallA) >
            DiscardAdvisor.WinProbability(shantenB, ukeireB, wallB));
    }

    [Theory]
    [InlineData(0, 8, 8)]     // already tenpai: certain
    [InlineData(1, 20, 40)]   // one away with a wide wait and time
    public void TenpaiByDrawIsReachable(int shanten, int ukeire, int wall)
    {
        Assert.True(DiscardAdvisor.TenpaiByDrawProbability(shanten, ukeire, wall) > 0.3);
    }

    [Fact]
    public void AlreadyTenpaiIsCertainAtTheDraw()
    {
        Assert.Equal(1.0, DiscardAdvisor.TenpaiByDrawProbability(0, 4, 8));
        Assert.Equal(1.0, DiscardAdvisor.TenpaiByDrawProbability(-1, 0, 8));
    }

    [Fact]
    public void TenpaiIsStillWorthChasingWhenWinningIsNot()
    {
        // Two away with two draws left: the hand cannot complete, but it can still
        // get ready in time, and readiness pays at an exhaustive draw.
        Assert.Equal(0.0, DiscardAdvisor.WinProbability(2, 20, 8));
        Assert.True(DiscardAdvisor.TenpaiByDrawProbability(2, 20, 8) > 0);
    }

    [Fact]
    public void EndgameRanksTowardReadiness()
    {
        // 123m 456m 789m 1122p: cutting a pin leaves a one-away shape, and with
        // the wall nearly gone that readiness is what the score should reward.
        var table = new TableState
        {
            Hand = Tiles.Parse(TenpaiOnCut3z),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand | Observed.SafetyReady,
            WallRemaining = 8,
        };

        var advice = new DiscardAdvisor().Recommend(table);
        Assert.Equal("3z", Tiles.Name(advice.Best!.Tile));
        Assert.True(advice.Best.Score > 0, "tenpai before the draw must carry value");
    }

    [Fact]
    public void UnreachableHandsScoreZero()
    {
        Assert.Equal(0.0, DiscardAdvisor.WinProbability(shanten: 5, ukeire: 8, wallRemaining: 8));
        Assert.Equal(0.0, DiscardAdvisor.WinProbability(shanten: 1, ukeire: 0, wallRemaining: 60));
    }
}
