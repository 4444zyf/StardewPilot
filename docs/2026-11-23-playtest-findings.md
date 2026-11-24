# Playtest findings, 2026-11-23

A self-play run on the dev save (farm "Test", Y2 summer 15 onwards, English), with Claude watching and stepping in through the chat as the player would. Each finding has what happened, the evidence, and a suggested fix for the next release. Severity: **high** (breaks play or loses progress), **medium** (wrong result or wasted time), **low** (wording, polish).

Setup: game 1.6.15 with SMAPI 4.5.2, the mod built from `main` (d2fb66b) plus the Chinese naming change, copilot model qwen4-max. Goal given in the chat: "play by yourself, goal: earn 2,000g by the end of summer".

## Summary

All 21 are fixed as of 2026-11-24 (each finding ends with its **Status**); a second playtest from the same morning is described at the end.

| # | Finding | Kind | Severity |
| --- | --- | --- | --- |
| 1 | Opening the chat during a door warp leaves the farmer at the old map's tile on the new map | Bug | High |
| 11 | An exit found blocked stays blocked for the rest of the day, even after the blocker is gone | Bug | High |
| 17 | The 11:30pm bedtime rule only acts while a job is running; with the way home blocked the farmer is left out | Bug | High |
| 18 | `go_to_bed` cannot use Pelican Town's west exit to the Bus Stop | Bug | High |
| 20 | "Continue self-play" never turns self-play back on; the next day nobody plays | Bug | High |
| 2 | A door warp is requested hundreds of times per exit | Bug | Medium |
| 3 | When walled in, the autopilot never clears its own way out | Gap | Medium |
| 8 | A blocked door says "blocked" but not by what, so the model guesses | Gap | Medium |
| 9 | While the copilot waits for the player, the clock keeps running | UX | Medium |
| 10 | `use_tool` reports "finished" without saying what it hit; probing with `walk_to` trips the three-failure stop | Gap | Medium |
| 13 | In self-play the copilot stops to ask about shipping in the chat, although the mod asks in the game | UX | Medium |
| 14 | `fish` gives up on "every route blocked" after trying only the farm's south exit | Bug | Medium |
| 16 | After a halt the farmer stands idle for hours with energy to spare | UX | Medium |
| 19 | The diary counts the copilot's idle time in self-play as the player's own play | Bug | Medium |
| 4 | "No known way from Test Farm to Test Farm": the message names the farm, not the coop | Wording | Low |
| 5 | Starting self-play kills a running scheduled routine, and the message offers F7 anyway | UX | Low |
| 6 | The diary calls a routine that started by itself "asked by the player" | Bug | Low |
| 7 | The diary stores place names in the game's language | Bug | Low |
| 12 | The bundle warning fires when `ship_items` already kept one back | Bug | Low |
| 15 | Harvest dates are left to the model, which says "Summer 29" | Gap | Low |
| 21 | A click on the night's last menu cancels the 6am routine; the briefing still promises it | Bug | Low |

## The run in brief

- **Summer 15, 6:00.** Self-play started ("earn 2,000g by the end of summer"). Opening the chat during the 6am routine's door warp stranded the farmer among trees (1); self-play halted after three "No path" failures and asked for help.
- **7:00-12:40.** Freed by the player, the copilot could not reach the coop: a wild tree blocked the door (8). Told what it was, it chopped it down with `use_tool` (10), then the coop stayed "blocked" for the day (11) until the player suggested going in through the door with `interact`. It collected 93 eggs.
- **13:00-15:00.** Shipped the eggs and 2 Sunfish (~4,735g), bought and planted 4 blueberries and 2 corn. Fishing failed on the farm's overgrown south exit (14), and the farmer stood idle until 7:50pm (16).
- **20:30-0:10.** Fished the town river and caught Bream. `go_to_bed` could not leave town to the west (18), and the bedtime rule did nothing (17); the player walked the farmer home.
- **Summer 16.** 4,810g: goal beaten. But self-play was still off (20), so the farmer stayed in bed.

The dev save's farm is unusually overgrown (trees close to the coop, barn and south exit), which brought out the path problems quickly; a normal farm would hit them less often, but the same code paths fail.

## Fix first

1. The bedtime rule and getting home (17, 18): passing out costs gold and is what the rule exists to prevent.
2. Self-play state (20, 16, 9, 13): a halt should pause, "continue" should resume, and waiting should not burn the day.
3. Paths (1, 11, 14, 3, 8): the stranding warp, the all-day blocked memory, giving up too early, and naming or clearing what blocks the way.
4. The diary (19, 6, 7): habits are learned from it, so it must only record what the player did, in English.

