using System.Diagnostics;
using Mahjong.Advisor;
using Mahjong.Advisor.Simulator;

int hands = args.Length > 0 && int.TryParse(args[0], out int n) ? n : 1000;
int seed = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 12345;

if (args.Length > 2 && args[2] == "matchpair")
{
    var aggressive = new DiscardAdvisor
    {
        AverageDealInCost = 4000,
        AverageWinValue = 1800,
    };

    var watch = Stopwatch.StartNew();
    var comparison = MatchSimulator.Compare(new DiscardAdvisor(), aggressive, hands, seed);
    Console.WriteLine(comparison);
    Console.WriteLine($"[{watch.Elapsed.TotalSeconds:0}s]");
    return;
}

if (args.Length > 2 && args[2] == "rolloutpair")
{
    // Phase C A/B: the same advisor plays both arms; arm B's discard choices
    // are re-ranked by the rollout search built on top of it. Identical seeds,
    // so the only difference between the arms is the search itself.
    var advisor = new DiscardAdvisor
    {
        AverageDealInCost = 4000,
        AverageWinValue = 1800,
    };

    int rollouts = args.Length > 3 && int.TryParse(args[3], out int ro) ? ro : 8;
    int candidates = args.Length > 4 && int.TryParse(args[4], out int ca) ? ca : 3;

    var search = new RolloutSearch { Base = advisor, Rollouts = rollouts, Candidates = candidates };

    var watch = Stopwatch.StartNew();
    var comparison = MatchSimulator.Compare(
        advisor, advisor,
        policyA: null,
        policyB: table => search.Recommend(table),
        hands, seed);

    Console.WriteLine($"rolloutpair: {rollouts} rollouts x {candidates} candidates per searched decision");
    Console.WriteLine("A = advisor only            B = advisor + rollout search");
    Console.WriteLine(comparison);
    Console.WriteLine($"[{watch.Elapsed.TotalMinutes:0}m {watch.Elapsed.Seconds:00}s]");
    return;
}

if (args.Length > 2 && args[2] == "matchtune")
{
    (string Name, DiscardAdvisor Advisor)[] configs =
    [
        ("baseline", new DiscardAdvisor()),
        ("deal-in 4000", new DiscardAdvisor { AverageDealInCost = 4000 }),
        ("deal-in 7000", new DiscardAdvisor { AverageDealInCost = 7000 }),
        ("win 1000", new DiscardAdvisor { AverageWinValue = 1000 }),
        ("win 1800", new DiscardAdvisor { AverageWinValue = 1800 }),
        ("noten 1000", new DiscardAdvisor { NotenPenaltyValue = 1000 }),
        ("noten 2500", new DiscardAdvisor { NotenPenaltyValue = 2500 }),
        ("late wall 18", new DiscardAdvisor { LateWallThreshold = 18 }),
    ];

    foreach (var (name, advisor) in configs)
    {
        var watch = Stopwatch.StartNew();
        var summary = MatchSimulator.Run(advisor, hands, seed);
        Console.WriteLine($"{name,-14} {summary}  [{watch.Elapsed.TotalSeconds:0}s]");
    }

    return;
}

if (args.Length > 2 && args[2] == "matchplace")
{
    var neutral = new PlacementAdjuster
    {
        Leading = PlacementStance.Neutral,
        CommandingLead = PlacementStance.Neutral,
        Trailing = PlacementStance.Neutral,
    };

    (string Name, DiscardAdvisor Advisor)[] configs =
    [
        ("placement on", new DiscardAdvisor()),
        ("neutral", new DiscardAdvisor { Placement = neutral }),
        ("mild chase", new DiscardAdvisor
        {
            Placement = new PlacementAdjuster
            {
                Trailing = new PlacementStance(Danger: 0.85, Speed: 1.05, HandValue: 1.15),
            },
        }),
    ];

    foreach (var (name, advisor) in configs)
    {
        var watch = Stopwatch.StartNew();
        var summary = MatchSimulator.Run(advisor, hands, seed);
        Console.WriteLine($"{name,-12} {summary}  [{watch.Elapsed.TotalSeconds:0}s]");
    }

    return;
}

if (args.Length > 2 && args[2] == "matchsweep")
{
    (string Name, DiscardAdvisor Advisor)[] configs =
    [
        ("riichi 5000", new DiscardAdvisor()),
        ("riichi 8000", new DiscardAdvisor
        {
            Threats = new ThreatModel { RiichiHandValueBonus = 4000 },
        }),
        ("riichi 12000", new DiscardAdvisor
        {
            Threats = new ThreatModel { RiichiHandValueBonus = 8000 },
        }),
    ];

    foreach (var (name, advisor) in configs)
    {
        var watch = Stopwatch.StartNew();
        var summary = MatchSimulator.Run(advisor, hands, seed);
        Console.WriteLine($"{name,-12} {summary}  [{watch.Elapsed.TotalSeconds:0}s]");
    }

    return;
}

if (args.Length > 2 && args[2] == "match")
{
    var watch = Stopwatch.StartNew();
    var summary = MatchSimulator.Run(new DiscardAdvisor(), hands, seed);
    Console.WriteLine(summary);
    Console.WriteLine($"[{watch.Elapsed.TotalSeconds:0}s]");
    return;
}

