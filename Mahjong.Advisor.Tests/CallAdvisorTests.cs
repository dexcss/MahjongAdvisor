using Xunit;

namespace Mahjong.Advisor.Tests;

public class CallAdvisorTests
{
    private static TableState Hand(string notation, int calledMelds = 0) => new()
    {
        Hand = Tiles.Parse(notation),
        CalledMelds = calledMelds,
        Seats = Build.EmptySeats(),
        Observations = Observed.OwnHand | Observed.SafetyReady,
        WallRemaining = 60,
    };

    [Fact]
    public void WinsAreAlwaysTakenFirst()
    {
        var table = Hand("123456789m123p1s");
        var advice = new CallAdvisor().Recommend(
            table, Build.Tile("1s"), [CallKind.Ron, CallKind.Pass]);

        Assert.Equal(CallKind.Ron, advice.Best!.Kind);
        Assert.Contains("Ron", advice.Headline);
    }

    [Fact]
    public void PonIsOfferedOnlyWithTwoCopiesInHand()
    {
        // Only one 5z held, so pon is not a legal option to rank.
        var table = Hand("123456789m12p5z");
        var advice = new CallAdvisor().Recommend(
            table, Build.Tile("5z"), [CallKind.Pon, CallKind.Pass]);

        Assert.DoesNotContain(advice.Options, o => o.Kind == CallKind.Pon);
    }

    [Fact]
    public void PonOnDragonsIsWorthOpeningFor()
    {
        // Not 234567m234p55z99s: that hand is already closed tenpai, so passing
        // and keeping riichi available is correct and the old expectation was
        // wrong. This one is three away and the pon advances it.
        var table = Hand("7m89p45578s45577z");
        var advice = new CallAdvisor().Recommend(
            table, Build.Tile("5z"), [CallKind.Pon, CallKind.Pass]);

        var pon = Assert.Single(advice.Options, o => o.Kind == CallKind.Pon);
        Assert.False(pon.YakuAtRisk);
        Assert.Equal(CallKind.Pon, advice.Best!.Kind);
    }

    [Fact]
    public void OpeningWithNoYakuIsPenalised()
    {
        // A terminal-bearing mixed-suit hand with no dragons: chi here opens the
        // hand into nothing, so passing should win even though chi is legal.
        var table = Hand("19m234p5678s123z");
        var advice = new CallAdvisor().Recommend(
            table, Build.Tile("5p"), [CallKind.Chi, CallKind.Pass]);

        var chi = advice.Options.FirstOrDefault(o => o.Kind == CallKind.Chi);
        if (chi is not null)
        {
            Assert.True(chi.YakuAtRisk);
            Assert.NotEqual(CallKind.Chi, advice.Best!.Kind);
        }
    }

    [Fact]
    public void ChiFindsEveryRunShape()
    {
        // Holding 3p4p5p6p, claiming 5p can form 34+5, 4+5+6 and 5+67 style shapes.
        var table = Hand("123456789m3456p");
        var advice = new CallAdvisor().Recommend(
            table, Build.Tile("5p"), [CallKind.Chi, CallKind.Pass]);

        var chis = advice.Options.Where(o => o.Kind == CallKind.Chi).ToList();
        Assert.True(chis.Count >= 2, $"expected multiple chi shapes, got {chis.Count}");
        Assert.All(chis, c => Assert.Equal(2, c.UsedFromHand.Count));
    }

    [Fact]
    public void ChiIsNeverOfferedOnHonours()
    {
        var table = Hand("123456789m123p1z");
        var advice = new CallAdvisor().Recommend(
            table, Build.Tile("1z"), [CallKind.Chi, CallKind.Pass]);

        Assert.DoesNotContain(advice.Options, o => o.Kind == CallKind.Chi);
    }

    [Fact]
    public void RiichiOfferedOnlyOnClosedTenpai()
    {
        var tenpai = Hand("123456789m123p1s");
        var closed = new CallAdvisor().Recommend(tenpai, Build.Tile("1s"), [CallKind.Riichi, CallKind.Pass]);
        Assert.Contains(closed.Options, o => o.Kind == CallKind.Riichi);

        // Same shape but with a meld already called: riichi is not legal.
        var open = new TableState
        {
            Hand = Tiles.Parse("123456789m12p"),
            CalledMelds = 1,
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand | Observed.SafetyReady,
            WallRemaining = 60,
        };
        var openAdvice = new CallAdvisor().Recommend(open, Build.Tile("3p"), [CallKind.Riichi, CallKind.Pass]);
        Assert.DoesNotContain(openAdvice.Options, o => o.Kind == CallKind.Riichi);
    }

    [Fact]
    public void RiichiNeedsNoClaimedTile()
    {
        // Real hand from play: 345m 78p 6678s 44777z, 14 tiles on our own turn.
        // Cutting 6 Bamboo leaves closed tenpai on 6/9 Dots, so riichi applies -
        // and no tile is being claimed from anyone.
        var table = new TableState
        {
            Hand = Tiles.Parse("345m78p6678s44777z"),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand | Observed.SafetyReady,
            WallRemaining = 57,
        };

        var advice = new CallAdvisor().Recommend(table, claimedTile: -1, [CallKind.Riichi, CallKind.Pass]);

        Assert.Equal(CallKind.Riichi, advice.Best!.Kind);
        Assert.Equal(0, advice.Best.ResultingShanten);
        Assert.Equal(8, advice.Best.Ukeire); // 6 Dots x4 and 9 Dots x4
    }