## Findings

### 1. Opening the chat during a door warp strands the farmer (high)

**What happened.** At 6:00 the "long walk" routine (`water_crops`) walked the farmer out of the farmhouse. The player pressed F8 at the moment the farmer stepped on the door. The farmer arrived on the Farm at tile (9,11), the farmhouse's own door coordinates, inside a stand of trees with no path out. Every later job failed with "No path" and self-play stopped.

**Reproduced** once more on purpose: put the farmer in the farmhouse (`debug warp FarmHouse 9 8`), call `water_crops` through the bridge, press F8 as soon as the log shows `Warping to Farm`. Result: `location: Farm, tile: (9,11)`. Without F8 the farmer lands correctly at (64,15).

**Evidence.** `[water_crops] walk start: 2 steps to (9,12)` → warp → `walk end at (9,11)` on the Farm; the job then reported `Done`.

**Suggested fix.** In the walker, treat a location change as the end of the walk step and re-read the farmer's tile from the new location before continuing; never set the farmer's position after a warp. Also check that a menu opening during a warp does not interrupt the game's own warp (the job pauses while the chat is open; the position update may be the part that is skipped). Add a guard: if after a warp the farmer is on a tile with no path to the map's warps, warp to the arrival tile of the door just used.


**Status.** Fixed: the walker stops the game's path walker as soon as a warp starts and clears it after (`Navigation.FinishWarp`). Retested: F8 during the 6am door warp, farmer lands at (64,15).
### 2. Door warp requested hundreds of times (medium)

**What happened.** One walk out of the farmhouse logged `Warping to Farm` 313 times (another time 45 times in one second).

**Suggested fix.** Once a warp is requested, stop pressing the door / stepping on the warp tile until the location has changed (or a short timeout passes). Likely related to finding 1: repeated warp requests race with whatever opens or closes during the warp.


**Status.** Fixed with 1: one warp request per exit (retested: 1 instead of 313).
### 3. Walled in, the autopilot does not clear its way (medium)

**What happened.** Trapped among trees and stones, with an axe and a pickaxe in the toolbar and full energy, every job failed with "No path to tile". The copilot asked the player to "chop/pick the debris around them".

**Suggested fix.** When no path exists on the current map, let the walker plan through debris it can clear (weeds, stones, twigs, small stumps; not trees the player planted, and not fruit trees) and clear it with the right tool, within the energy rules. At least offer a `clear_way` action the model can call when stuck.


**Status.** Fixed: new `clear_way` action cuts wild trees and stumps and clears debris on the way (never fruit or tapped trees). Retested: it cut its way to the coop and, on its own, out of the coop pocket.
### 4. "No known way from Test Farm to Test Farm" (low)

**What happened.** `tend_animals` failed with "No known way from Test Farm to Test Farm. The way to its door at 53,22 is blocked." The target was the coop.

**Suggested fix.** Name the destination building ("No way to the Coop: its door at (53,22) is blocked").


**Status.** Fixed: building interiors are named by their building ("No known way from Test Farm to Coop").
### 5. Self-play replaces a running routine, then offers F7 (low)

**What happened.** The 6am routine was running when self-play started. The chat said: `"long walk" (every morning) stopped: Replaced by a new job: tend_animals. F7 starts it again while it is due.` Routines are not offered during self-play, so F7 does nothing useful.

**Suggested fix.** When self-play starts, let a running routine finish (or tell the model it is running), and drop the F7 hint while self-play is on.


**Status.** Fixed: during self-play the stop message has no F7 hint.
### 6. Diary: a routine that started by itself is "asked by the player" (low)

**What happened.** The day file for Y2-summer-14 says `6:00-6:10 farmhouse: copilot, asked by the player: water_crops, run_routine`, but the routine started by itself at 6am.

**Suggested fix.** Give the watcher's slice a control kind for routines started by schedule ("by itself") and word it so in the diary and day summary.


**Status.** Fixed: new diary control "scheduled" ("copilot, a routine that started by its schedule").
### 7. Diary place names follow the game's language (low)

**What happened.** After a day played in Russian, the diary lines read `6:00-6:10 фермерский дом` and `6:10-2:00 ферма`. The diary and `habits.md` are meant to stay English (the habit finder compares place names across days, so mixed languages also split one habit into two).

