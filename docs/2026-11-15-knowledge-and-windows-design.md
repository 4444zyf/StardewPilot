# Knowledge sources and Windows verification: design

Date: 2026-11-15
Status: all work items implemented by 2026-11-16 (K6 guest case and a person playing on Windows still open); agreed after an independent review; the owner left the open decisions to the recommendations below. Knowledge section re-checked against the 1.6.15 game code the same day

Two things come next after v0.2.0: giving the guide more knowledge, from a source we can trust, and making sure the mod works on Windows without owning a Windows machine. Other candidate features are listed at the end.

## Part 1: Knowledge

### Problem

The guide answers from 11 state tools plus shop stock. It knows prices, gift tastes, bundles and shop stock, but not where a fish lives, which crop pays best, what a machine makes, or what the museum still needs. For those it falls back on the model's memory, which is wrong often enough to matter and knows nothing about installed mods.

### The authoritative source is the running game

There is one authority: **the game the player is running**, meaning its data files and its code together. It is what will actually happen, for this version, this language, and with the player's other mods applied. The wiki is written from the same data. Where the game's code overrides its data (see fish below), the code is the truth.

The risk is not the source but **our reading of it**. So every tool result says how each part was obtained:

- **raw**: copied from the game's data or the player's records. Cannot be wrong.
- **asked**: the answer of one of the game's own functions, called the way the game calls it (for example its "what would this machine make from this item" check, or its "how many can I craft" count). As right as the game.
- **computed**: worked out by our code from our understanding of the rules. This is the only kind that can be wrong. Each computed answer is covered by the answer-check list (see Testing), and the agent can say "by my calculation" for these.

Rule for building tools: prefer raw, then asked, and compute only what the game offers no function for.

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

| Tool | Answers | Source | How obtained |
|---|---|---|---|
| `get_fish_info` | Where and when a fish can be caught: maps, seasons, hours, weather, fishing level. Or, for a map, which fish are possible there, and which are possible right now | `Data/Fish`, `Data/Locations`, the farm's map property, plus a small hand-written table for the mines | Listing: raw. "Possible now": computed |
| `get_crop_plan` | For a season: each crop's seed price and shop, days to grow, regrowth, sell price, harvests still possible if planted today, gold per day | `Data/Crops`, today's shop stock, the game's sell-price function | Fields: raw. Sell price: asked. Harvest count and gold per day: computed |
| `get_recipes` | Cooking or crafting recipes: ingredients, whether known, how it is learned, how many can be made now from the backpack and chests | `Data/CookingRecipes`, `Data/CraftingRecipes`, player records, the game's recipe functions | Ingredients, known: raw. "Can make now": asked. How learned: raw where the recipe data says, otherwise not given |
| `get_machine_info` | What a machine accepts; what it would make from a given item, and how long that takes | `Data/Machines`, the game's machine check in its "probe" mode | Accepted items: raw. Output for an item: asked |
| `get_collections` | What is still missing: museum donations, items never shipped, fish never caught, recipes never cooked | Player records; the museum's own "can this be donated" function | Raw and asked |
| `get_npc_schedule` | Where a villager is now and where they will be later today | The schedule the game has loaded for today | Raw on the host; see limits for guests |

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
- **Machines.** The game has a "probe" mode for exactly this question: the same function it runs when a player holds an item up to a machine, which reports the output without changing anything. The tool uses it, so machines whose output is decided in code (cask, seed maker, geode crusher, deconstructor and others) need no notes from us. Where a machine picks one of several outputs at random, the tool lists every candidate whose condition passes instead of one roll. Open point for implementation: the probe expects a machine placed in a map; whether a machine that is not placed anywhere works for every machine type must be tried, and any that do not are reported as "place one to check".
- **Recipes.** "How many can I make" is the game's own count, including nearby chests the way the crafting menu counts them. How a recipe is learned is in the recipe data for three kinds: known from the start, a skill level (parsed by the game's own function), and friendship with a villager. Recipes from the TV, shops or special mail are not marked in the recipe data; the tool says "source not in recipe data" for those.
- **Museum.** The game has a function for "can this item still be donated", based on item tags, so the tool asks it for every item instead of keeping a list. This also covers items added by other mods.
- **Schedules.** On the host, the tool reads the day's schedule the game has built: times, target maps and tiles. A schedule can change during the day (rain, festivals, marriage); the tool returns what is loaded now. In co-op the host decides schedules, and only the schedule's name is sent to guests. For guests the tool first checks whether the guest's game has built the schedule itself; if not, it reports where the villager is now and that later stops are unknown.
- **Crop plan.** Assumes no fertiliser and the current map's seasons, and says so in the result. Sell prices come from the game's own price function, so professions are included.
- **Conditions with chance.** Some data conditions include a random roll. For "possible" answers the tool tells the game's condition evaluator to skip those, so "possible" means "can happen", not "will happen this time".
- **Calling the game's functions.** Several of them draw from the game's shared random generator. That is harmless (the game calls the same functions every time the player hovers an item), but a single random result is not an answer, which is why the fish tool does not use the game's catch function.
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
- **Status file.** The wrapper retries the rename briefly; the mod holds the file open only for the read. (CI showed that Windows refuses a rename over an open file even when the reader allows deletion, so the retry is what makes it work.)
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

