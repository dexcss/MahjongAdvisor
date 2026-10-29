using Xunit;

namespace Mahjong.Advisor.Tests;

public class RolloutSearchTests
{
    private const Observed FullObservations =
        Observed.OwnHand | Observed.Discards | Observed.OpponentMelds
        | Observed.OpponentRiichi | Observed.RiichiTurn | Observed.DoraIndicators;

    /// <summary>Tenpai hand, quiet table: the search runs and may re-rank.</summary>
    private static TableState PushTable()
    {
        var seats = Build.EmptySeats();
        seats[1].Discards = Build.Pond("2m8m");
        seats[2].Discards = Build.Pond("2p8p");
        seats[3].Discards = Build.Pond("2s8s");

        return new TableState
        {
            Hand = Tiles.Parse("123456789m12345p"),
            CalledMelds = 0,
            Seats = seats,
            Observations = FullObservations,
            WallRemaining = 40,
            Scores = [25000, 25000, 25000, 25000],
            DoraIndicators = [Build.Tile("1s")],
        };
    }

    /// <summary>Two declared riichi against a hand that is not tenpai: the advisor folds.</summary>
    private static TableState FoldTable()
    {
        var seats = Build.EmptySeats();
        seats[1].Riichi = true;
        seats[1].Discards = Build.Pond("1234567m");
        seats[2].Riichi = true;
        seats[2].Discards = Build.Pond("1234567p");
        seats[3].Discards = Build.Pond("1234567s");

        return new TableState
        {
            Hand = Tiles.Parse("19m19p19s3456m2356p"),
            CalledMelds = 0,
            Seats = seats,
            Observations = FullObservations,
            WallRemaining = 12,
            Scores = [25000, 25000, 25000, 25000],
        };
    }

    private static RolloutSearch Search() => new()
    {
        Base = new DiscardAdvisor(),
        Rollouts = 2,
        Candidates = 3,
        Seed = 4242,
    };

    /// <summary>
    /// Advisor and search sharing one advisor instance, the way the plugin
    /// wires them. Pass-through tests rely on the advisor's memo returning the
    /// same Advice object for the same table.
    /// </summary>
    private static (DiscardAdvisor Advisor, RolloutSearch Search) Pair()
    {
        var advisor = new DiscardAdvisor();
        return (advisor, new RolloutSearch
        {
            Base = advisor,
            Rollouts = 2,
            Candidates = 3,
            Seed = 4242,
        });
    }

    [Fact]
    public void BestDiscard_IsAlwaysOneOfTheAdvisorCandidates()
    {
        var table = PushTable();
        var baseAdvice = new DiscardAdvisor().Recommend(table);
        var searched = Search().Recommend(table);

        Assert.NotNull(searched.Best);
        Assert.Contains(searched.Best!.Tile, baseAdvice.Options.Take(3).Select(o => o.Tile));
    }

    [Fact]
    public void Search_KeepsEveryAdvisorOption()
    {
        var table = PushTable();
        var baseAdvice = new DiscardAdvisor().Recommend(table);
        var searched = Search().Recommend(table);

        Assert.Equal(baseAdvice.Options.Count, searched.Options.Count);
        Assert.Equal(
            baseAdvice.Options.Skip(3).Select(o => o.Tile),
            searched.Options.Skip(3).Select(o => o.Tile));
    }

    [Fact]
    public void SameTable_SameSeed_ProducesTheSameAnswer()
    {
        var table = PushTable();

        var first = Search().Recommend(table);
        var second = Search().Recommend(table);

        Assert.Equal(first.Best!.Tile, second.Best!.Tile);
        Assert.Equal(first.Options.Select(o => o.Tile), second.Options.Select(o => o.Tile));
    }

    [Fact]
    public void FoldedAdvice_PassesThroughUnchanged()
    {
        var table = FoldTable();
        var (advisor, search) = Pair();
        var baseAdvice = advisor.Recommend(table);

        // The fixture must actually fold, otherwise the test proves nothing.
        Assert.True(baseAdvice.Folding);

        var searched = search.Recommend(table);
        Assert.Same(baseAdvice, searched);
    }

    [Fact]
    public void RiichiLockedHand_PassesThrough()
    {
        // SelfRiichi forces the drawn tile; the advisor returns one option.
        var locked = new TableState
        {
            Hand = Tiles.Parse("123456789m12345p"),
            CalledMelds = 0,
            Seats = Build.EmptySeats(),
            Observations = FullObservations,
            WallRemaining = 40,
            Scores = [25000, 25000, 25000, 25000],
            SelfRiichi = true,
            DrawnTile = Build.Tile("9m"),
        };

        var (advisor, search) = Pair();
        var baseAdvice = advisor.Recommend(locked);
        var searched = search.Recommend(locked);

        Assert.Same(baseAdvice, searched);
        Assert.Single(baseAdvice.Options);
    }

    [Fact]
    public void UnreadScores_PassThrough()
    {
        var table = PushTable();
        var noScores = new TableState
        {
            Hand = table.Hand,
            CalledMelds = table.CalledMelds,
            Seats = table.Seats,
            Observations = table.Observations,
            WallRemaining = table.WallRemaining,
            DoraIndicators = table.DoraIndicators,
            Scores = [],
        };

        var (advisor, search) = Pair();
        var baseAdvice = advisor.Recommend(noScores);
        var searched = search.Recommend(noScores);

        Assert.Same(baseAdvice, searched);
    }

    [Fact]
    public void FarFromTenpai_PassesThrough()
    {
        // Seven isolated tiles: every cut stays at least two away from tenpai,
        // which is below the search's interest threshold.
        var seats = Build.EmptySeats();
        seats[1].Discards = Build.Pond("1234567m");

        var table = new TableState
        {
            // Fourteen isolated-or-worse tiles: no wait, no kokushi run, no
            // chiitoitsu finish. Every cut stays well away from tenpai.
            Hand = Tiles.Parse("19m19p19s3456m2356p"),
            CalledMelds = 0,
            Seats = seats,
            Observations = FullObservations,
            WallRemaining = 40,
            Scores = [25000, 25000, 25000, 25000],
        };

        var (advisor, search) = Pair();
        var baseAdvice = advisor.Recommend(table);
        Assert.True(baseAdvice.Best!.ResultingShanten > 1);

        var searched = search.Recommend(table);
        Assert.Same(baseAdvice, searched);
    }
}
