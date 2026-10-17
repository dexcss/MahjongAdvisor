using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mahjong.Advisor;
using MahjongAdvisor.Automation;
using MahjongAdvisor.Reading;

namespace MahjongAdvisor.Diagnostics;

/// <summary>
/// Packages everything needed to diagnose an auto-play session into one zip:
/// the JSONL logs, plugin config, saved layouts, a Dalamud log tail, a full
/// last-read snapshot and a human-readable summary.
/// </summary>
public static class DiagnosticBundle
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string? Create(
        string configDirectory,
        string logDirectory,
        Configuration config,
        BoardRead? read,
        Advice? advice,
        CallAdvice? callAdvice,
        CallPrompt prompt,
        AutoPlayController auto,
        string buildStamp,
        string? pondDiagnostic,
        Action<string>? warn = null)
    {
        try
        {
            string outputDirectory = Path.Combine(configDirectory, "diagnostics");
            Directory.CreateDirectory(outputDirectory);

            string path = Path.Combine(
                outputDirectory,
                $"MahjongAdvisor-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");

            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

            AddText(zip, "summary.txt", BuildSummary(
                config, read, advice, callAdvice, prompt, auto, buildStamp, pondDiagnostic));
            AddText(zip, "snapshot.json", BuildSnapshot(
                config, read, advice, callAdvice, prompt, auto, buildStamp));
            AddText(zip, "environment.txt", BuildEnvironment(configDirectory, logDirectory, buildStamp));
            AddText(zip, "README.txt",
                "Mahjong Advisor diagnostic bundle.\r\n\r\n"
                + "summary.txt      Human-readable state at bundle time.\r\n"
                + "snapshot.json    Full last-read / advice / prompt objects.\r\n"
                + "environment.txt  OS, runtime and paths.\r\n"
                + "config/          Plugin configuration JSON.\r\n"
                + "layouts/         Saved node mappings.\r\n"
                + "logs/            Recent game-*.jsonl decision and auto-action logs.\r\n"
                + "dalamud-tail.txt Last part of XIVLauncher/dalamud.log.\r\n");

            foreach (string file in Directory.EnumerateFiles(configDirectory, "*.json"))
                AddFile(zip, $"config/{Path.GetFileName(file)}", file);

            // Dalamud stores the actual IPluginConfiguration file one level up:
            // pluginConfigs/<InternalName>.json, not inside the per-plugin data
            // directory. Include it explicitly.
            string parentConfigDirectory = Path.GetDirectoryName(configDirectory) ?? configDirectory;
            string pluginConfigFile = Path.Combine(
                parentConfigDirectory, Path.GetFileName(configDirectory) + ".json");
            AddFile(zip, $"config/{Path.GetFileName(pluginConfigFile)}", pluginConfigFile);

            string layoutDirectory = Path.Combine(configDirectory, "layouts");
            if (Directory.Exists(layoutDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(layoutDirectory, "*.json"))
                    AddFile(zip, $"layouts/{Path.GetFileName(file)}", file);
            }

            if (Directory.Exists(logDirectory))
            {
                foreach (string file in Directory
                             .EnumerateFiles(logDirectory, "*.jsonl")
                             .OrderByDescending(File.GetLastWriteTimeUtc)
                             .Take(20))
                {
                    AddFile(zip, $"logs/{Path.GetFileName(file)}", file);
                }
            }

            AddText(zip, "dalamud-tail.txt", ReadDalamudTail());

            return path;
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Could not create the diagnostic bundle: {ex.Message}");
            return null;
        }
    }

    private static string BuildSummary(
        Configuration config,
        BoardRead? read,
        Advice? advice,
        CallAdvice? callAdvice,
        CallPrompt prompt,
        AutoPlayController auto,
        string buildStamp,
        string? pondDiagnostic)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Mahjong Advisor diagnostics");
        sb.AppendLine($"Generated : {DateTime.Now:O}");
        sb.AppendLine($"Build     : {buildStamp}");
        sb.AppendLine($"Author    : Akari");
        sb.AppendLine($"API       : Dalamud 15");
        sb.AppendLine();

        sb.AppendLine("Auto-play");
        sb.AppendLine($"  Enabled      : {config.AutoPlay}");
        sb.AppendLine($"  Status       : {auto.LastStatus}");
        sb.AppendLine($"  Last action  : {auto.Stats.LastAction}");
        sb.AppendLine($"  Counts       : discards {auto.Stats.Discards}, calls {auto.Stats.Calls}, "
            + $"riichi {auto.Stats.Riichi}, wins {auto.Stats.Wins}, passes {auto.Stats.Passes}, "
            + $"advances {auto.Stats.Advances}, kans {auto.Stats.Kans}");
        sb.AppendLine($"  Pon/Chi/Kan  : {config.AutoPlayAllowPon}/{config.AutoPlayAllowChi}/{config.AutoPlayAllowKan}");
        sb.AppendLine($"  Advance      : {config.AutoPlayAdvanceResults}");
        sb.AppendLine($"  Delays (ms)  : turn {config.AutoPlayTurnDelayMinMs}-{config.AutoPlayTurnDelayMaxMs}, "
            + $"call {config.AutoPlayCallDelayMinMs}-{config.AutoPlayCallDelayMaxMs}, "
            + $"result {config.AutoPlayResultDelayMinMs}-{config.AutoPlayResultDelayMaxMs}");
        sb.AppendLine();

        sb.AppendLine("Plugin");
        sb.AppendLine($"  Logging      : {config.LogGames}");
        sb.AppendLine($"  Overlay      : {config.ShowOverlay}, only at table {config.OverlayOnlyAtTable}, locked {config.LockOverlay}");
        sb.AppendLine($"  Alternatives : {config.AlternativesShown}");
        sb.AppendLine($"  Tuning       : win {config.AverageWinValue}, deal-in {config.AverageDealInCost}, "
            + $"noten {config.NotenPenaltyValue}, opponent {config.OpponentHandValue}");
        sb.AppendLine();

        sb.AppendLine("Last read");
        if (read is null)
        {
            sb.AppendLine("  (no read)");
        }
        else
        {
            sb.AppendLine($"  Status          : {read.Status}");
            sb.AppendLine($"  Profile         : {read.Profile?.Name ?? "none"} ({read.Profile?.AddonName ?? "-"})");
            sb.AppendLine($"  State code      : {read.StateCode}");
            sb.AppendLine($"  Texture base    : {read.TextureBase} (shifted {read.TextureBaseShifted})");
            sb.AppendLine($"  Rejected tiles  : {read.RejectedTiles}");
            sb.AppendLine($"  Hand slots valid: {read.HandSlotsValid}");
            sb.AppendLine($"  Pond diagnostic : {pondDiagnostic ?? "none"}");

            if (read.Table is { } table)
            {
                sb.AppendLine($"  Hand            : {Tiles.Format(table.Hand)}");
                sb.AppendLine($"  Called melds    : {table.CalledMelds}");
                sb.AppendLine($"  Wall            : {table.WallRemaining}");
                sb.AppendLine($"  Dora indicators : {string.Join(" ", table.DoraIndicators.Select(Tiles.Name))}");
                sb.AppendLine($"  Scores          : {string.Join(" ", table.Scores)}");
                sb.AppendLine($"  Observed        : {table.Observations}");
                sb.AppendLine($"  Meld read       : {table.Observations.HasFlag(Observed.OpponentMelds)}");
                sb.AppendLine($"  Self riichi     : {table.SelfRiichi} (pending {table.RiichiDeclarationPending})");
                sb.AppendLine($"  Drawn tile      : {(table.DrawnTile >= 0 ? Tiles.Name(table.DrawnTile) : "-")}");
                sb.AppendLine($"  Forbidden       : {string.Join(" ", table.ForbiddenDiscards.Select(Tiles.Name))}");
                sb.AppendLine("  Seats:");
                for (int i = 0; i < table.Seats.Length; i++)
                {
                    var seat = table.Seats[i];
                    sb.AppendLine($"    {i}: discards [{string.Join(" ", seat.Discards.Select(Tiles.Name))}] "
                        + $"melds [{string.Join(" ", seat.MeldTiles.Select(Tiles.Name))}] "
                        + $"riichi {seat.Riichi} at {seat.RiichiDiscardIndex}");
                }
            }

            if (read.HandSlots.Count > 0)
            {
                sb.AppendLine("  Display slots:");
                foreach (var slot in read.HandSlots)
                {
                    sb.AppendLine($"    {slot.Slot,2}: {Tiles.Name(slot.Tile),3} icon {slot.IconId}"
                        + (slot.IsRed ? " red" : ""));
                }
            }

            if (read.Profile is { } profile)
            {
                sb.AppendLine($"  Hand slot nodes : {string.Join(", ", profile.HandSlotNodeIds)} / {profile.DrawnTileNodeId}");
                for (int seat = 0; seat < 4; seat++)
                    sb.AppendLine($"  Meld nodes {seat}  : {string.Join(", ", profile.MeldNodeIdsForSeat(seat))}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Prompt");
        sb.AppendLine($"  State       : {prompt.StateCode}");
        sb.AppendLine($"  Available   : {string.Join(", ", prompt.Available)}");
        sb.AppendLine($"  Claimed     : {(prompt.HasClaimedTile ? Tiles.Describe(prompt.ClaimedTile) : "-")}");
        foreach (var option in prompt.Options)
            sb.AppendLine($"  [11,{option.Option}] {option.Label}");

        sb.AppendLine();
        sb.AppendLine("Discard advice");
        if (advice?.Best is { } best)
        {
            sb.AppendLine($"  Headline : {advice.Headline}");
            sb.AppendLine($"  Folding  : {advice.Folding}");
            foreach (var option in advice.Options.Take(8))
            {
                sb.AppendLine($"  {Tiles.Name(option.Tile),3} shanten {option.ResultingShanten} "
                    + $"ukeire {option.Ukeire} danger {option.Risk.DealInRate:0.####} "
                    + $"score {option.Score:0.##} - {option.Rationale}");
            }
        }
        else
        {
            sb.AppendLine("  (none)");
        }

        sb.AppendLine();
        sb.AppendLine("Call advice");
        if (callAdvice?.Best is { } call)
        {
            sb.AppendLine($"  Headline : {callAdvice.Headline}");
            foreach (var option in callAdvice.Options.Take(6))
            {
                sb.AppendLine($"  {option.Kind} shanten {option.ResultingShanten} ukeire {option.Ukeire} "
                    + $"yaku-risk {option.YakuAtRisk} - {option.Rationale}");
            }
        }
        else
        {
            sb.AppendLine("  (none)");
        }

        return sb.ToString();
    }

    private static string BuildSnapshot(
        Configuration config,
        BoardRead? read,
        Advice? advice,
        CallAdvice? callAdvice,
        CallPrompt prompt,
        AutoPlayController auto,
        string buildStamp)
    {
        var snapshot = new
        {
            generated = DateTime.UtcNow,
            build = buildStamp,
            config = new
            {
                config.AutoPlay,
                config.AutoPlayAllowPon,
                config.AutoPlayAllowChi,
                config.AutoPlayAllowKan,
                config.AutoPlayAdvanceResults,
                config.AutoPlayTurnDelayMinMs,
                config.AutoPlayTurnDelayMaxMs,
                config.AutoPlayCallDelayMinMs,
                config.AutoPlayCallDelayMaxMs,
                config.AutoPlayResultDelayMinMs,
                config.AutoPlayResultDelayMaxMs,
                config.ShowOverlay,
                config.OverlayOnlyAtTable,
                config.LockOverlay,
                config.LogGames,
            },
            auto = new
            {
                status = auto.LastStatus,
                stats = new
                {
                    auto.Stats.Discards,
                    auto.Stats.Calls,
                    auto.Stats.Riichi,
                    auto.Stats.Wins,
                    auto.Stats.Passes,
                    auto.Stats.Advances,
                    auto.Stats.Kans,
                    auto.Stats.LastAction,
                },
            },
            read,
            advice,
            callAdvice,
            prompt,
        };

        return JsonSerializer.Serialize(snapshot, Json);
    }

    private static string BuildEnvironment(string configDirectory, string logDirectory, string buildStamp)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generated : {DateTime.Now:O}");
        sb.AppendLine($"Build     : {buildStamp}");
        sb.AppendLine($"OS        : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Runtime   : {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Config    : {configDirectory}");
        sb.AppendLine($"Logs      : {logDirectory}");
        sb.AppendLine($"Machine   : {Environment.MachineName}");
        return sb.ToString();
    }

    private static string ReadDalamudTail()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher",
                "dalamud.log");

            if (!File.Exists(path))
                return "dalamud.log not found.";

            // Dalamud keeps this file open for writing. Open it with sharing
            // instead of letting File.ReadLines fail on the lock.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var tail = new Queue<string>(5000);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == 5000)
                    tail.Dequeue();
                tail.Enqueue(line);
            }

            return string.Join(Environment.NewLine, tail);
        }
        catch (Exception ex)
        {
            return $"Could not read dalamud.log: {ex.Message}";
        }
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void AddFile(ZipArchive zip, string entryName, string file)
    {
        try
        {
            if (File.Exists(file))
                zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
        catch
        {
            // A log being flushed or a config file with an unusual lock should
            // not lose the rest of the bundle.
        }
    }
}