**Suggested fix.** Record internal location names (or English display names) in the activity files, and translate only when showing text to the player.


**Status.** Fixed: the diary, activity log and habits use English names (`ApprenticeService.AgentNames`); player-facing routine text keeps the game's language.
### 8. "The way to its door is blocked", but not by what (medium)

**What happened.** A wild tree grows on (53,23), the tile in front of the coop's door. `tend_animals` failed with "The way to its door at 53,22 is blocked", and `get_location` lists buildings, chests and NPCs but not trees, stones or fences. The model guessed "likely a fence or gate the tools can't see", pressed the action button on it, and asked the player to open a gate. The player had to look and tell it that it was a tree.

**Suggested fix.** Name the blocker in the failure ("a tree", "a stone", "a fence", "an animal") and add the farm's blocking terrain near targets to `get_location` (or a small `what_is_at` tool). With finding 3, the autopilot could then clear it itself.


**Status.** Fixed: failures say what is in the way ("In the way: a tree at (53,23)") and report `blockedBy` (with health) to the agent (`Blockers`). Animals or villagers standing in a gap are named first.
### 9. The clock runs while the copilot waits for the player (medium)

**What happened.** After it stopped and asked the player to unblock the coop (around 7:00am), the game clock kept running: by the time the player read the chat it was 10:20am, on the last day to plant blueberries. `PauseWhileThinking` covers thinking only.

**Suggested fix.** In single-player, when self-play stops to ask the player something, pause the clock (or say in the message that time is passing), as the chat window does.


**Status.** Fixed: while self-play waits for the player (`selfplay/waiting`) the clock is held in single-player, with a HUD line; any move or chat answer ends the wait.
### 10. `use_tool` gives no outcome, and probing counts as failure (medium)

**What happened.** Told to chop the tree, the copilot called `use_tool` (Axe) on (53,23) and got `use_tool finished` each time, with no word on what was hit or how much the tree has left. To find out whether the tree was gone it called `walk_to (53,23)` between swings; each "No path" counted toward the three-failures rule, and the mod halted it again after 7 hits: "my walk-checks between chops counted as failures and tripped its 3-strike limit".

**Suggested fix.** Make `use_tool` report its effect: what was on the tile before and after (tree with its health or stage, stone, debris, nothing), energy spent. Then the model needs no probing. Consider not counting a `walk_to` "No path" toward the stop when the model is clearly working on the blocker, or reset the count after a successful action.


**Status.** Fixed: `use_tool` reports `before`, `after` (with health), `walkableNow` and `energyUsed`. Retested: "a tree (health 10)" → "a tree (health 8)".
### 11. A blocked exit stays blocked all day (high)

**What happened.** After 17 axe hits the tree in front of the coop was gone and the farmer stood on (53,23), right below the door. `tend_animals` still failed at once: "The way to its door at 53,22 is blocked". `Navigation.BlockedExits` records an exit that failed once and is only cleared at day start (`ModEntry`, `DayStarted`), so the coop stayed unreachable for the rest of the day. The copilot read it as "a pathfinding quirk with building doors".

**Suggested fix.** Remember a blocked exit only for the current route plan (or a few game minutes), and always retry when the farmer stands next to it. At least clear the memory when the player or a `use_tool` changes the tiles next to an exit.


**Status.** Fixed: blocked exits are remembered only for the walk that found them.
### 12. False bundle warning after shipping (low)

**What happened.** `ship_items` shipped 2 of 3 Sunfish and kept one for the River Fish bundle, as designed. The bin warning still fired: "Sunfish is still needed for the River Fish. Take it back out of the shipping bin before you sleep". The model then also retried shipping the eggs twice ("Nothing to ship. Egg: none in the backpack"), as if unsure what the first call had shipped.

**Suggested fix.** Skip the bin warning for items the autopilot shipped while keeping one back (or when the player still holds enough for the bundle). Make the `ship_items` summary list what went into the bin and what was kept, so the model does not retry.


**Status.** Fixed: no bin warning when the backpack still holds the item (`ship_items` kept one).
### 13. Self-play stops to ask about shipping in the chat (medium)

**What happened.** With 93 eggs collected, self-play stopped and asked in the chat "May I ship the 93 eggs?", and waited for a reply while the clock ran. The mod already asks in the game before shipping (unless the player gave a standing permission), so the chat question is a second ask that halts the day.

