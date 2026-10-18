# Mahjong Advisor

A Doman Mahjong discard advisor for Dalamud, by **Akari**. It reads your hand,
the discards, the dora, the scores and the called melds, then ranks each legal
discard by hand speed against deal-in risk. Auto-play and file logging are on by
default in this build: sit at a casual table and it plays. Manual suggestion
mode is one command away (`/mjadv stop`).

**0.3.9.0** is the current public build. The engine and most of the reader were
played live before this version; the meld mapping, the call-prompt fixes, the
expected-value model and the new auto-play controller still need a live pass.

## Projects

| Project | Contents |
|---|---|
| `Mahjong.Advisor` | Pure engine — shanten, ukeire, safety, threat model, discard and call ranking. No Dalamud reference. |
| `Mahjong.Advisor.Tests` | 193 xUnit tests over the engine, including the shanten vectors. |
| `Mahjong.Advisor.Plugin` | Dalamud shell — addon reading, overlay, control panel, JSONL game log, optional auto-play actuator. |
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
- If detection fails on your client, `/mjadv nodes` opens the control panel with
  the manual override and the node-tree dump. `/mjadv melds` prints the per-seat
  meld readout to the Dalamud log (`/xllog`).

Riichi is read from the marker node beside each pond. The rotation flag and the
score-drop heuristic are dead ends on this client and are no longer used to
declare riichi.

Auto-play and file logging are **on by default** in this build. `/mjadv stop`
turns auto-play off immediately and leaves the overlay as suggestions only;
`/mjadv auto on` turns it back on. Manual play and auto-play use the same reader
and advisor, so the overlay still shows the pick either way.

## UI

The **overlay** is the compact in-game window: current recommendation, the hand
in display order with the suggested tile bracketed, danger, alternatives and the
auto-play state.

The **control panel** is the installer's Open/Settings window. It has five tabs:

| Tab | Contents |
|---|---|
| Advisor | table state, recommendation, opponents, observation flags and read diagnostics |
| Automation | auto-play master switch, pon/chi/kan policy, result advancement and timing |
| Setup | pond/meld/hand-slot detection, manual override, node-tree and tile dumps |
| Settings | overlay visibility, logging and advisor tuning |
| About | build, author, commands and data sources |

The installer's **Open** button opens the control panel *and* the overlay;
**Settings** opens the control panel directly on the Settings tab.

## Auto-play (casual tables only)

The simplest way to run a test is:

```text
/mjadv session     # auto-play on, logging on, fresh log file
<play 2-3 casual hanchan>
/mjadv bundle      # zip of logs + config + layouts + last state
```

`/mjadv session` is also a button in the Automation tab. `/mjadv bundle` opens
the folder containing the zip when it finishes.

Auto-play replays the client's own FireCallback protocol:

- discard: `[15, iconId]` then `[7, slot]`
- call / self-action: `[11, option]`
- result screens: `[14]` at the agari modal, `[17]` at the end-of-hand transition

It deliberately **does not** advance the new-hand screen. The game fires that
acknowledgement itself; sending a second one desynced live tables in the
reference implementation.

The state machine also handles the NPC-table quirks observed in the first two
logged games:

- **State 15** is also the generic “other players are acting” state. It is only
  treated as a call prompt when real call buttons are readable; otherwise the
  overlay says “Waiting for other players” instead of showing stale advice.
- **State 22** is the forced discard after declaring riichi. The bot cuts the
  drawn tile immediately instead of waiting for the game’s timeout.
- **State 27** is the between-hands result/score screen. The game advances this
  itself, so the bot waits and shows “Result screen” rather than firing an
  unverified opcode.
- **Late-wall defence.** When an opponent is a concrete threat and the wall is
  down to the last four go-arounds, any hand that is not tenpai folds. Pushing
  a 1-shanten no-riichi hand there dealt 12,000 in a live game.
- A **self-kan bar** (`Kan / Pass` at state 6) is ignored. The client does not
  accept the Pass callback there; the bot just discards normally, which
  declines the kan. Firing Pass four times was the freeze in the first
  0.3.3.0 run.
- A **complete open hand with no yaku is not a win.** The bot checks for a yaku
  before firing the unlabelled tsumo option; otherwise it keeps discarding.
  Treating “complete” as “can win” was the second freeze.
- Call prompts now prefer the **newest board addition** over a stale pond tile.
  The second live log claimed a 6z dragon Pon while the actual claim was 9m,
  which opened a no-yaku hand. The log now records the pond’s last discard and
  seat so this is visible next time.
- Discard advice is only computed at our actual decision states (6/30/28/22), so
  a call or result screen can no longer leave an old “discard X” on the overlay.

Defaults are conservative:

- **Pon**: taken when the advisor says it is worth opening the hand.
- **Ron / tsumo**: always taken.
- **Riichi**: declared when the advisor says it is the best line.
- **Chi**: automated for single-shape calls. The controller calls chi when the
  advisor ranks it best and exactly one run shape is possible — that path
  completes with no dialog. When two or three shapes are possible the client
  opens its shape-selection surface, which ignores scripted call picks (the
  first picker dump proves it), so the controller passes instead and logs how
  many shapes it declined. The picker layout is still dumped once per session
  so shape-specific selection can be built later.