if (args.Length > 2 && args[2] == "riichi")
{
    Sweep(
        "What a declared riichi hand is assumed to cost you:",
        "riichi value",
        ((int[])[1000, 4000, 8000]).Select(v =>
            ($"{4000 + v}", new DiscardAdvisor
            {
                Threats = new ThreatModel { RiichiHandValueBonus = v },
            })),
        hands, seed);
    return;
}

if (args.Length > 2 && args[2] == "rail")
{
    Sweep(
        "Double-riichi fold rail:",
        "rail",
        ((bool[])[false, true]).Select(v =>
            (v ? "on" : "off", new DiscardAdvisor { TwoRiichiFold = v })),
        hands, seed);
    return;
}

if (args.Length > 2 && args[2] == "exact")
{
    Sweep(
        "Exact ready-hand value only:",
        "value",
        [("exact", new DiscardAdvisor { ExactTenpaiValue = true })],
        hands, seed);
    return;
}

if (args.Length > 2 && args[2] == "value")
{
    // Isolated A/B of the exact ready-hand value model. Both rows use the same
    // exact outcome scoring, so only the wait valuation differs.
    Sweep(
        "Exact ready-hand value vs the old dora multiplier:",
        "value",
        ((bool[])[false, true]).Select(v =>
            (v ? "exact" : "heuristic", new DiscardAdvisor { ExactTenpaiValue = v })),
        hands, seed);
    return;
}

Console.WriteLine($"Simulating {hands} hands per configuration (seed {seed}).");
Console.WriteLine("Closed hands only, no calls. Every configuration plays the same deals.");
Console.WriteLine();

static void Sweep(string title, string label,
    IEnumerable<(string Name, DiscardAdvisor Advisor)> configs, int hands, int seed)
{
    Console.WriteLine(title);
    Console.WriteLine($"  {label,-12} result");

    foreach (var (name, advisor) in configs)
    {
        var watch = Stopwatch.StartNew();
        Console.Write($"  {name,-12} running...");
        var result = HandSimulator.Run(advisor, hands, seed);
        Console.Write('\r');
        Console.WriteLine($"  {name,-12} {result}   [{watch.Elapsed.TotalSeconds:0.0}s]");
    }

    Console.WriteLine();
}

// Readiness is now gated to the endgame; this checks where that gate belongs.
Sweep(
    "When readiness should start to matter (wall count):",
    "below wall",
    ((int[])[0, 8, 16, 24, 70]).Select(w =>
        (w == 0 ? "never" : w == 70 ? "always" : w.ToString(),
         new DiscardAdvisor { NotenAppliesBelowWall = w })),
    hands, seed);

Sweep(
    "How many future discards a push should account for:",
    "horizon",
    ((int[])[1, 2, 4, 8]).Select(h =>
        (h.ToString(), new DiscardAdvisor { MaxPushHorizonOverride = h })),
    hands, seed);

// This is the live risk price. The old sweep moved AverageDealInCost, which the
// scoring never reached, so all three rows came back identical.
Sweep(
    "What an opponent's hand is assumed to cost you:",
    "opp value",
    ((double[])[2500, 4000, 6500, 9000]).Select(c =>
        ($"{c:0}", new DiscardAdvisor { Threats = new ThreatModel { BaseHandValue = c } })),
    hands, seed);

Console.WriteLine("Higher average points is better. Deal-in rate is what pushing costs,");
Console.WriteLine("win rate is what it buys. The best setting maximises the gap.");
// Now that our seat can call, the call policy is measurable for the first time.
Sweep(
    "How reluctant to open the hand (penalty for calling with no yaku):",
    "penalty",
    ((double[])[0.5, 1.5, 2.5, 4.0]).Select(v =>
        ($"{v:0.0}", new DiscardAdvisor { NoYakuPenalty = v })),
    hands, seed);

// Ready hands are now priced from yaku/fu/dora instead of a dora multiplier.
// This isolates the value model: both rows play the same deals and use the same
// exact scoring, so the difference is only how the wait is valued.
Sweep(
    "Exact ready-hand value vs the old dora multiplier:",
    "value",
    ((bool[])[false, true]).Select(v =>
        (v ? "exact" : "heuristic", new DiscardAdvisor { ExactTenpaiValue = v })),
    hands, seed);

Console.WriteLine();

// Calibration is worth stating outright: defensive settings mean nothing while
// the simulated table plays at a different speed from a real one.
var baseline = HandSimulator.Run(new DiscardAdvisor(), hands, seed);
Console.WriteLine("Calibration against real play, at default settings:");
Console.WriteLine($"  {"",-12} {"sim",-10} {"real",-10}");
Console.WriteLine($"  {"your win",-12} {baseline.WinRate * 100,-9:0.0}% {"21%",-10}");
Console.WriteLine($"  {"deal-in",-12} {baseline.DealInRate * 100,-9:0.0}% {"13%",-10}");
Console.WriteLine($"  {"draw",-12} {baseline.DrawRate * 100,-9:0.0}% {"16%",-10}");
Console.WriteLine($"  {"opponents",-12} {baseline.OpponentWinRate * 100,-9:0.0}% {"63%",-10}");
Console.WriteLine();
Console.WriteLine("The closer those match, the more the sweeps above can be trusted.");
