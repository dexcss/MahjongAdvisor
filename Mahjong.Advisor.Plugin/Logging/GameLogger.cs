using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mahjong.Advisor;
using MahjongAdvisor.Reading;

namespace MahjongAdvisor.Logging;

/// <summary>One decision point, written as a single JSONL line.</summary>
public sealed class LogRecord
{
    public string Time { get; set; } = "";

    /// <summary>Plugin build, so a log can be matched to the code that produced it.</summary>
    public string Build { get; set; } = "";
    public string Kind { get; set; } = "";       // "discard" | "call" | "prompt-unmatched" | "auto"
    public int StateCode { get; set; }
    public string Hand { get; set; } = "";
    public int CalledMelds { get; set; }
    public int Wall { get; set; }
    public string Dora { get; set; } = "";
    public string Observed { get; set; } = "";

    /// <summary>Whether opponent melds were actually read, not merely empty.</summary>
    public bool MeldObserved { get; set; }

    public string Profile { get; set; } = "";
    public int RejectedTiles { get; set; }
    public bool TextureBaseShifted { get; set; }

    /// <summary>Whether the display-slot hand read matched the memory hand.</summary>
    public bool HandSlotsValid { get; set; }

    /// <summary>Newest discard the pond tracker saw at this point.</summary>
    public string? LastDiscard { get; set; }

    public int LastDiscardSeat { get; set; } = -1;

    /// <summary>Points per seat, ours first. A riichi declaration costs exactly 1000.</summary>
    public int[] Scores { get; set; } = [];

    /// <summary>Per-seat count of discards the client is drawing sideways.</summary>
    public int[] RotatedDiscards { get; set; } = [];

    /// <summary>Counts from the client's own byte. A pile reading short loses a tile.</summary>
    public int[] DiscardCountBytes { get; set; } = [];

    /// <summary>Tile-range texture ids inside piles that did not decode.</summary>
    public int[] UnknownPondTextures { get; set; } = [];

    /// <summary>Nodes that became visible this tick, as "id@x,y:texture".</summary>
    public string[] AppearedNodes { get; set; } = [];

    /// <summary>Discards barred by swap-calling this turn.</summary>
    public string[] Barred { get; set; } = [];

    /// <summary>Our hand is frozen by our own riichi.</summary>
    public bool SelfRiichi { get; set; }

    /// <summary>Hand in client display-slot order when the slot reader is valid.</summary>
    public string HandOrder { get; set; } = "";

    /// <summary>The tile identified as just drawn.</summary>
    public string DrawnTile { get; set; } = "";

    /// <summary>Turn number, so decisions can be compared against the timing tables.</summary>
    public int Turn { get; set; }

    /// <summary>What this hand is estimated to pay, and why.</summary>
    public double HandValue { get; set; }

    /// <summary>Dora held in hand, including red fives. Distinct from the indicators above.</summary>
    public int DoraHeld { get; set; }

    /// <summary>Per-opponent readiness, as "seat:percent".</summary>
    public string[] Readiness { get; set; } = [];

    /// <summary>Our standing, 1 = leading.</summary>
    public int Rank { get; set; }

    /// <summary>The riichi declaration discard is still to come.</summary>
    public bool RiichiPending { get; set; }

    public string[] SeatDiscards { get; set; } = [];
    public bool[] SeatRiichi { get; set; } = [];
    public string[] SeatMelds { get; set; } = [];

    /// <summary>Ranked advice. First entry is what the plugin recommended.</summary>
    public List<LoggedOption> Options { get; set; } = [];

    public string Headline { get; set; } = "";
    public bool Folding { get; set; }

    /// <summary>Auto-play controller status, for "auto-status" records.</summary>
    public string? AutoStatus { get; set; }

    /// <summary>What actually happened, filled in once the next state is seen.</summary>
    public string? PlayerAction { get; set; }

    public bool? FollowedAdvice { get; set; }

    // Call-prompt diagnostics: raw values so the mapping can be checked.
    public string? ClaimedTile { get; set; }
    public string[]? PromptLabels { get; set; }
    public int[]? PromptRawClaim { get; set; }
    public string[]? PromptOptions { get; set; }

    /// <summary>Raw popup button run as "index:label", even when it is not a call.</summary>
    public string[]? PromptRawOptions { get; set; }

    /// <summary>How the discard piles were located, and per-seat tile counts.</summary>
    public string? PondMapping { get; set; }

    public int[]? PondCounts { get; set; }
}

