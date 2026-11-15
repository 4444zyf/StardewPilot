# Knowledge sources and Windows verification: design

Date: 2026-11-15
Status: agreed 2026-11-15 after an independent review; the owner left the open decisions to the recommendations below

Two things come next after v0.2.0: giving the guide more knowledge, from a source we can trust, and making sure the mod works on Windows without owning a Windows machine. Other candidate features are listed at the end.

## Part 1: Knowledge

### Problem

The guide answers from 11 state tools plus shop stock. It knows prices, gift tastes, bundles and shop stock, but not where a fish lives, which crop pays best, what a machine makes, or what the museum still needs. For those it falls back on the model's memory, which is wrong often enough to matter and knows nothing about installed mods.

### The authoritative source is the running game

There is one authority: **the game the player is running**, meaning its data files and its code together. It is what will actually happen, for this version, this language, and with the player's other mods applied. The wiki is written from the same data. Where the game's code overrides its data (see fish below), the code is the truth.

The risk is not the source but **our reading of it**. A tool that copies a field from a data file cannot be wrong. A tool that works something out ("this fish can bite here now") contains our understanding of the game's rules, and that can be wrong. So every tool result separates the two:

- **raw**: taken directly from the game's data or the player's records.
- **computed**: worked out by our code from the game's rules. Each computed answer is covered by the answer-check list (see Testing), and the agent can say "by my calculation" for these.

Below the game, in order:

- **The first-year checklist** we already ship: strategy, written by us. Any fact in it must pass the answer-check list.
- **The model's own memory**: strategy only, as in the main design.

### The wiki is not used

The official wiki (stardewvalleywiki.com) was considered as a look-up source and rejected:

- Its `robots.txt` (read 2026-11-15) bans AI agents by name, including ClaudeBot and GPTBot. The operators plainly do not want AI tools reading it. A lookup made for one player's question is not a crawler, but it is the same kind of use, and we respect that.
- Its licence (CC BY-NC-SA 3.0, checked the same day) does not fit an MIT project, so nothing from it can be bundled either.
- Looked-up text would be stored in pi's session files and sent to the model provider, which makes "we don't keep it" impossible to promise.

What the agent may do: **name the wiki page** for the player to read themselves ("the wiki's Fishing page explains this"). No request is made.

Consequence: explanations of things the game's data does not contain (quest walk-throughs, secrets) come from the model's memory and are labelled as such. That is an accepted limit.

### New tools

All read-only. Each returns display names in the game's language plus stable IDs.

| Tool | Answers | Source | Raw or computed |
|---|---|---|---|
| `get_fish_info` | Where and when a fish can be caught: maps, seasons, hours, weather, fishing level. Or, for a map, which fish are possible there, and which are possible right now | `Data/Fish`, `Data/Locations`, the farm's map property, plus a small hand-written table for the mines | Listing: raw. "Possible now": computed |
| `get_crop_plan` | For a season: each crop's seed price and shop, days to grow, regrowth, sell price, harvests still possible if planted today, gold per day | `Data/Crops`, `Data/Objects`, today's shop stock | Fields: raw. Harvest count and gold per day: computed |
| `get_recipes` | Cooking or crafting recipes: ingredients, whether known, what can be made now from the backpack and chests | `Data/CookingRecipes`, `Data/CraftingRecipes`, player records | Raw; "can make now" computed |
| `get_machine_info` | What a machine accepts and produces, and how long it takes | `Data/Machines` | Raw where the data states the output |
| `get_collections` | What is still missing: museum donations, items never shipped, fish never caught, recipes never cooked | Player records; the museum's own "can this be donated" check | Raw |
| `get_npc_schedule` | Where a villager is now and where they will be later today | The schedule the game has loaded for today | Raw |

Known limits, found by reading the game's code, that each tool must handle honestly:

- **Fish.** The game's own "which fish bites" functions cannot be called to answer questions: they roll dice on the game's shared random generator, return one fish, and need a bobber position and tackle. So "possible now" is our own check of the fixed conditions, in the order the game applies them:
  - from the map's spawn rules (`Data/Locations`, plus the `Default` rules every map shares): season, fishing area, fishing level, distance from shore, catch limit, and the rule's condition, checked with the game's condition evaluator;
  - from `Data/Fish`, unless the rule says to ignore it: hours, weather, fishing level.

  It reports "possible", never a chance, and assumes no magic bait or training rod. Rules tied to a part of the map (an area, or bobber and player positions) are reported with that restriction instead of being guessed.

  Where code decides, read from the 1.6.15 source:
  - **Mines.** Stonefish, Ice Pip and Lava Eel (and Cave Jelly on the lava floors) are chosen in code by mine area, before the data rules. This is the one hand-written table: three areas, four items.
  - **Farm.** Some farm types borrow another map's fish through a map property (`FarmFishLocationOverride`). The tool reads the property, so no table is needed.
  - **Island and Railroad.** Their code only adds one-time special items (golden walnuts, a quest item), not fish. The tool ignores them; the spoiler rule would hide them anyway.
  - **Fish ponds and fish frenzies** replace the catch at one spot. Not reported.
  - Maps added by other mods that choose fish in their own code cannot be known. The tool's answer for a modded map says it shows data rules only.
- **Machines.** Some outputs are decided by game code, not data (the data only names the function). The tool returns those as "decided by the game" with our note for the unmodded machines.
- **Recipes.** How a recipe is learned is only partly in the recipe data (friendship and skill unlocks are; TV, mail and shop sources are elsewhere). The tool reports the source when the data has it and "source not in data" otherwise.
- **Schedules.** Confirmed for the host's game only. Whether a co-op guest has other villagers' schedules loaded must be checked first; if not, the tool says it is unavailable for guests.
- **Crop plan.** Assumes no fertiliser and the current map's seasons, and says so in the result.
- **Broken mods.** Entries that fail to load are skipped and counted ("3 entries could not be read").

Not in this round: animal and building info, and a "how to get this item" index across shops, machines and monsters. Both are large and spoiler-heavy for what they add.

### Spoilers and size

- An entry is "reached" when the save has it on record: the map has been visited, the item is in the collections, the recipe is known, the machine has been crafted or seen. Each tool marks entries not reached, and the agent leaves them out unless asked directly or "tell me everything" is on. `get_collections` lists missing items only by category count unless the player asks for names.
- Results are filtered by arguments and capped. Asking about one fish never returns the fish table.

### Testing

- The answer-check list from the main design grows: questions with known answers for unmodded 1.6.15 (where and when Catfish bites; days for Parsnip to grow; what a Keg makes from Hops; fish on mine floors 20, 60 and 100). Every computed answer has at least one entry. Each tool is run against the list in the game.
- For fish, one experiment: fish a spot repeatedly and confirm every catch is in the tool's "possible now" list.
- One run with a content mod that changes a crop, to show answers follow the modded data.

## Part 2: Windows without a Windows machine

### Problem

Most players are on Windows. Everything so far was built and played on Linux. Nobody on the project has a Windows machine.

### How much is at risk

The mod is one .NET assembly; the same file runs on every system, and almost all of it talks only to the game and SMAPI. The agent is TypeScript on Node. The parts that differ by system:

| Part | Where | Risk on Windows |
|---|---|---|
| Starting pi | wrapper, mod launcher | **High; expected to fail today.** npm installs `pi` on Windows as a `.cmd` script. Node refuses to start a `.cmd` without a shell, and the mod's direct start uses the bare name `pi`, which .NET does not resolve to `pi.cmd`. |
| Status file | wrapper writes, mod reads every second | **Medium; likely bug.** The wrapper replaces the file by renaming over it. On Windows that fails while the mod has it open, and the failure is only logged, so the game would show a stale state. |
| Bridge listener | mod | Medium, unknown. .NET's HTTP listener goes through a different system component on Windows. |
| Finding pi's package | wrapper | Medium. npm's layout is handled; pnpm, volta and nvm-windows are untested. |
| Node not found | mod launcher | Medium. A game started from Steam may not see the same PATH as a terminal. |
| Paths | both | Low to medium. The game folder has spaces (`Program Files (x86)`); user names may be non-ASCII. |
| Stopping pi with the game | wrapper | Medium. Closing stdin works everywhere; the fallback kill differs. |
| Config folder, opening the browser, private files | both | Low. |
| Antivirus and SmartScreen | player's machine | Unknown. A game starting `node` and opening a local port may trigger a prompt. Cannot be tested by machines; see step 4. |

Also found: on macOS the agent's two halves compute the config folder slightly differently. Both will use one shared function.

### Step 1: fix what reading the code already shows

Status: implemented on Linux on 2026-11-15 (all four points); unit tests pass. Proof on Windows comes with step 2.