**Suggested fix.** Tell the model in self-play to call `ship_items` and let the mod's in-game question do the asking; reserve chat questions for things the mod cannot ask.


**Status.** Fixed in the prompt: the mod asks in the game, so the agent calls the tool instead of asking in the chat. Retested: it shipped without a chat question.
### 14. `fish`: "every route was blocked" too early (medium)

**What happened.** From the farm, `fish` tried the farm's south exit to Cindersap Forest three times (tiles (40..42,65), all overgrown) and failed: "Could not reach Cindersap Forest: every route was blocked." The longer way (Farm → Bus Stop → Town → Forest) was open, and Town and the Mountain have fishable water too. The copilot reported fishing as impossible for the day.

**Suggested fix.** When the nearest exits are blocked, let the route planner try the other ways to the target before giving up (the blocked exits should only rule out themselves). In `fish`, if one place cannot be reached, fall back to the nearest other water the farmer can reach.


**Status.** Fixed: a failed exit also rules out its neighbouring warp tiles, and only failures count toward giving up (4), so the other ways are tried. Retested: Forest, Bus Stop and Backwoods were all tried.
### 15. Harvest dates computed by the model (low)

**What happened.** `get_crops` returns `nextHarvestInDays: 14`; on Summer 15 the copilot wrote "corn (Summer 29, carries into fall)". Summer has 28 days: it is Fall 1.

**Suggested fix.** Return the harvest date as well as the day count (`readyOn: "Fall 1"`), computed by the mod.


**Status.** Fixed: `get_crops` adds `nextHarvestOn` ("Fall 1, Year 2").
### 16. Idle after a halt (medium)

**What happened.** After `fish` failed (2:40pm) the copilot hit the three-failures rule, posted its summary and suggested going to bed. The farmer stood at the Bus Stop until the player read the chat at 7:50pm, with 194 energy left: five game hours with nothing done.

**Suggested fix.** In self-play, after a halt let the model plan something else that does not depend on the failing route (the rule is meant to stop a loop of the same failure, not the day). If it really must wait for the player, pause the clock (see finding 9).


**Status.** Fixed in the extension: after a halt, self-play goes on once with a note to do something else; only a second halt in a row waits for the player.
### 17. The bedtime rule needs a running job (high)

**What happened.** At 10:50pm `go_to_bed` failed (finding 18) and the copilot stopped. The farmer reached the farm through the forest but stood in an overgrown pocket with no path to the house. At 11:30pm nothing happened: `EnforceEndOfDay` (`ModEntry`) returns at once when no job is running, so the "work stops and the farmer goes to bed" rule never fired. Left alone, the farmer would have passed out at 2am (the player walked it home at 11:50pm).

**Suggested fix.** In self-play (and after any autopilot job today), run the end-of-day rule whether or not a job is running. If `go_to_bed` cannot find a way, warn the player at once in the HUD (not only in the chat) while there is still time, and try the other ways home (finding 14).


**Status.** Fixed: the end-of-day rule also runs when nothing is running but the copilot has the farmer (self-play, or a job ended and the player has not moved), and warns in the HUD if no way home is found. Retested at 11:30pm with no job running: the farmer went to bed.
### 18. `go_to_bed` cannot leave Pelican Town by the west exit (high)

**What happened.** From the town river at 10:50pm, `go_to_bed` tried the exit to the Bus Stop at (-1,55), (-1,54) and (-1,53): each attempt walked to (6,89) and stopped with "Could not get to tile (-1,53) in Pelican Town: the way is blocked". The same exit worked in the other direction that afternoon. It then left by the forest instead.

**Suggested fix.** Check the walk to map-edge warps at x = -1: plan to the last tile inside the map and step out, as the Bus Stop side does. Log what blocks the path (an NPC at night, a closed gate, a fence) to tell a real blocker from a planner bug.


**Status.** Fixed as far as found: the game's walker gives up after a few seconds when a villager stands still on the path; the walker now notices the stall, goes around villagers and animals, and waits up to ten seconds for one in a gap. (The night failure could not be reproduced by day.)
### 19. Self-play idle time is logged as the player's play (medium)

**What happened.** The Summer 15 diary reads `14:40-20:30 Bus Stop`, `7:10-11:20 the farm` and so on, with no "copilot" mark: those were hours when self-play had halted and the farmer stood waiting for the player. The end-of-day line says "780 hand minutes" for a day the player barely touched. Jobs the copilot chose in self-play are written "copilot, asked by the player: fish". Apprentice mode learns habits from "hand" time only, so this feeds it the copilot's own wandering as the player's habits.