public sealed class LoggedOption
{
    public string Tile { get; set; } = "";
    public string? Call { get; set; }
    public int Shanten { get; set; }
    public int Ukeire { get; set; }
    public double Danger { get; set; }
    public string DangerReason { get; set; } = "";
    public double Score { get; set; }
    public string Rationale { get; set; } = "";
}

/// <summary>
/// Appends a JSONL record for every decision point, so a finished game can be
/// reviewed move by move afterwards. Buffered and flushed on a cadence -- a
/// file write per frame is how you end up with a locked database.
/// </summary>
public sealed class GameLogger : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string directory;
    private readonly Action<string> warn;
    private readonly List<string> pending = [];
    private readonly object gate = new();

    /// <summary>Bumped whenever behaviour changes, so logs are attributable.</summary>
    public const string BuildStamp = "2026-10-18.11";

    private string? currentPath;
    private string lastFingerprint = "";
    private LogRecord? awaitingAction;
    private DateTime lastFlush = DateTime.UtcNow;

    public GameLogger(string directory, Action<string> warn)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(warn);
        this.directory = directory;
        this.warn = warn;
    }

    public bool Enabled { get; set; }

    public int RecordsWritten { get; private set; }

    public string? CurrentFile => currentPath;

    public string Directory => directory;

    /// <summary>
    /// Records a decision point. Deduplicates on hand + state so a 6 Hz tick does
    /// not write the same position dozens of times.
    /// </summary>
    public void Record(
        BoardRead read,
        Advice? discardAdvice,
        CallPrompt prompt,
        CallAdvice? callAdvice,
        string? pondDiagnostic = null,
        int[]? rotatedDiscards = null,
        int[]? countBytes = null,
        int[]? unknownTextures = null,
        string[]? appeared = null)
    {
        if (!Enabled || read.Table is null)
            return;

        var table = read.Table;
        string hand = Tiles.Format(table.Hand);

        // Dedupe on everything that can change the recommendation, not just the
        // hand: an opponent's discard can turn a tile from live to genbutsu and
        // change the advice while our own hand is untouched.
        string discards = string.Join("/", table.Seats.Select(s => s.Discards.Count));
        string riichi = string.Join("", table.Seats.Select(s => s.Riichi ? "R" : "-"));
        string promptKey = string.Join(",", prompt.Available);
        string fingerprint =
            $"{read.StateCode}|{hand}|{table.CalledMelds}|{prompt.ClaimedTile}|{promptKey}|{table.WallRemaining}|{discards}|{riichi}";

        // Fill in what the player did as soon as OUR hand changes. A pond change
        // must not resolve the pending discard as "no action taken".
        if (awaitingAction is not null && hand != awaitingAction.Hand)
        {
            ResolveAction(awaitingAction, hand);
            Append(awaitingAction);
            awaitingAction = null;
        }

        if (fingerprint == lastFingerprint)
            return;
        lastFingerprint = fingerprint;

        var record = new LogRecord
        {
            Time = DateTime.UtcNow.ToString("O"),
            Build = BuildStamp,
            StateCode = read.StateCode,
            Hand = hand,
            CalledMelds = table.CalledMelds,
            Wall = table.WallRemaining,
            Dora = string.Join(" ", table.DoraIndicators.Select(Tiles.Name)),
            Observed = table.Observations.ToString(),
            MeldObserved = table.Observations.HasFlag(Observed.OpponentMelds),
            Profile = read.Profile?.Name ?? "",
            RejectedTiles = read.RejectedTiles,
            TextureBaseShifted = read.TextureBaseShifted,
            HandSlotsValid = read.HandSlotsValid,
            LastDiscard = read.LastDiscardTile >= 0 ? Tiles.Name(read.LastDiscardTile) : null,
            LastDiscardSeat = read.LastDiscardSeat,
            SeatDiscards = [.. table.Seats.Select(s => string.Join(" ", s.Discards.Select(Tiles.Name)))],
            SeatRiichi = [.. table.Seats.Select(s => s.Riichi)],
            SeatMelds = [.. table.Seats.Select(s => string.Join(" ", s.MeldTiles.Select(Tiles.Name)))],
            Scores = [.. table.Scores],
            RotatedDiscards = rotatedDiscards ?? [],
            DiscardCountBytes = countBytes ?? [],
            UnknownPondTextures = unknownTextures ?? [],
            AppearedNodes = appeared ?? [],
            Barred = [.. table.ForbiddenDiscards.Select(Tiles.Name)],
            SelfRiichi = table.SelfRiichi,
            HandOrder = string.Join(" ", read.HandOrder.Select(Tiles.Name)),
            Turn = HououTables.TurnFromWall(table.WallRemaining),
            HandValue = Math.Round(new DiscardAdvisor().EstimateWinValue(table), 0),
            DoraHeld = table.CountDora(),
            Rank = table.Scores.Count == 4 ? PlacementAdjuster.RankOf(table.Scores) : 0,
            RiichiPending = table.RiichiDeclarationPending,
            Readiness = [.. new ThreatModel().Evaluate(table)
                .Select(t => $"{t.Seat}:{t.TenpaiProbability * 100:0}{(t.Riichi ? "R" : "")}")],
            DrawnTile = table.DrawnTile >= 0 ? Tiles.Name(table.DrawnTile) : "",
            PondMapping = pondDiagnostic,
            PondCounts = [.. table.Seats.Select(s => s.Discards.Count)],
        };

        if (prompt.Available.Count > 0)
        {
            record.Kind = callAdvice is null ? "prompt-unmatched" : "call";
            record.ClaimedTile = prompt.HasClaimedTile ? Tiles.Name(prompt.ClaimedTile) : null;
            record.PromptLabels = [.. prompt.RawLabels];
            record.PromptRawClaim = [.. prompt.RawClaimCandidates];
            record.PromptOptions = [.. prompt.Available.Select(a => a.ToString())];
            record.PromptRawOptions = [.. prompt.Options.Select(o => $"{o.Option}:{o.Label}")];

            if (callAdvice is not null)
            {
                record.Headline = callAdvice.Headline;
                record.Options = [.. callAdvice.Options.Select(o => new LoggedOption
                {
                    Tile = prompt.HasClaimedTile ? Tiles.Name(prompt.ClaimedTile) : "",
                    Call = o.Kind.ToString(),
                    Shanten = o.ResultingShanten,
                    Ukeire = o.Ukeire,
                    Score = o.Score,
                    Rationale = o.Rationale,
                })];
            }
        }
        else if (discardAdvice is not null)
        {
            record.Kind = "discard";
            record.Headline = discardAdvice.Headline;
            record.Folding = discardAdvice.Folding;
            record.Options = [.. discardAdvice.Options.Take(6).Select(o => new LoggedOption
            {
                Tile = Tiles.Name(o.Tile),
                Shanten = o.ResultingShanten,
                Ukeire = o.Ukeire,
                Danger = Math.Round(o.Risk.DealInRate, 4),
                DangerReason = o.Risk.Reason.ToString(),
                Score = Math.Round(o.Score, 2),
                Rationale = o.Rationale,
            })];
            awaitingAction = record;
            return; // held until we can see what was discarded
        }
        else
        {
            return;
        }

        Append(record);
    }

    /// <summary>
    /// Appends a trace line for an input the auto-play controller actually sent.
    /// Kept separate from the decision record so a hand can be replayed against
    /// exactly what the plugin clicked.
    /// </summary>
    public void RecordAutoAction(string action, string detail)
    {
        if (!Enabled)
            return;

        Append(new LogRecord
        {
            Time = DateTime.UtcNow.ToString("O"),
            Build = BuildStamp,
            Kind = "auto",
            Headline = action,
            PlayerAction = detail,
        });
    }

    /// <summary>
    /// Appends a status-change line from the auto-play controller. These are
    /// the "why did it stop / why did it wait" records: they carry the state,
    /// hand, prompt buttons and the first few ranked options at that moment.
    /// </summary>
    public void RecordAutoStatus(
        string status,
        BoardRead? read,
        CallPrompt prompt,
        Advice? advice,
        CallAdvice? callAdvice)
    {
        if (!Enabled)
            return;

        var record = new LogRecord
        {
            Time = DateTime.UtcNow.ToString("O"),
            Build = BuildStamp,
            Kind = "auto-status",
            AutoStatus = status,
            Headline = status,
            StateCode = read?.StateCode ?? -1,
            Profile = read?.Profile?.Name ?? "",
            HandSlotsValid = read?.HandSlotsValid ?? false,
            RejectedTiles = read?.RejectedTiles ?? 0,
            TextureBaseShifted = read?.TextureBaseShifted ?? false,
            LastDiscard = read is { LastDiscardTile: >= 0 }
                ? Tiles.Name(read.LastDiscardTile)
                : null,
            LastDiscardSeat = read?.LastDiscardSeat ?? -1,
        };

        if (read?.Table is { } table)
        {
            record.Hand = Tiles.Format(table.Hand);
            record.CalledMelds = table.CalledMelds;
            record.Wall = table.WallRemaining;
            record.Dora = string.Join(" ", table.DoraIndicators.Select(Tiles.Name));
            record.Observed = table.Observations.ToString();
            record.MeldObserved = table.Observations.HasFlag(Observed.OpponentMelds);
            record.HandOrder = string.Join(" ", read.HandOrder.Select(Tiles.Name));
            record.Scores = [.. table.Scores];
            record.SeatRiichi = [.. table.Seats.Select(s => s.Riichi)];
            record.SeatDiscards = [.. table.Seats.Select(s => string.Join(" ", s.Discards.Select(Tiles.Name)))];
            record.SeatMelds = [.. table.Seats.Select(s => string.Join(" ", s.MeldTiles.Select(Tiles.Name)))];
        }

        if (prompt.Available.Count > 0 || prompt.Options.Count > 0 || prompt.RawLabels.Count > 0)
        {
            record.ClaimedTile = prompt.HasClaimedTile ? Tiles.Name(prompt.ClaimedTile) : null;
            record.PromptOptions = [.. prompt.Available.Select(a => a.ToString())];
            record.PromptLabels = [.. prompt.RawLabels];
            record.PromptRawClaim = [.. prompt.RawClaimCandidates];
            record.PromptRawOptions = [.. prompt.Options.Select(o => $"{o.Option}:{o.Label}")];
        }

        if (advice?.Best is { } best)
        {
            record.Headline = $"{status} | discard {Tiles.Name(best.Tile)} "
                + $"({best.ResultingShanten}-shanten, {best.Ukeire} tiles)";
            record.Options = [.. advice.Options.Take(3).Select(o => new LoggedOption
            {
                Tile = Tiles.Name(o.Tile),
                Shanten = o.ResultingShanten,
                Ukeire = o.Ukeire,
                Danger = Math.Round(o.Risk.DealInRate, 4),
                DangerReason = o.Risk.Reason.ToString(),
                Score = Math.Round(o.Score, 2),
                Rationale = o.Rationale,
            })];
        }

        if (callAdvice?.Best is { } call)
            record.Headline = $"{status} | call {call.Kind}";

        Append(record);
    }

    /// <summary>Marks the start/stop of an auto-play session in the current file.</summary>
    public void RecordSessionMarker(string marker, string? detail = null)
    {
        if (!Enabled)
            return;

        Append(new LogRecord
        {
            Time = DateTime.UtcNow.ToString("O"),
            Build = BuildStamp,
            Kind = "session",
            Headline = marker,
            AutoStatus = detail,
        });
    }

    /// <summary>Works out which tile left the hand between the two positions.</summary>
    private static void ResolveAction(LogRecord record, string nextHand)
    {
        try
        {
            var before = Tiles.Parse(record.Hand);
            var after = Tiles.Parse(nextHand);
            var gone = new List<int>();

            for (int t = 0; t < Tiles.Kinds; t++)
            {
                for (int n = 0; n < before[t] - after[t]; n++)
                    gone.Add(t);
            }

            if (gone.Count == 1)
            {
                record.PlayerAction = Tiles.Name(gone[0]);
                record.FollowedAdvice = record.Options.Count > 0
                    && record.Options[0].Tile == record.PlayerAction;
            }
            else if (gone.Count > 1)
            {
                record.PlayerAction = $"multiple:{string.Join(" ", gone.Select(Tiles.Name))}";
            }
        }
        catch
        {
            // A malformed hand string is not worth losing the record over.
        }
    }

    private void Append(LogRecord record)
    {
        lock (gate)
        {
            pending.Add(JsonSerializer.Serialize(record, Json));
            RecordsWritten++;
        }

        if ((DateTime.UtcNow - lastFlush).TotalSeconds >= 5)
            Flush();
    }

    /// <summary>Starts a new file. Call at the start of a session or a new game.</summary>
    public void StartNewGame()
    {
        // The last decision has not been matched to a player action yet. Keep it
        // in the old file rather than dropping it when the file rolls over.
        if (awaitingAction is not null)
        {
            Append(awaitingAction);
            awaitingAction = null;
        }

        Flush();
        lock (gate)
        {
            currentPath = Path.Combine(
                directory, $"game-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            lastFingerprint = "";
            RecordsWritten = 0;
        }
    }

    public void Flush()
    {
        string[] lines;
        string? path;

        lock (gate)
        {
            if (pending.Count == 0)
                return;
            lines = [.. pending];
            pending.Clear();
            path = currentPath;
        }

        lastFlush = DateTime.UtcNow;

        if (path is null)
            return;

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            File.AppendAllLines(path, lines, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            warn($"Could not write the game log: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (awaitingAction is not null)
        {
            Append(awaitingAction);
            awaitingAction = null;
        }

        Flush();
    }
}
