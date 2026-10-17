using Mahjong.Advisor;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MahjongAdvisor.Logging;
using MahjongAdvisor.Reading;
using MahjongAdvisor.UI;

namespace MahjongAdvisor;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mjadv";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("MahjongAdvisor");
    private readonly string layoutDirectory;
    private readonly List<LayoutProfile> profiles;

    private int framesSinceRead;
    private bool disposed;
    private bool readFailureLogged;

    public Configuration Configuration { get; }

    public EmjBoardReader Reader { get; }

    public NodeInspector NodeInspector { get; }

    public CallPromptReader PromptReader { get; } = new();

    public GameLogger GameLogger { get; }

    public AdvisorWindow AdvisorWindow { get; }

    public DebugWindow DebugWindow { get; }

    public BoardRead? LastRead { get; private set; }

    public Advice? LastAdvice { get; private set; }

    public CallPrompt LastPrompt { get; private set; } = CallPrompt.None;

    public CallAdvice? LastCallAdvice { get; private set; }

    private readonly CallAdvisor callAdvisor = new();

    /// <summary>Reads roughly six times a second; the board does not move faster than that.</summary>
    public int ReadIntervalFrames { get; set; } = 10;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize();
        if (Configuration.NeedsSaveAfterInitialize)
            SaveConfiguration();

        layoutDirectory = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName ?? ".", "layouts");
        string userLayouts = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "layouts");

        // Bundled profiles first, then user-saved ones override per addon name --
        // a saved Emj node mapping must not drop the bundled EmjL fallback.
        var profilesByAddon = new Dictionary<string, LayoutProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in LayoutProfileLoader.LoadAll(layoutDirectory, w => Log.Warning(w)))
            profilesByAddon[profile.AddonName] = profile;

        if (Directory.Exists(userLayouts)
            && Directory.EnumerateFiles(userLayouts, "*.json").Any())
        {
            foreach (var profile in LayoutProfileLoader.LoadAll(userLayouts, w => Log.Warning(w)))
                profilesByAddon[profile.AddonName] = profile;
        }

        profiles = [.. profilesByAddon.Values];

        NodeInspector = new NodeInspector();
        Reader = new EmjBoardReader(GameGui, profiles, NodeInspector);

        GameLogger = new GameLogger(
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "logs"),
            w => Log.Warning(w))
        {
            Enabled = Configuration.LogGames,
        };
        if (Configuration.LogGames)
            GameLogger.StartNewGame();

        AdvisorWindow = new AdvisorWindow(this);
        DebugWindow = new DebugWindow(this);
        windowSystem.AddWindow(AdvisorWindow);
        windowSystem.AddWindow(DebugWindow);

        AdvisorWindow.IsOpen = Configuration.ShowOverlay;
        DebugWindow.IsOpen = Configuration.OpenSetupOnLaunch;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/mjadv - show the overlay. Also: hide, toggle, setup, nodes, melds, log, geometry, snap.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSetup;
        PluginInterface.UiBuilder.OpenMainUi += ToggleOverlay;
        Framework.Update += OnFrameworkUpdate;

        Log.Information($"Mahjong Advisor loaded with {profiles.Count} layout profile(s).");
    }

    /// <summary>
    /// Everything here touches game memory, so it stays on the framework thread.
    /// No async work reads client state.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed)
            return;

        // Keep window visibility in lockstep with config, wherever it was changed.
        AdvisorWindow.IsOpen = Configuration.ShowOverlay;

        if (++framesSinceRead < ReadIntervalFrames)
            return;
        framesSinceRead = 0;

        try
        {
            var read = Reader.Read();
            LastRead = read;

            if (!read.Ok || read.Table is null)
            {
                LastAdvice = null;
                LastCallAdvice = null;
                LastPrompt = CallPrompt.None;
                if (read.Profile is null)
                    Reader.ResetAutoMap();
                return;
            }

            int total = Tiles.Total(read.Table.Hand);
            int expected = 14 - (read.Table.CalledMelds * 3);

            // A 3n+1 shape means we are waiting on a draw -- nothing to discard,
            // but a call prompt can still be live.
            if (total != expected)
            {
                LastAdvice = null;
                LastPrompt = ReadPrompt(read);
                LastCallAdvice = BuildCallAdvice(read, LastPrompt);
                GameLogger.Record(read, null, LastPrompt, LastCallAdvice, PondDiagnostic(), Reader.RotatedPerSeat, Reader.DiscardCountBytes, [.. Reader.UnknownPondTextures],
                [.. Reader.NewlyVisible.Select(n => $"{n.NodeId}@{n.X:0},{n.Y:0}:{n.TextureId}")]);
                return;
            }

            // A call prompt takes priority: the question is whether to claim,
            // not what to throw.
            LastPrompt = ReadPrompt(read);
            LastCallAdvice = BuildCallAdvice(read, LastPrompt);

            LastAdvice = Configuration.Advisor.Recommend(read.Table);
            GameLogger.Record(read, LastAdvice, LastPrompt, LastCallAdvice, PondDiagnostic(), Reader.RotatedPerSeat, Reader.DiscardCountBytes, [.. Reader.UnknownPondTextures],
                [.. Reader.NewlyVisible.Select(n => $"{n.NodeId}@{n.X:0},{n.Y:0}:{n.TextureId}")]);
            readFailureLogged = false;
        }
        catch (Exception ex)
        {
            LastAdvice = null;
            LastCallAdvice = null;
            LastPrompt = CallPrompt.None;
            LastRead = BoardRead.Failed($"Read failed: {ex.Message}");

            // The tick fires several times a second; log the streak once.
            if (!readFailureLogged)
            {
                readFailureLogged = true;
                Log.Error(ex, "Mahjong Advisor read failed (suppressing repeats until it recovers).");
            }
        }
    }

    /// <summary>
    /// The chi shape-picker (state 25) offers no button labels of its own, so the
    /// advice from the prompt that opened it is carried through instead of blanking.
    /// </summary>
    private CallAdvice? BuildCallAdvice(BoardRead read, CallPrompt prompt)
    {
        if (read.Table is null)
            return null;

        // A win is taken, full stop. Nothing about identifying tiles or reading
        // the board should be able to stand between the player and a ron.
        if (prompt.Available.Contains(CallKind.Ron) || prompt.Available.Contains(CallKind.Tsumo))
            return callAdvisor.Recommend(read.Table, prompt.ClaimedTile, prompt.Available);

        if (prompt.Available.Count > 0 && prompt.HasClaimedTile)
            return callAdvisor.Recommend(read.Table, prompt.ClaimedTile, prompt.Available);

        // Riichi and tsumo are declared on our own turn, so there is no claimed
        // tile to identify and none is needed.
        if (prompt.Available.Count > 0 && prompt.Available.All(IsSelfDeclaration))
            return callAdvisor.Recommend(read.Table, -1, prompt.Available);

        // The tile could not be pinned down, but the recommendation may still be
        // certain: if every tile that could legally have been claimed gives the
        // same answer, that answer is safe to show.
        if (prompt.Available.Count > 0)
        {
            var candidates = CallPromptReader.LegalClaims(read.Table.Hand, prompt.Available);
            if (candidates.Count > 0)
            {
                CallAdvice? agreed = null;
                foreach (int tile in candidates)
                {
                    var option = callAdvisor.Recommend(read.Table, tile, prompt.Available);
                    if (agreed is null)
                    {
                        agreed = option;
                        continue;
                    }

                    if (agreed.Best?.Kind != option.Best?.Kind)
                        return null;
                }

                if (agreed?.Best is not null)
                {
                    string tiles = string.Join(" or ", candidates.Select(Tiles.Describe));
                    return agreed with
                    {
                        Headline = $"{agreed.Headline} (couldn't tell if it's {tiles}, but the answer is the same either way)",
                    };
                }
            }
        }

        int chiSelect = read.Profile?.StateCodes.ChiVariantSelect ?? 25;
        if (read.StateCode == chiSelect && LastCallAdvice is not null)
            return LastCallAdvice;

        return null;
    }

    /// <summary>
    /// Calls that need no claimed tile identified. A win is always taken, and a
    /// kan offered without a claim is declared from our own hand.
    /// </summary>
    private static bool IsSelfDeclaration(CallKind kind) =>
        kind is CallKind.Riichi or CallKind.Tsumo or CallKind.Pass
            or CallKind.Ron or CallKind.Kan;

    /// <summary>
    /// Dumps every visible node with its position. Run this right after a riichi
    /// appears on screen and the declaration marker will be in the file.
    /// </summary>
    private unsafe void DumpVisibleNodes()
    {
        var unit = Reader.TryGetAddon(out var profile);
        if (unit is null || profile is null)
        {
            Log.Information("[MahjongAdvisor] No mahjong table open.");
            return;
        }

        NodeInspector.BeginRead(unit, profile.TileTextureBase);
        var nodes = NodeInspector.SnapshotVisible(unit);
        string dir = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "logs");
        string path = Path.Combine(dir, $"visible-{DateTime.Now:yyyyMMdd-HHmmss}.tsv");

        try
        {
            Directory.CreateDirectory(dir);
            var lines = new List<string> { "nodeId\tx\ty\ttexture" };
            lines.AddRange(nodes
                .OrderBy(n => n.Y)
                .ThenBy(n => n.X)
                .Select(n => $"{n.NodeId}\t{n.X:0.0}\t{n.Y:0.0}\t{n.TextureId}"));
            File.WriteAllLines(path, lines);
            Log.Information($"[MahjongAdvisor] Wrote {nodes.Count} visible nodes to {path}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MahjongAdvisor] Could not write the visible-node dump.");
        }
    }

    /// <summary>Writes the board's tile geometry next to the game logs.</summary>
    private unsafe void DumpGeometry()
    {
        var unit = Reader.TryGetAddon(out var profile);
        if (unit is null || profile is null)
        {
            Log.Information("[MahjongAdvisor] No mahjong table open.");
            return;
        }

        NodeInspector.BeginRead(unit, profile.TileTextureBase);
        var lines = NodeInspector.DumpGeometry(unit, profile.TileTextureBase);
        string dir = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "logs");
        string path = Path.Combine(dir, $"geometry-{DateTime.Now:yyyyMMdd-HHmmss}.tsv");

        try
        {
            Directory.CreateDirectory(dir);
            var header = new List<string> { "tile\tx\ty\trot\tancestry" };
            header.AddRange(lines);
            File.WriteAllLines(path, header);
            Log.Information($"[MahjongAdvisor] Wrote {lines.Count} tiles to {path}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MahjongAdvisor] Could not write the geometry dump.");
        }
    }

    private string PondDiagnostic()
    {
        if (Reader.DiscardArrays.RelativeOffset is not null)
            return $"memory: {Reader.DiscardArrays.Status}";
        if (Reader.AutoPondNodeIds.Length == 4)
            return $"nodes {string.Join(",", Reader.AutoPondNodeIds)} | mem: {Reader.DiscardArrays.Status}";
        return $"{Reader.LastAutoMap?.Diagnostic ?? "none"} | mem: {Reader.DiscardArrays.Status}";
    }

    /// <summary>
    /// Per-seat meld readout for validating the meld container mapping in game.
    /// The threat model's strongest input is the called-set count, so this is the
    /// first thing to check if readiness looks too low.
    /// </summary>
    private void DumpMelds()
    {
        var read = LastRead;
        if (read?.Table is null)
        {
            Log.Information("[MahjongAdvisor] No mahjong table open.");
            return;
        }

        var table = read.Table;
        bool known = table.Observations.HasFlag(Observed.OpponentMelds);
        Log.Information($"[MahjongAdvisor] Meld read: {(known ? "observed" : "NOT observed")}");

        string[] names = ["Self", "Shimocha", "Toimen", "Kamicha"];
        for (int seat = 0; seat < 4; seat++)
        {
            var view = table.Seats[seat];
            string tiles = view.MeldTiles.Count == 0
                ? "(none)"
                : string.Join(" ", view.MeldTiles.Select(Tiles.Name));
            Log.Information($"  {names[seat]}: {view.MeldCount} meld(s), {tiles}");
        }

        if (read.Profile is { } profile)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                Log.Information(
                    $"  containers {names[seat]}: {string.Join(",", profile.MeldNodeIdsForSeat(seat))}");
            }
        }
    }

    private unsafe CallPrompt ReadPrompt(BoardRead read)
    {
        if (read.Profile is null)
            return CallPrompt.None;

        var unit = Reader.TryGetAddon(out var profile);
        if (unit is null || profile is null)
            return CallPrompt.None;

        // A call can only be offered on someone else's discard. If the last pond
        // that grew was ours, the opponent's tile has not been observed yet;
        // passing our own tile here would make the advisor claim a tile that is
        // already in our pond.
        int claimed = Reader.LastDiscardSeat == 0 ? -1 : Reader.LastDiscardTile;

        return PromptReader.Read(
            unit, profile, read.StateCode, claimed, read.Table?.Hand,
            Reader.RecentBoardAdditions);
    }

    private void OnCommand(string command, string args)
    {
        string arg = args.Trim().ToLowerInvariant();
        switch (arg)
        {
            case "":
            case "show":
                ShowOverlay();
                break;
            case "hide":
                HideOverlay();
                break;
            case "toggle":
                ToggleOverlay();
                break;
            case "setup":
            case "config":
                OpenSetup();
                break;
            case "log":
                Configuration.LogGames = !Configuration.LogGames;
                GameLogger.Enabled = Configuration.LogGames;
                if (Configuration.LogGames)
                    GameLogger.StartNewGame();
                else
                    GameLogger.Flush();
                SaveConfiguration();
                Log.Information($"Game logging {(Configuration.LogGames ? "on" : "off")}.");
                break;
            case "geometry":
                DumpGeometry();
                break;
            case "snap":
            case "riichi":
                DumpVisibleNodes();
                break;
            case "nodes":
                DebugWindow.IsOpen = true;
                break;
            case "melds":
                DumpMelds();
                break;
            default:
                Log.Information(
                    $"Unknown argument '{arg}'. Use: /mjadv, /mjadv hide, /mjadv toggle, /mjadv setup, "
                    + "/mjadv nodes, /mjadv melds, /mjadv log, /mjadv geometry, /mjadv snap.");
                break;
        }
    }

    public void ShowOverlay()
    {
        Configuration.ShowOverlay = true;
        AdvisorWindow.IsOpen = true;
        AdvisorWindow.RequestPositionReset();
        SaveConfiguration();
    }

    private void HideOverlay()
    {
        Configuration.ShowOverlay = false;
        AdvisorWindow.IsOpen = false;
        SaveConfiguration();
    }

    private void ToggleOverlay()
    {
        if (Configuration.ShowOverlay)
            HideOverlay();
        else
            ShowOverlay();
    }

    private void OpenSetup()
    {
        DebugWindow.IsOpen = true;
    }

    public void SaveConfiguration() => PluginInterface.SavePluginConfig(Configuration);

    public void SaveLayoutProfile(LayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string dir = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "layouts");
        string path = Path.Combine(dir, $"{profile.AddonName.ToLowerInvariant()}.json");
        try
        {
            LayoutProfileLoader.Save(path, profile);
            Log.Information($"Saved layout profile to {path}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save layout profile.");
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSetup;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleOverlay;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        GameLogger.Dispose();
    }
}