**Suggested fix.** While self-play is on, or while the copilot is waiting on the player after a halt, record the time as copilot time (or as "idle"), never as hand time. Mark self-play jobs as "self-play", not "asked by the player". Finding 6 is the same mix-up for scheduled routines.


**Status.** Fixed: new diary control "idle" from the end of a copilot job until the player moves; self-play stays on while it waits, so its time stays "self-play".
### 20. "Continue self-play" does not turn self-play back on (high)

**What happened.** The first halt (finding 1) switched self-play off ("Self-play off." in the chat). The player then said "Continue self-play, same goal" twice. The copilot carried on job by job for the rest of Summer 15, but never switched self-play back on. On Summer 16 nobody played: the farmer was still in bed at 10:40am, with the last day to plant melons ticking away.

**Suggested fix.** Make a halt pause self-play rather than end it, and resume on the player's "continue" (or have the extension's prompt say that resuming means calling `set_self_play`). Show "Self-play on/off" in the HUD so the player can see the state.


**Status.** Fixed: a halt no longer switches self-play off (16, 9), and the prompt says to call `set_self_play` when the player asks to continue. Retested: after a halt it waited with self-play on and went on when answered.
### 21. A click on the night's last menu cancels the 6am routine; the briefing still promises it (low)

**What happened.** The level-up screen after sleeping was closed with a click; a click arriving just after the new day started was logged as `Routines: input (MouseLeft); nothing starts by itself today`. The morning briefing then said: "Your 'long walk' routine runs by itself this morning if you leave the farmer untouched." (In this run the late click may have come from the test harness clicking through night menus; a player double-clicking OK would do the same.)

**Suggested fix.** Ignore input for a moment after the day starts (as `IsTakeControlInput` already does after cutscenes), and word the briefing from the routine's real state for today.


**Status.** Fixed: clicks and cursor moves while a menu is open (the night's level-up and shipping screens are still open after the new day starts), and in the moment after one closes, do not cancel a routine that starts by itself. A first fix (only the first second of the day) was not enough: the second playtest showed the level-up screen closing minutes into the new day.
## Worked well

- Clear, honest summaries in the chat: what was shipped and for how much, what was planted and when it is ready, what was skipped and why.
- The day closed cleanly once home: bed at 0:10, `plan.md`, `goal.txt` and a good day summary written when the day was saved, shipping paid out (75g → 4,810g).
- Three failed jobs in a row → self-play stopped, with a clear message: what is blocked, what the player can do, and why it matters (93 eggs in the auto-grabber, last day to plant blueberries).

## Second playtest, 2026-11-24

Same save (restored to the morning of Summer 15), same goal, same 6am routine, the chat opened again during the routine's door warp. The fixes above, built from the working tree.

- **6:00.** The farmer came out of the door at (64,15): no stranding, one warp.
- **6:10-9:30.** Self-play cleared its own way to the coop with `clear_way`, collected the 93 eggs, reached the barn and milked the cow, and shipped without a chat question. Two chickens kept standing in the one-tile gaps through the trees; failures named them ("In the way: Zasko at (56,23)"). After the second halt in a row it waited for the player with the clock held at 9:00am and self-play still on; one answer from the player ("the chickens have moved, try again") and it went on.
- **9:30-14:00.** Bought and planted blueberries and a tomato, planted the corn, fished in town, cut three trees in all, and when the way out of the farm failed again used `clear_way` on its own. At 14:00, with the way to town closed by the chickens once more and 93 energy left, it wrote its summary and went to bed.
- **Night and Summer 16.** 5,045g (first run: 4,810g, with six interventions against one). The diary marks every stretch "copilot in self-play", in English, with 0 hand minutes; harvest dates are right ("Fall 1"). On Summer 16 self-play went straight on with the animals, without the player.

Still to watch:

- Chickens in one-tile gaps. The walker now waits up to ten seconds for an animal to move and re-checks every half second (built after this run, not yet played). If that is not enough on farms like this one, `clear_way` could prefer opening a second way round, or the agent could close the coop's animal door.
- Going to bed at 2pm when the way out is blocked is safe but wastes the afternoon; with the animal wait above it should happen less. Worth a look in the next run.