- **Start pi without the `.cmd`.** The wrapper already finds pi's package folder. It reads the entry file from the package's own `bin` field and starts it with the same Node that runs the wrapper. The only program the mod starts is then `node`, a normal executable on every system.
- **Direct start without the wrapper** (`Launcher.UseWrapper` off) is a development option. On Windows it is not supported; the mod says so instead of failing.
- **Status file.** The wrapper retries the rename briefly and keeps trying on the next change; the mod opens the file in a mode that allows it to be replaced.
- **Node not found.** The mod's error names `Launcher.NodeCommand` and says to set the full path.

### Step 2: test on real Windows, automatically

GitHub Actions gives free Windows and Linux machines to public repositories. A workflow runs on every push, on both:

- The agent's unit tests, including the pi supervisor against a fake pi. That test is skipped on Windows today because its fake is a shell script; after step 1 the fake is a JavaScript file and runs everywhere.
- A wrapper check against the real pi (version pinned): install pi with npm, start the wrapper's setup mode with a temporary config folder that has a space and a non-ASCII character in its path, call the page API, sign in with a made-up key, and confirm the files land where the mod expects.
- A start-and-stop check: the wrapper starts the real pi with an empty extension, then its stdin is closed; pi must be gone within a few seconds. (On Linux, pi with an empty extension stays up without calling a model; to be confirmed on the Windows machine.)
- The status file replaced repeatedly while a reader holds it open.

No real API key is used, and no secrets are stored in the repository settings. macOS can be added to the same workflow when a Mac player asks.

Limit: these machines run Windows Server. They say nothing about a home PC's antivirus or SmartScreen.

### Step 3: test the mod's system-specific code

The game cannot run in CI: its files are licensed and may not be uploaded, and the mod project cannot even be compiled without them. So the system-specific C# (config folder, private file, reading the status file, opening a link, starting a process, and a minimal start of the HTTP listener on 127.0.0.1) moves into source files that use neither the game, SMAPI, nor the game's JSON library. A separate small test project includes those same files and runs on the Windows machine in CI.

This is real work, not a move: today those classes log through SMAPI and parse JSON with the game's library. They need a plain logging callback and .NET's built-in JSON. The test project targets .NET 6, like the mod, so the workflow installs that runtime.

What stays untested by machines is the game-facing code, which is the same on every system.

### Step 4: one real play-through by a person

Machines cannot press F8 in the game on Windows, or meet a home PC's antivirus. That needs a person once per release:

- A console command `pilot_diagnose` prints a self-check: system, versions, whether Node and pi were found and where, wrapper state, bridge port, file locations. It never prints the token, keys or the settings link. A tester pastes it into an issue.
- The release notes ask for Windows testers and link an issue form with a five-minute checklist: start the game, `/settings`, add a key, ask a question, run one autopilot action.
- Until someone has done it, the README says plainly: "Windows: automated tests pass; not yet played by a person."

### Considered and not chosen

- **Windows build of the game under Wine on the dev VM.** It would test Wine, not Windows: starting processes and replacing open files are exactly where Wine differs. It also needs the owner's Steam login to download the Windows files, and the VM is short of RAM.
- **A Windows VM inside the dev VM.** The dev machine is itself a VM with 6 GB of RAM.
- **A Windows evaluation VM on the owner's own computer.** This would work: Microsoft offers a free 90-day evaluation image, and one Steam purchase covers all systems. It is the fallback if no tester turns up; it costs the owner an afternoon.

## Other candidate features

In suggested order after the two above. Each gets its own design when started.

1. **In-game settings menu** (Generic Mod Config Menu) for chattiness, hotkeys, budgets and confirmations.
2. **More autopilot jobs**: animals, machines, tool upgrades, bundle delivery. Self-play stalls without them after the first weeks.
3. **A long self-play run** (a full season) to find where it gets stuck, then fix those places.
4. **Better combat and deeper mines**: approach and avoid monsters, eat when hurt, use the elevator.
5. **Usage view**: requests and tokens used today, on the settings page and in the chat.
6. **Translations** of the mod's own messages and the settings page, Chinese first.

## Decisions

Settled on 2026-11-15, following the recommendations:

1. A public call for Windows testers goes into the next release notes, with the issue form.
2. Order: Windows step 1 first (it fixes bugs in the released 0.2.0), then the knowledge tools, then the CI work (steps 2 and 3), then `pilot_diagnose` and the tester call with the next release.
3. Walk-through style questions are answered from the model's memory and labelled as such.
