using Mahjong.Advisor;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace MahjongAdvisor;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool ShowOverlay { get; set; } = true;

    public bool OverlayOnlyAtTable { get; set; } = true;

    public bool LockOverlay { get; set; }

    /// <summary>Number of alternative discards listed under the recommendation.</summary>
    public int AlternativesShown { get; set; } = 3;

    public bool ShowAcceptedTiles { get; set; } = true;

    /// <summary>
    /// Reopen the setup window on launch. Off by default: it is a diagnostics
    /// window, and opening it every session because it was used once is a
    /// nuisance rather than a convenience.
    ///
    /// Renamed from ShowDebugWindow so existing configs fall back to the default
    /// instead of carrying the stuck value forward.
    /// </summary>
    public bool OpenSetupOnLaunch { get; set; }

    /// <summary>Write a JSONL record of every decision point for later review.</summary>
    public bool LogGames { get; set; }

    // --- advisor tuning -------------------------------------------------
    public double AverageWinValue { get; set; } = 1300;

    public double AverageDealInCost { get; set; } = 5200;

    /// <summary>Points swing between being ready and not ready at an exhaustive draw.</summary>
    public double NotenPenaltyValue { get; set; } = 1500;

    /// <summary>What feeding an average opponent hand costs.</summary>
    public double OpponentHandValue { get; set; } = 4000;

    /// <summary>
    /// Computed views must never be serialised -- Newtonsoft writes every public
    /// property by default and a computed collection duplicates the whole graph
    /// on every save.
    /// </summary>
    [JsonIgnore]
    public DiscardAdvisor Advisor => new()
    {
        AverageWinValue = AverageWinValue,
        AverageDealInCost = AverageDealInCost,
        NotenPenaltyValue = NotenPenaltyValue,
        Threats = new ThreatModel { BaseHandValue = OpponentHandValue },
    };

    public void Initialize()
    {
        bool changed = false;

        if (AlternativesShown is < 0 or > 10)
        {
            AlternativesShown = 3;
            changed = true;
        }

        if (AverageWinValue <= 0)
        {
            AverageWinValue = 1300;
            changed = true;
        }

        if (AverageDealInCost <= 0)
        {
            AverageDealInCost = 5200;
            changed = true;
        }

        if (NotenPenaltyValue < 0)
        {
            NotenPenaltyValue = 1500;
            changed = true;
        }

        if (OpponentHandValue <= 0)
        {
            OpponentHandValue = 4000;
            changed = true;
        }

        if (changed)
            NeedsSaveAfterInitialize = true;
    }

    [JsonIgnore]
    public bool NeedsSaveAfterInitialize { get; private set; }
}
