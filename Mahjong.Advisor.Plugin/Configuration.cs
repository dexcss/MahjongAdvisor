using Mahjong.Advisor;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace MahjongAdvisor;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    public bool ShowOverlay { get; set; } = true;

    public bool OverlayOnlyAtTable { get; set; } = true;

    public bool LockOverlay { get; set; }

    /// <summary>Number of alternative discards listed under the recommendation.</summary>
    public int AlternativesShown { get; set; } = 3;

    public bool ShowAcceptedTiles { get; set; } = true;

    // --- auto-play ------------------------------------------------------
    // This build is set up to play by itself: auto-play and logging are on
    // for new installs and for configs upgraded from version 1. /mjadv stop
    // or the Automation tab returns to manual suggestions immediately.
    public bool AutoPlay { get; set; } = true;

    /// <summary>Pon is the only call auto-play takes by default: it is a clean,
    /// verified `[11, option]` press and our CallAdvisor already weighs the yaku.</summary>
    public bool AutoPlayAllowPon { get; set; } = true;

    /// <summary>Chi requires the variant-selection surface, which is not verified.</summary>
    public bool AutoPlayAllowChi { get; set; }

    /// <summary>A kan flips another dora indicator for every seat; opt in only.</summary>
    public bool AutoPlayAllowKan { get; set; }

    public bool AutoPlayAdvanceResults { get; set; } = true;

    // Humanized timing. The reference's live run used the same ranges.
    public int AutoPlayTurnDelayMinMs { get; set; } = 700;
    public int AutoPlayTurnDelayMaxMs { get; set; } = 1800;
    public int AutoPlayCallDelayMinMs { get; set; } = 500;
    public int AutoPlayCallDelayMaxMs { get; set; } = 1200;
    public int AutoPlayResultDelayMinMs { get; set; } = 1500;
    public int AutoPlayResultDelayMaxMs { get; set; } = 2800;

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
    public bool LogGames { get; set; } = true;

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

        // v2 is the "just play and send the log" setup: auto-play and file
        // logging are enabled for existing v1 configs. Toggling either off
        // afterwards persists normally.
        if (Version < 2)
        {
            AutoPlay = true;
            LogGames = true;
            Version = 2;
            changed = true;
        }

        // v3 resets the exposed tuning sliders to the values the engine was
        // fitted and simulated with. Existing configs had drifted (5200/5800),
        // which changes the push/fold balance.
        if (Version < 3)
        {
            AverageWinValue = 1300;
            AverageDealInCost = 5200;
            NotenPenaltyValue = 1500;
            OpponentHandValue = 4000;
            Version = 3;
            changed = true;
        }

        var turnDelay = NormalizeDelay(
            AutoPlayTurnDelayMinMs, AutoPlayTurnDelayMaxMs, 700, 1800);
        AutoPlayTurnDelayMinMs = turnDelay.Min;
        AutoPlayTurnDelayMaxMs = turnDelay.Max;
        changed |= turnDelay.Changed;

        var callDelay = NormalizeDelay(
            AutoPlayCallDelayMinMs, AutoPlayCallDelayMaxMs, 500, 1200);
        AutoPlayCallDelayMinMs = callDelay.Min;
        AutoPlayCallDelayMaxMs = callDelay.Max;
        changed |= callDelay.Changed;

        var resultDelay = NormalizeDelay(
            AutoPlayResultDelayMinMs, AutoPlayResultDelayMaxMs, 1500, 2800);
        AutoPlayResultDelayMinMs = resultDelay.Min;
        AutoPlayResultDelayMaxMs = resultDelay.Max;
        changed |= resultDelay.Changed;

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

        static (int Min, int Max, bool Changed) NormalizeDelay(
            int min, int max, int defaultMin, int defaultMax)
        {
            bool changed = false;

            if (min is < 0 or > 10_000)
            {
                min = defaultMin;
                changed = true;
            }

            if (max is < 0 or > 10_000)
            {
                max = defaultMax;
                changed = true;
            }

            if (max < min)
            {
                max = min;
                changed = true;
            }

            return (min, max, changed);
        }
    }

    [JsonIgnore]
    public bool NeedsSaveAfterInitialize { get; private set; }
}
