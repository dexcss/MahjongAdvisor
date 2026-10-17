# Mahjong Advisor

A Doman Mahjong discard advisor for Dalamud. It reads your hand, the discards,
the dora, the scores and the called melds, then ranks each legal discard by hand
speed against deal-in risk. It is a suggestion overlay — it never clicks
anything for you.

**0.2.0.0** is the first public build. The engine and most of the reader were
played live before this version; the meld mapping, the call-prompt fixes and the
new expected-value model are the parts that still need a live pass.

## Projects

| Project | Contents |
|---|---|
| `Mahjong.Advisor` | Pure engine — shanten, ukeire, safety, threat model, discard and call ranking. No Dalamud reference. |
| `Mahjong.Advisor.Tests` | 190 xUnit tests over the engine, including the shanten vectors. |
| `Mahjong.Advisor.Plugin` | Dalamud shell — addon reading, overlay, setup window, JSONL game log. |
| `Mahjong.Advisor.Simulator` | Offline console app for tuning parameters against identical deals. |

The engine stays on `net8.0` so it loads under whatever runtime the host
provides. The plugin uses `Dalamud.NET.Sdk/15.0.0`, which resolves to
`net10.0-windows` at API level 15. The tests and simulator target `net9.0`, so a
local build needs both the .NET 9 and .NET 10 runtimes.

Clean-room implementation. Nothing is derived from the AGPL solver source; the
addon offsets and node ids are observable facts about the game client.

## Build and test

```powershell
dotnet test Mahjong.Advisor.Tests/Mahjong.Advisor.Tests.csproj -c Release
dotnet build Mahjong.Advisor.Plugin/Mahjong.Advisor.Plugin.csproj -c Release
```

The Release build runs DalamudPackager and produces
`Mahjong.Advisor.Plugin/bin/Release/Mahjong.Advisor.Plugin/latest.zip`.

Simulator sweeps:

```powershell
dotnet run --project Mahjong.Advisor.Simulator -c Release -- 200
```

200 hands is a smell test; 1000 is the smallest run whose differences are worth
acting on.

## First run

Open a Doman Mahjong table. The overlay appears by itself; there is no mapping
step.

- Discard piles are found automatically from their tile counts and positions.
  Memory reading takes over once enough tiles are down.
- Called-meld strips use the known node families (`112`-`115`). A mapping is
  only accepted when our own meld count agrees with the hand shape, so a wrong
  id degrades to "meld count unknown" instead of silently reading zero melds.
- If detection fails on your client, `/mjadv nodes` opens the setup window with
  the manual override and the node-tree dump. `/mjadv melds` prints the per-seat
  meld readout to the Dalamud log (`/xllog`).

Riichi is read from the marker node beside each pond. The rotation flag and the
score-drop heuristic are dead ends on this client and are no longer used to
declare riichi.

## Commands

| Command | Action |
|---|---|
| `/mjadv` / `/mjadv show` | Show the overlay and reset its position |
| `/mjadv hide` / `/mjadv toggle` | Hide or toggle the overlay |
| `/mjadv setup` / `/mjadv config` | Open the setup window |
| `/mjadv nodes` | Open setup on the node-mapping tab |
| `/mjadv melds` | Log each seat's meld count and tiles, plus the configured node families |
| `/mjadv log` | Toggle the JSONL decision log |
| `/mjadv geometry` | Dump tile geometry to a TSV file |
| `/mjadv snap` | Dump every visible node id to a TSV file (for snapshot diffing) |

## What is verified, and what is not

**Verified in game**

- Own hand, discards, dora, scores and wall count.
- Opponent riichi markers on all four seats.
- Self-riichi lock.
- Kuikae-forbidden discards after a call.
- Discard advice on every turn.
- The automatic pond mapping, including its self-correction when a pile goes
  lopsided.

**Fixed, but not yet played in this build**

- The meld container mapping and the readiness prior that uses it.
- Ron / tsumo / self-kan prompt handling.
- The expected-value discard model and the push horizon of 2.
- Opening the setup window only when asked.

**Known limits**

- Meld mapping is a best-effort default. If it is rejected, readiness falls back
  to a quiet-hand floor plus part of the one-call rate; the overlay says so.
- Seat and round winds are still not read, so yakuhai detection is deliberately
  pessimistic.
- Alt-character retainers are irrelevant here, but the same principle applies to
  any "my own" detection: the client hides information.
- The simulator runs about 17% win / 10% deal-in / 12% draw against real play's
  roughly 21% / 13% / 16%. Use it to rank settings against each other, never to
  judge whether the advisor is good.

## Diagnostics

Every decision can be written as one JSONL record. Turn it on with `/mjadv log`
or the setup window; records land in
`%APPDATA%\XIVLauncher\pluginConfigs\Mahjong.Advisor.Plugin\logs\`.

Each record carries the build stamp, full table state, observation flags, every
candidate's score, the meld readout, the raw call-prompt values and the nodes
that became visible that tick. When something looks wrong in game, the log
usually already contains the proof.

## Data sources

Win rates and opponent readiness come from `HououTables`, measured across about
1.2 million games in Tenhou's top lobby
(`chienshyong/houou-statistics`, forked from `Euophrys/houou-analysis`).

## Release

Bump all four places together:

1. `Mahjong.Advisor.Plugin/Mahjong.Advisor.Plugin.csproj` — `<Version>`
2. `Mahjong.Advisor.Plugin/Mahjong.Advisor.Plugin.json` — `AssemblyVersion` and `TestingAssemblyVersion`
3. `repo.json` — `AssemblyVersion`, `TestingAssemblyVersion`, `Changelog` and the download links
4. `Logging/GameLogger.cs` — `BuildStamp`

Then:

```powershell
git add .
git commit -m "Mahjong Advisor v0.2.0.0"
git push origin main
git tag v0.2.0.0
git push origin v0.2.0.0
```

CI runs the tests, builds the plugin and publishes `MahjongAdvisor.zip` on the
GitHub Release. After the release exists, add the repo to the combined
pluginmaster (`DalamudPlugins/Build-CombinedRepo.ps1`) and push it.

The version-bump rule is absolute: a tag that has been pushed is spent and must
never be reused, including when the only change is a failed build.
