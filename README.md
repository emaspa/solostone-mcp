# solostone-mcp

An MCP server that lets Claude play [SoloStone](https://github.com/KeyStarr/SoloStone) to reproduce bugs, check fixes and hunt for new bugs. It is a separate tool, not part of SoloStone. It needs SoloStone source checkouts, and for the live layer a Hearthstone client folder; it ships neither.

It has two layers.

**Engine.** A .NET worker (`worker/`) is linked against the engine of any SoloStone worktree. Its assembly is named `EngineProof` because the engine already grants that name access to its internals. The server builds one worker per worktree and caches it under `~/.cache/solostone-mcp/workers/<key>`. The key covers HEAD, the uncommitted engine diff and the worker source. A scenario can run against several worktrees in one call, so one call shows a bug failing on main and passing on the fix branch.

**Live client.** This layer drives the game client's test-control endpoint, which only hook builds have. On Linux it uses a separate lab install (`lab.sh`), so your own install, saves and window are never touched. The launcher runs the game in a network namespace with loopback down. `live_connect` brings loopback up inside that namespace (`nsenter --keep-caps`, no root needed) and talks to the endpoint through `nsenter … socat`. `target: "vm"` drives a Windows machine over `ssh -W` instead (the host in `SOLOSTONE_VM`).

## Setup

```
claude mcp add solostone -s user -e SOLOSTONE_MCP_DOTNET=$HOME/.dotnet/dotnet -- python3 ~/solostone-mcp/server/solostone_mcp.py
```

The server needs only Python's standard library. The worker needs the .NET 10 SDK. Environment variables:

- `SOLOSTONE_MCP_ENGINE`: the default engine worktree (`~/solostone-merge`).
- `SOLOSTONE_LAB`: the lab root (`~/.local/share/solostone-lab`).
- `SOLOSTONE_VM`: the ssh host of the VM (`win11`).

Findings are saved in `~/.local/state/solostone-mcp/findings/`.

## The lab

```
./lab.sh build ~/solostone-merge   # a --hooks build of HEAD plus uncommitted changes plus patches/*.patch
./lab.sh install                   # into ~/.local/share/solostone-lab, set up from ~/games/Hearthstone
./lab.sh play                      # headless in gamescope (--window for a normal window)
./lab.sh stop
```

`patches/lab-wine-hooks.patch` lets a hook build start under Wine. Without it, the plugin's isolation guard refuses any account in the Administrators group, and under Wine every account is in it. That is the same reason the guard already skips the check for Steam Deck launches. The patch exists only in lab builds. Never apply it to a tree you push.

## Tools

- **Engine:** `engine_info`, `card_search`, `scenario_run`, `seed_search`.
- **Sessions:** `game_new`, `game_state`, `game_entity`, `game_options`, `game_act`, `game_history`, `game_assert`, `game_diag`, `game_checkpoint`, `game_restore`, `game_close`.
- **Bug hunting:** `fuzz_run`, `finding_list`, `finding_get`, `finding_set`, `finding_verify`, `finding_replay`.
- **Match journals** from the desk or lab install (read-only): `journal_list`, `journal_read`.
- **Live client:** `live_connect`, `live_state`, `live_options`, `live_play`, `live_act`, `live_end_turn`, `live_choose`, `live_wait_idle`, `live_entities`, `live_fx`, `live_ui`, `live_press`, `live_screenshot`, `live_logs`, `live_call`, `live_disconnect`.

`live_screenshot` returns the frame the game rendered, captured by Unity inside the game process. It never captures the desktop.

`live_play` plays a card, hero power or attack by its entity id and returns the spell telemetry the client logged for it: which controller took the block, which sub-spells loaded and which spell states ran. That shows whether an animation played without watching the screen. The plugin keeps only the last 512 telemetry rows, so read them right after the action.

A debug scenario sets up a live match: write `reports/debug-scenario` with `runId=` (line 1 of `reports/plugin-loaded.txt`) and keys such as `humanHand=`, `npcBoard=`, `humanMana=` and `humanSeat=first` before pressing Play (see `plugin/Npc/DebugScenario.cs` in SoloStone). Cards it adds after the mulligan miss Start of Game, so test those with a deck that holds the card.

The scenario format is described in the `scenario_run` tool description. Examples are in `scenarios/`.

## Fuzz invariants

| Id | Checks |
|---|---|
| E1 | The engine throws while listing options or processing one. |
| E2 | An option the engine offered is refused. Choices are answered through `Game.TryAnswer`, as the game does. |
| E3 | No options while the game runs, or the game outlasts the action limit. |
| E4 | An impossible state: more than 7 minions, 10 cards in hand or 5 secrets; a dead minion or hero still in play; two copies of one secret; an entity listed in a zone its tags say it is not in. |
| E6 | The same seed and the same picks end in different states. |
| E8 | An attack that never resolves still sends ATTACKING or DEFENDING, so the client draws a hit that didn't happen. |

## License

AGPL-3.0, the same as SoloStone. See `LICENSE`.