    [Fact]
    public void RiichiIsNotOfferedWhenNotTenpai()
    {
        // Not 19m19p19s1234567z: that is thirteen-orphans tenpai, so riichi was
        // correctly offered and the test was simply wrong about the hand.
        var table = new TableState
        {
            Hand = Tiles.Parse("19m2467p13579s55z"),
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand,
            WallRemaining = 50,
        };

        var advice = new CallAdvisor().Recommend(table, claimedTile: -1, [CallKind.Riichi, CallKind.Pass]);
        Assert.DoesNotContain(advice.Options, o => o.Kind == CallKind.Riichi);
        Assert.Equal(CallKind.Pass, advice.Best!.Kind);
    }

    [Fact]
    public void NoClaimedTileMeansNoClaimingCalls()
    {
        var table = Hand("234567m234p55z99s");
        var advice = new CallAdvisor().Recommend(
            table, claimedTile: -1, [CallKind.Pon, CallKind.Chi, CallKind.Pass]);

        Assert.DoesNotContain(advice.Options, o => o.Kind is CallKind.Pon or CallKind.Chi);
        Assert.Equal(CallKind.Pass, advice.Best!.Kind);
    }

    [Fact]
    public void RonIsTakenEvenWhenTheTileIsNotIdentified()
    {
        // From play: the client offered Ron, the claimed tile did not decode, and
        // the advisor said nothing at all. A win never needs identifying.
        var table = Hand("123456789m123p1s");
        var advice = new CallAdvisor().Recommend(table, claimedTile: -1, [CallKind.Ron, CallKind.Pass]);

        Assert.Equal(CallKind.Ron, advice.Best!.Kind);
    }

    [Fact]
    public void SelfKanIsDeclinedAgainstRiichi()
    {
        // A kan reveals another dora indicator for everyone, including the player
        // who has already declared.
        var seats = Build.EmptySeats();
        seats[3].Riichi = true;
        seats[3].Discards = Build.Pond("1z2z3z");

        var table = new TableState
        {
            Hand = Tiles.Parse("79m5p1146799s7z"),
            CalledMelds = 1,
            Seats = seats,
            Observations = Observed.OwnHand | Observed.Discards | Observed.OpponentRiichi,
            WallRemaining = 39,
        };

        var advice = new CallAdvisor().Recommend(table, claimedTile: -1, [CallKind.Kan, CallKind.Pass]);
        Assert.Equal(CallKind.Pass, advice.Best!.Kind);
    }

    [Fact]
    public void SelfKanIsTakenWhenNobodyIsClose()
    {
        var table = new TableState
        {
            Hand = Tiles.Parse("79m5p1146799s7z"),
            CalledMelds = 1,
            Seats = Build.EmptySeats(),
            Observations = Observed.OwnHand | Observed.Discards,
            WallRemaining = 60,
        };

        var advice = new CallAdvisor().Recommend(table, claimedTile: -1, [CallKind.Kan, CallKind.Pass]);
        Assert.Equal(CallKind.Kan, advice.Best!.Kind);
    }

    [Fact]
    public void PassIsAlwaysAnOption()
    {
        var table = Hand("123456789m12p5z");
        var advice = new CallAdvisor().Recommend(table, Build.Tile("9p"), [CallKind.Pass]);
        Assert.Single(advice.Options);
        Assert.Equal(CallKind.Pass, advice.Best!.Kind);
    }

    [Theory]
    [InlineData("234567m234p55z", "5z", CallKind.Pon, true)]    // dragon triplet is a yaku
    [InlineData("234567m234p556s", "6s", CallKind.Chi, true)]   // all simples -> tanyao
    [InlineData("234567m22345s", "6s", CallKind.Chi, true)]     // still all simples
    public void YakuPathDetection(string hand, string claimed, CallKind kind, bool expected)
    {
        var concealed = Tiles.Parse(hand);
        Assert.Equal(expected, CallAdvisor.HasOpenYakuPath(concealed, Build.Tile(claimed), kind));
    }

    [Fact]
    public void TerminalsBreakTanyaoPath()
    {
        // A terminal in hand rules out all-simples, and mixed suits rule out honitsu.
        var concealed = Tiles.Parse("123m456p789s22z");
        Assert.False(CallAdvisor.HasOpenYakuPath(concealed, Build.Tile("3m"), CallKind.Chi));
    }

    [Fact]
    public void PassDoesNotInventAThirdCopy()
    {
        // A pair of dragons is not a dragon triplet. The old default of one extra
        // claimed copy turned this pair into a yaku and made a closed hand look
        // legal to open.
        var concealed = Tiles.Parse("234567m234p55z");
        Assert.False(CallAdvisor.HasOpenYakuPath(concealed, Build.Tile("5z"), CallKind.Pass));
    }

    [Fact]
    public void SingleSuitCountsAsAYakuPath()
    {
        var concealed = Tiles.Parse("112233445m11z");
        Assert.True(CallAdvisor.HasOpenYakuPath(concealed, Build.Tile("6m"), CallKind.Chi));
    }
}