- **Kan**: off. A kan flips another dora indicator for every seat.
- A self-kan prompt that the advisor declines is answered by discarding, which
  the client accepts; firing the Pass callback there does nothing.
- Nine-terminals/honours is aborted unless the hand is a real kokushi attempt
  (thirteen-orphans shanten 2 or better).

The controller waits six seconds after a table appears before sending anything,
waits for a full readable hand, uses humanized think times, and retries a popup
fire up to four times if the prompt animation swallowed it. `/mjadv auto off` or
`/mjadv stop` is the panic switch.

Two extra guards protect the discard path:

- The display-slot tiles must exactly match the hand array before a discard is
  sent. A stale/shifted node id fails closed rather than cutting a tile you do
  not hold.
- After declaring riichi, the follow-up discard is chosen from the tenpai-keeping
  options and is not sent until the Riichi/Pass prompt has closed. The generic
  recommendation is never used for that cut.

## Tuning

The advisor is set to the fitted win-rate values:

| Setting | Value |
|---|---|
| Base hand value | 1300 |
| Caution (deal-in cost) | 5200 |
| Tenpai-at-draw value | 1500 |
| Opponent hand value | 4000 |

A one-time v3 config migration resets old custom values to these. After that,
changes made in Settings persist normally. `Reset to recommended` in Settings
restores the same values.

## Commands

| Command | Action |
|---|---|
| `/mjadv` / `/mjadv show` | Show the overlay and reset its position |
| `/mjadv hide` / `/mjadv toggle` | Hide or toggle the overlay |
| `/mjadv setup` / `/mjadv config` | Open the control panel |
| `/mjadv session` | Auto-play + logging on, start a fresh log file |
| `/mjadv bundle` | Zip the recent logs, config, layouts and last state |
| `/mjadv folder` | Open the logs/diagnostics folder |
| `/mjadv auto` / `/mjadv auto [on/off]` | Toggle or set auto-play |
| `/mjadv stop` | Turn auto-play off |
| `/mjadv nodes` | Open the control panel on the node-mapping tab |
| `/mjadv melds` | Log each seat's meld count and tiles, plus the configured node families |
| `/mjadv hand` | Log the display-slot order, tile and icon id of every visible hand tile |
| `/mjadv options` | Log the current popup buttons and their `[11, n]` indices |
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
- Opening the control panel only when asked.
- The auto-play controller: discard handshake, option indices, popup retries,
  result advancement and riichi follow-up. The protocol is taken from the
  reference solver's live captures, but this integration has not been played.

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

## Testing auto-play

Use a **casual** Doman Mahjong table, not a ranked/PvP mode. Suggested first
session:

1. Run `/mjadv session` (or click **Start auto-play session** in the Automation
   tab).
2. Play one hand and watch the first discard. If the wrong tile leaves, run
   `/mjadv stop` immediately.
3. Play 2–3 full hanchan (about 8–12 hands).
4. Run `/mjadv bundle` and send the zip it opens. It contains the JSONL logs,
   config, layouts, Dalamud log tail and a full last-read snapshot.
5. Toggle `/mjadv stop` and confirm manual play returns to suggestion-only.

The controller logs every sent input under `[MahjongAdvisor][auto]` in
`/xllog`, writes `Kind: "auto"` records for sent inputs, and writes
`Kind: "auto-status"` records whenever its status changes (waiting, settling,
stalled, hand-slot mismatch, and so on). That status trail is the main thing
that makes a failed run diagnosable.

## Diagnostics

Every decision can be written as one JSONL record. Logging is on by default;
records land in `%APPDATA%\XIVLauncher\pluginConfigs\Mahjong.Advisor.Plugin\logs\`.

Each record carries the build stamp, full table state, observation flags, every
candidate's score, the meld readout, the raw call-prompt values and the nodes
that became visible that tick. Auto-play adds `Kind: "auto"` action records and
`Kind: "auto-status"` records whenever it starts waiting, settles, stalls or
refuses a discard.

`/mjadv bundle` zips the recent logs, config, layouts, the last 5,000 lines of
`dalamud.log` and a full last-read snapshot into
`...\pluginConfigs\Mahjong.Advisor.Plugin\diagnostics\`. The bundle can contain
player names from the log, so review it before sharing.

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
git commit -m "Mahjong Advisor v0.3.9.0"
git push origin main
git tag v0.3.9.0
git push origin v0.3.9.0
```

CI runs the tests, builds the plugin and publishes `MahjongAdvisor.zip` on the
GitHub Release. After the release exists, add the repo to the combined
pluginmaster (`DalamudPlugins/Build-CombinedRepo.ps1`) and push it.

The version-bump rule is absolute: a tag that has been pushed is spent and must
never be reused, including when the only change is a failed build.