## Work items

Each can be set as a goal by itself. "Done" is what must be true to call it finished.

| # | Item | Done when | State |
|---|---|---|---|
| W1 | Windows step 1: start-up fixes | Unit tests pass; the game on Linux still starts and connects | Done 2026-11-15 (commit `c8fdc6b`); confirmed on Windows by the W2 smoke test (pi started through node). Not yet in a release |
| K1 | `get_fish_info` | Answer-check entries for Catfish, a Default-rule fish, and mine floors 20, 60 and 100 pass; the repeated-catch experiment finds no fish outside "possible now" | Done 2026-11-15. Catfish, Woodskip, Sunfish and the Beach list match the data; mine floors 20, 60, 100 give Stonefish, Ice Pip, Lava Eel (+ Ghostfish from the mine data). 10-cast experiment at the Beach: all 8 fish caught were in "possible now" |
| K2 | `get_crop_plan` | Answer-check entries pass for a single-harvest and a regrowing crop; gold per day matches a hand calculation | Done 2026-11-15. Cauliflower (12 days, 175g, seed 80g) = 7.9 g/day and Blueberry (4 harvests of 3, 50g, seed 80g, 27 days) = 19.3 g/day match hand calculations |
| K3 | `get_recipes` | "Can make now" matches the crafting menu for three recipes; learned-by matches for one of each kind | Done 2026-11-15. Can-make counts match arithmetic for 120 Wood (60 fences, 12 gates, 2 chests); learned-by correct for start (Fried Egg), skill (Sprinkler, Farming 2), friendship (Salad, Emily 3 hearts), TV (Maki Roll, reported as not in data) |
| K4 | `get_machine_info` | Keg with Hops, Furnace with Copper Ore, Seed Maker and Cask give the outputs the game produces; the placed-or-not question is answered | Done 2026-11-15. Keg+Hops = Pale Ale 37h30m; Furnace+5 Copper Ore+Coal = Copper Bar; Seed Maker and Cask answered by the game's code; an unplaced machine works for these, and a placed one is used when it exists. Code-decided outputs are flagged as one example |
| K5 | `get_collections` | Counts match the Collections tab and the museum on the test save | Done 2026-11-15. Reads the game's own Collections page, so totals are the game's (154 shipped, 72 fish, 42 artifacts, 53 minerals, 81 dishes in 1.6.15) |
| K6 | `get_npc_schedule` | Host: two villagers found where the tool said, at two times of day. Guest behaviour checked in a two-player session | Host done 2026-11-15: Pierre and Lewis were where the plan said at 8:50am and 11:00am. Guest not checked: needs a second game instance (the VM lacks the RAM); the tool reports "plan unknown" for guests |
| K7 | Guide uses the tools | System prompt tells the agent when to use each tool and how to word asked and computed answers; five guide questions answered correctly in the game | Done 2026-11-15. Five questions (Catfish, best crop, Keg+Hops, where is Pierre, Salad recipe) answered correctly in the game; prompt tightened after the model added unmarked facts from memory |
| W2 | CI on Windows and Linux (step 2) | Workflow green on both; wrapper and real pi start and stop on Windows | Done 2026-11-16. CI green on ubuntu-latest and windows-latest: unit tests, and the wrapper starting and stopping the real pi 1.5.2 in a folder with a space and non-ASCII letters (run 43782948380) |
| W3 | System-specific C# under test (step 3) | Test project green on the Windows machine | Done 2026-11-16. 11/11 platform tests pass on Windows 10.0.26100 and Linux in CI. The first Windows run found that a rename over an open file fails even with delete sharing; the wrapper's retry handles it (5 retries in CI) |
| W4 | `pilot_diagnose`, tester call, README note (step 4) | Command prints no secrets (checked by test); issue form exists; next release notes carry the call | pilot_diagnose (tested: no token or link in output, also checked in the real SMAPI log), issue form, README note done. Tester call goes into the next release notes |

Suggested order: K1 to K7, then W2, W3, W4, then a release that carries W1 to W4 and the tools.

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
