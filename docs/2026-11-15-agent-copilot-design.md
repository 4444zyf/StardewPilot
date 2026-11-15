# Agent copilot: design

Date: 2026-11-15
Status: agreed in design interview, revised after review the same day. All five build steps built and tested in-game; released as v0.1.0 on GitHub (combat is basic)

## What it is

A copilot for Stardew Valley 1.6 on PC (SMAPI). It advises a new player and can also control the player's farmer.

- Works in single-player and co-op. It always drives the local player's own farmer, never a separate bot character.
- Personal project for now, kept publishable.

## Structure

Two processes:

1. **Bridge mod** (C#, SMAPI, .NET 6). Exposes game state and actions over a local WebSocket.
2. **Agent** ([pi](https://github.com/earendil-works/pi), TypeScript, Node 22.19+). Runs alongside the game. A pi extension registers the bridge's state and actions as tools.

pi cannot run inside the game, which is why there are two processes. During development pi is started by hand, so the agent can be restarted without restarting the game. For players, the mod starts a small Node wrapper that runs pi in RPC mode and serves a local settings page for sign-in and model choice; see [the settings and login design](2026-11-15-agent-settings-and-login-design.md).

The bridge is our own code. Its schema stays MCP-compatible so pi is not a lock-in.

**The agent gets Stardew tools only.** pi is a coding agent and ships with shell and file-editing tools. These must be switched off, and its coding system prompt replaced, before the agent is connected to a game. pi 1.5.2 supports this with `--no-tools` and a `--tools <names>` allowlist, so the agent runs with only the Stardew tools enabled. Replacing the system prompt is still to be checked in step 1.

Text that comes from the game (other players' chat, farm and player names, text added by other mods) is treated as data, never as instructions.

Bridge rules:

- The WebSocket listens on localhost only and requires a random token.
- All game actions are queued onto the game's main thread.
- Settings live in SMAPI's standard `config.json`.

## Model access

All model access goes through pi. The mod never handles credentials.

Order of preference:

1. Codex/ChatGPT subscription login. pi 1.5.2 has it built in as provider `openai-codex` ("OpenAI (ChatGPT Plus/Pro)"), and the settings page runs its sign-in. A real sign-in by the owner is still to be done.
2. An OpenAI or other provider API key (billed per use).
3. Local models (Ollama, llama.cpp) through pi's `models.json`.

Develop against a strong cloud model first, so model weakness does not hide design problems.

**Test provider (development):** Alibaba Cloud Model Studio token plan, Beijing region, OpenAI-compatible endpoint `https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1`. It is configured in pi as provider `qwen-token-plan`, with `qwen3.8-max` as the default model. Checked on 2026-11-15: qwen3.8-max and qwen3.8-flash both make correct tool calls in roughly 5–7 seconds. The API key lives outside the repo, in a file readable only by its owner, and pi reads it at request time.

## Guide behaviour

- Chat happens in a private in-game overlay, not the shared game chat. The copilot replies in the language the player types; tips default to the game's language.
- Proactive tips are triggered by cheap rule checks in C# and worded by the model. Example triggers: crops unwatered in the evening, rain tomorrow, a birthday today, a festival tomorrow, a bundle item about to be sold, low energy far from home.
- Time-critical warnings do not wait for the model. A warning that must land before the player acts (about to sell or ship a bundle item, about to pass out) is shown instantly as fixed text from C#. The model may add an explanation afterwards.
- Tips are capped per game day. Chattiness setting: off / important only / talkative.
- Spoilers are held back by default. The rule: the guide may talk about anything the save has already unlocked or recorded (locations opened, items in the collections tab, NPCs met, mail received, bundles visible in the Community Center) plus what today needs. Everything else is a spoiler unless the player asks directly. "Tell me everything" is a setting.
- Facts come from live game data (crop tables, gift tastes, bundle contents, shop stock), so they stay correct with other mods installed. The model's own memory is used only for strategy. No screenshots.

## Autopilot behaviour

- The model plans; deterministic C# macros execute (for example "water all crops", "buy 15 parsnip seeds at Pierre's"). Low-level primitives (walk to tile, face, use tool) remain as a fallback.
- Two ways to run: follow the player's commands, or play by itself toward a standing goal the player sets in plain language. With no goal set, it follows a default balanced first-year progression: a written checklist shipped with the mod (crops each season, tool upgrades, Community Center bundles in season, friendships, first animals), drafted in step 4. Each morning it writes a short day plan the player can read and edit.
- It plays legitimately: it walks, spends energy and time, and respects shop stock and prices. Menus are driven through the game's own code, not simulated mouse clicks.
- Movement and tool-use input from the player cancels the autopilot at once. Input inside the copilot's own windows (typing in the chat, answering a confirmation) does not. A hotkey toggles it.
- The autopilot stops itself, and hands control back, when a cutscene, festival or dialogue it did not start begins.
- End-of-day rules, enforced in C# regardless of the plan: stop work and walk to bed in time to be asleep before 2am, and stop tool use before energy reaches exhaustion. Passing out costs gold, so these are not left to the model.
- Self-play has a daily model budget (tokens or requests, set in config). When it runs out, the agent finishes the current macro, goes to bed if the day is late, and tells the player.
- Irreversible actions need confirmation unless a standing permission is set: selling or shipping, spending above a gold threshold, gifting, trashing, and anything touching another player's property in co-op. Full self-play switches confirmations off through one explicit setting.
- Each macro returns done / partly done with reason / failed with reason. A watchdog stops a macro that makes no progress for a few seconds. The agent gets at most two re-plans, then stops and tells the player what went wrong.
- Fishing and combat are handed back to the player at first.

## Time and memory

- Single-player: the game pauses while the agent thinks or chats. Autopilot macros run in real time.
- Co-op: the game cannot pause freely, so the agent thinks in the background and re-checks state before acting.
- Each game day starts a fresh agent session, seeded with the notes file, the standing goal, yesterday's summary and the current state. The agent writes the summary at bedtime.
- Memory follows the game's save. Stardew saves only when the player sleeps, and quitting mid-day discards the day. Notes and summaries written during a day are held as pending and committed only when the game reports that it has saved. If the day is discarded, so are they.
- Notes are a plain text file, readable and editable by the player. They hold goals, preferences and what the agent has learned about the farm. They live in the mod's own data folder, keyed by farm and player, not next to the save: a co-op guest has no local save file.

## Co-op limits

- A guest's game may only hold fresh data for the areas where players currently are (from memory, verify in step 1). State tools must say when data for an area may be stale, and the agent must not plan on it without going there.
- The agent never acts on another player's property without confirmation, even in full self-play.

## Build order

Each step is usable by itself.

1. **Bridge**: read-only game state over the WebSocket, pi extension with read tools, chat in pi's terminal.
2. **Guide in-game**: chat overlay and proactive tips.
3. **Commanded autopilot**: walk-to, then macros for watering, harvesting, planting, shopping, chests and shipping. Starts with an experiment on walking between maps (farm to Pierre's), which is the largest unknown: the game's pathfinder is believed to work within one map only, so cross-map travel needs our own route planning over the map exits.
4. **Self-play**: the daily planner working toward the standing goal.
5. **Later**: fishing controller, mines and combat, one-click launcher, public release.

## Testing

- On the development VM, in-game tests run on the VNC desktop (`DISPLAY=:1`), so they can be scripted over SSH and watched over VNC.
- Bridge and macros are tested in the running game against a small set of fixed test saves (for example: day 1 of spring, a mid-summer farm with crops and chests, a co-op farm). Each macro has a scripted check: load the save, run the macro, compare the resulting state.
- The pi extension is tested without the game against recorded bridge messages.
- Guide answers are spot-checked against a list of questions with known answers taken from the game's data (prices, seasons, gift tastes, bundle contents).

## Project setup

- One repository holding both halves: the C# mod and the TypeScript pi extension.
- MIT licence. No code may be copied from GPL projects such as Stardew Access.
- Public GitHub repository (`4444zyf/StardewPilot`, default branch `main`). Changes are pushed only when the owner says so.
- No secrets in the repo: API keys stay in files outside it, and SMAPI's generated `config.json` (which will hold the bridge token) is git-ignored.

## Development environment

Set up and checked on 2026-11-15. A test mod built against the installed game and loaded in it.

- Ubuntu 24.04 VM with no GPU (software OpenGL) and no audio device, 6 cores, about 6 GB RAM.
- Stardew Valley 1.6.15 (Linux, Steam) with SMAPI 4.5.2. The game runs on an XFCE desktop over VNC (display `:1`, localhost only, reached through an SSH tunnel). Steam must be running to start it.
- .NET SDK 10, building mods that target .NET 6. `Pathoschild.Stardew.ModBuildConfig` finds the game folder by itself and copies the built mod into `Mods/`.
- Node 22.23 and pi 1.5.2, using the test provider described under Model access.
- RAM is the tight resource: game, Steam, a build and pi together leave little headroom. Close Steam's other windows while testing.

## Background facts

- SMAPI does not sandbox mods. It logs file and process access but blocks nothing, and has no rule against network access.
- Similar projects bridge a SMAPI mod over a socket: stardew-mcp (WebSocket, about 12 tools), StardewWebApi (HTTP and WebSocket).
- StarDojo (NeurIPS 2025) benchmarked agents on 1,000 Stardew tasks. The best model completed 12.7%, and pausing the game while the model thinks mattered for results. This is the main reason for macros over step-by-step control and for structured state over screenshots.
- Verified against the decompiled 1.6.15 source and in-game: `PathFindController` walks the farmer within one map, and because it moves the farmer through `Farmer.MovePosition`, walking into a map-edge warp triggers the game's own warp. Its own warp handling is written for NPCs, so cross-map travel uses our route planner over warps and doors; doors are opened with the game's action code, which also enforces shop hours.
- Verified: a co-op guest's view of a map is live when the map is "always active" or a player is on it; the state tools flag other maps as possibly stale.
- New farms are full of weeds, stones and twigs that the game's pathfinder treats as walls; the autopilot clears a way through them with tools, as a player would.
- SMAPI blocks mods from reflecting into its internals (so no simulated button presses through SMAPI). The fishing controller uses the rod's own functions, and holds the tool button for the minigame by setting the keyboard state the minigame reads.
- pi 1.5.2 supports a ChatGPT/Codex subscription (provider `openai-codex`); an end-to-end sign-in by the owner is still to be done.
