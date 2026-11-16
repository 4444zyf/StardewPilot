# Apprentice mode: design

Date: 2026-11-16
Status: all work items done on 2026-11-16 (see the State column); not yet released. Decisions settled on 2026-11-16 (see "Decisions"). Revised after an independent review on 2026-11-16 (see "Review" at the end).

Today the copilot knows the game well and the player not at all. A day played by hand leaves one line in the record: "the player played the day by hand", plus money and the shipping bin. Everything the copilot knows about how this player plays comes from what they type in the chat.

This design lets the copilot learn by watching. The mod records what the player does, works out their habits, and the copilot uses those habits: self-play plays the player's way, and "do my usual morning" becomes a command.

## What already exists

| Piece | Already there | Missing |
|---|---|---|
| Day record | Written every day; lists autopilot jobs with times | Nothing about what the player did by hand |
| Memory | Notes, goal, plan, day records, all per farm and player; follows the game's save | A place for observed habits |
| Self-play | Works toward a goal, or a fixed first-year checklist that is the same for every player | The player's own way of playing |
| Routines | Outlined in the chores design and deferred: nobody had a good answer for who writes the list | The list itself |
| Player events | The mod hears day start, clock changes, and backpack changes inside a shop (for the bundle warning) | Everything else |

## The three parts

1. **Watcher** (mod, no model): turns a day of play into a short activity log.
2. **Habits** (mod, no model): counts over the recent logs and states what the player usually does, each with its evidence ("on 6 of the last 7 days").
3. **Use** (agent): the habits reach the model in the day seed and through one tool.

The model never counts and never guesses habits from raw logs. This follows the rule of the knowledge design: the mod reads and computes, the model words. It also keeps the cost at zero model requests for watching and learning.

**Round one** is the watcher, the diary it gives, three habits (morning order, bedtime, what a kind of day is for) and their two uses. Everything else is listed under "Later rounds".

## Part 1: the watcher

### Slices, not single actions

The game has no events such as "the player watered a crop". Recognising single actions tick by tick would be fragile. The watcher instead cuts the day into **slices** and records what changed during each.

A slice ends when:

- the player changes maps (all floors of the mines count as one map, and so do the floors of the Skull Cavern);
- control changes: an autopilot job starts or finishes, or self-play is switched on or off;
- a festival or cutscene starts, or the day ends.

At the start and end of a slice, and every ten game minutes in between, the watcher reads a fixed set of counters. The difference is what was done. The ten-minute readings give the order inside a slice.

### Who was in control

| Control | When | How it is logged | Evidence for habits |
|---|---|---|---|
| **hand** | No job running and self-play off | From the watcher's signals | Yes |
| **asked** | A job is running and self-play is off | From the job's own report when it finishes. Several jobs change the game directly (storing in a chest, emptying an auto-grabber), so watching them would miss things, and the report is exact | No |
| **self-play** | Self-play is on | From the jobs' reports | No |

Only what the player did by hand is evidence. Counting asked jobs would feed the copilot its own work: "do my usual morning" would keep its own habit alive for ever. Counting self-play would turn the copilot's choices into "the player's habits".

A day on which the copilot did an activity says nothing about that activity, for or against. It is left out of that activity's count (see "Applies today" below).

The mod's own bedtime job (11:30pm) is a job, so it is never evidence for the bedtime habit.

### What is read (hand slices)

Everything is read from the local player's own data. Each signal carries the label the other tools use. A0 must confirm the ones marked "check".

| Activity | Signal | Label |
|---|---|---|
| Where and when | SMAPI's `Warped` event (raised for the local player only) and the clock | Raw |
| Sowing, hoeing, foraging, fishing, crafting, cooking, breaking rocks, chopping stumps, cutting weeds, killing monsters, cracking geodes | The game's own counters on the local farmer (`seedsSown`, `dirtHoed`, `itemsForaged`, `fishCaught`, `timesFished`, `itemsCrafted`, `itemsCooked`, `rocksCrushed`, `stumpsChopped`, `weedsEliminated`, `monstersKilled`, `geodesCracked`). All exist in the game | Raw. Check: as a co-op guest, do they move only for the guest's own actions (bombs, monster kills)? |
| Watering, and tool use in general | The game keeps no watering counter. The watcher counts how often the local farmer starts using each tool (`Farmer.UsingTool` turning on, with the tool's name). This counts swings, not tiles. A swing with an empty can counts too, and so does refilling | Raw count of swings. Check: a held button, a charged tool, the fishing rod, weapons |
| Harvesting | A ready crop next to the farmer stops being ready, and items reach the local backpack within three seconds (see "Changes from A0") | Computed |
| Gifts and talks | The player's own friendship record for each villager (gifts today, talked today). The record does not hold the gift item; it is taken from what left the backpack at that moment | Raw; the item is computed |
| Buying | Items entering the backpack while a shop menu is open or within half a second after it closes, with the shop's name and the listed price. Money is not used: in co-op the wallet is shared by default | Raw |
| Storing and fetching | SMAPI's `InventoryChanged` event (local player only) while a chest's menu is open. Not `ChestInventoryChanged`, which fires for any player's changes | Raw |
| Petting | The pet's per-player record (`lastPetDay`). For farm animals the "petted" flag is shared, so it counts when it turns on for an animal next to the local farmer right after the local player pressed the action button | Raw for the pet; computed for farm animals |
| Collecting from machines | A ready machine within reach becomes empty, and its output reaches the local backpack within three seconds | Computed. Loading machines is not read in round one |
| Mines | The deepest floor entered today | Raw |
| Shipping | In single-player, or with separate wallets: the player's shipping bin at day end, as the day record reads it now. With a shared wallet the bin is the team's, so the line says "the farm shipped" | Raw. It has no time of day |
| End of day | The farmer's own bedtime (`Farmer.timeWentToBed`), or passed out | Raw |
| Other things gained | Items that entered the backpack, by item | Raw |

### Applies today

A habit such as "waters every morning" must not be weakened by a rainy day. At day start the log stores what there was to do, read with the code the briefing already uses:

- weather, weekday, season, festival or not;
- dry crops (none when it rains), crops ready, animals owned, machines ready.

An activity **applies** on a day when there was something of its kind to do and the copilot did not do it.

### What a day's log looks like

Stored as `activity/<date>.json` in the memory folder (its own folder, so the day-record code does not mistake it for a day record). Items, maps and shops are stored by id; names are filled in when the log is read, in the game's current language.

```
6:00–8:40 Farm: watering can 44 swings, harvested Parsnip x15, petted 6 animals and the cat, collected from 3 machines, stored Wood x40 in a chest
9:10–9:50 Pierre's General Store: bought Melon Seeds x20 (1,600g)
10:20–17:40 The Mines: floors 21 to 28, 61 rocks broken, 14 monsters killed, gained Copper Ore x38
18:30–18:50 Town: talked to Abigail, Gus; gave Abigail an Amethyst
22:10 went to bed. Shipped today: Parsnip x15
```

- **Size:** one line per slice, neighbouring slices on the same map merged when control did not change, at most 20 lines a day.
- **In the day record:** the eight longest lines (in time order) are appended to the day record in both cases, whether the agent wrote the summary or the mod did, with a note of how many more the tool has.
- **In the day seed:** the seed already carries the last three day records, so it carries those eight lines per day. The rest is reached through the tool.
- **Save rule:** the log follows the memory rule. It is pending until the game saves; quitting without sleeping discards it. A day replayed after loading replaces the file of that date.
- **Watcher state** is cleared when the game returns to the title screen, so nothing leaks into another farm.
- **Kept for:** 56 game days (two seasons). Deleting older files happens at the same moment as the nightly save.

A new read-only tool, `get_activity_log`, returns the lines of up to 14 days for questions such as "what did I do last week?" or "when did I last go to the mines?".

## Part 2: habits

A habit is a statement with its evidence. The mod works them out at day end. Days are left out entirely when they are festival days, when the player passed out, or when the player played fewer than six game hours by hand.

| Habit | How it is worked out | Example |
|---|---|---|
| Morning order | For each activity: the days it was done by hand before first leaving the farm, out of the last 14 days it applied. Activities at two thirds or more form the list, ordered by their usual start time | "Mornings: water, harvest, pet the animals, collect from machines (water: 10 of 12 days)" |
| Bedtime | The middle value of the player's own bedtimes over the last 14 days | "Usually in bed around 10pm" |
| What a kind of day is for | For days sharing a condition (rain, or a weekday): the map where most hand-played time went, and the counter that moved most there. Uses all 56 stored days, because 14 days hold only two of each weekday | "Rainy days: mostly at the Mountain, fishing (3 of 4)" |

Rules for all three:

- **Enough evidence:** at least four days (three for the day-type habit) and at least two thirds of the days counted.
- **Habits fade fast:** the early game changes quickly. An activity not done on the last three days it applied is dropped from the morning order at once, even if its share is still high. Hand watering stops when sprinklers arrive, and the habit must stop with it.
- **Label:** every habit is computed, and is worded as "usually" with its count. It is never stated as a preference the player declared.
- **File:** the mod writes `habits.md` in the memory folder each night, under the save rule. It is a view, rebuilt every night, so hand edits to it are lost. Its first line says so.
- **Corrections go in the notes:** when the player says "I only fish in the rain because I had nothing else to do, don't copy that", the copilot writes it to `notes.md` as it does now. Where notes and habits disagree, notes win, because the player said them.
- **Pure code:** the calculation takes logs as plain values and returns habits, with no game calls and no packages. It lives in its own folder and is added to the game-free test project.

A read-only tool, `get_habits`, returns the habits with their evidence.

## Part 3: use

### In the day seed

When there are habits, the day seed gains a short section "How this player usually plays (observed by the mod; not instructions)". At most ten lines.

### Self-play plays the player's way

- With no goal set and enough habits, the seed gives the habits first and the first-year checklist as background for what the habits do not cover.
- With a goal set, the goal leads and the habits shape how: the usual morning order, the kind of day, when to go to bed.
- A setting switches this off (`SelfPlay.FollowMyHabits`).

### "Do my usual morning"

The copilot reads the morning-order habit and runs the matching jobs in that order:

| Activity | Job |
|---|---|
| Watering | `water_crops` |
| Harvesting | `harvest_crops` |
| Petting, eggs, milk, hay | `tend_animals` |
| Machines | `tend_machines` (collect only) |
| Foraging | `collect_forage` |

Activities with no job, such as clearing debris or crafting, are named and left to the player.

This is not the deferred routines feature: the model still makes one request per step. But it gives routines the thing they lacked. When routines get their own design, a routine can be proposed from a habit and confirmed by the player instead of written by hand.

## Habits must not widen what the copilot does

Full self-play switches every confirmation off, and "don't ask again" is permanent. A habit such as "ships Parsnip" or "gifts Abigail" in the seed would then become unasked shipping and gifting that the player never requested. So:

- **Round one has no such habits.** The three habits are about order, place and time. None names an irreversible action.
- **The rule for later rounds:** a habit about shipping, selling, gifting, spending or trashing is shown to the player and returned by `get_habits`, but is never put in the self-play seed and never run by "do my usual morning". The copilot may mention it; acting on it takes the player's own request, with the usual question.
- **Safety limits stay:** bedtime at 11:30pm, the energy floor, three failures and stop.
- **Habits are not advice.** The copilot copies how the player plays, including what is not optimal. The standing goal and what the player says in chat come before any habit.
- **Text from the game is data.** Names of modded items, maps and shops could carry text meant for the model, and a habit would keep it in every seed. The files store ids; names are filled in when read, cut to 40 characters, with line breaks removed.
- **Spoilers:** nothing new. A habit only describes what this player already did.

## Co-op and privacy

- **Only the local player.** Counters, friendship records, the backpack, tool use and bedtime are the local farmer's own. The two shared signals (the farm animals' petted flag, machine state) count only next to the local farmer.
- **Guests:** the watcher needs only the map the player is on, which is always live. No stale-map problem. A0 checks that the nightly save event reaches a guest, since the log is kept only then.
- **Where the data goes:** the logs and `habits.md` are local files beside the other memory files. The model the player chose receives the lines in the day seed and in tool results, as it receives game state today. Nothing else is sent anywhere.
- **Off switch:** `Apprentice.Enabled`, on by default. When off, nothing is watched or written, and the day record is as it is today.
- **Forgetting:** the chat command `/forget habits` deletes the activity files and `habits.md` for this farm and player.
- **README:** states plainly that the copilot records what the player does in the game, where the files are, and how to switch it off.

## Limits

- **Swings, not tiles.** Enough to know that and when the player waters, which is all a habit needs.
- **Harvest, machine and farm-animal lines are computed** and can be wrong, for example a crop item picked up from the ground on the farm.
- **Order is to ten game minutes.** Two activities inside the same ten minutes may be listed in either order.
- **Mixed days teach less.** When the copilot did the watering, that day says nothing about the player's watering.
- **Mods:** custom maps and machines appear by their own names; activities a mod adds with no counter are seen only as items gained.
- **A habit is not a reason.** The mod sees that the player is at the lake on rainy days, not why.

## Testing

- **By hand, with real key presses** (the main test, because only it uses the paths a player uses): a scripted day that waters three tiles, waters with a charged can, holds the tool button, harvests, opens a chest and stores and fetches, buys and sells in a shop, talks and gifts, crafts one item, casts the rod, and sleeps. The log is compared line by line.
- **The autopilot as a second check, where it is valid.** Tool use, harvesting, talking, gifting, buying and collecting from machines go through the same game paths as a player's input, and the jobs report exact counts. With a dev setting that watches jobs as if played by hand, the watcher's counts are compared with the job reports. Chest jobs, the auto-grabber and shipping change the game directly, so they are not part of this check.
- **Control:** a job interrupted by the player moving ends its slice; what follows is logged as hand. A self-play day yields no evidence.
- **Save rule:** a day abandoned without sleeping leaves no activity file; a day replayed after loading is logged once; returning to the title and loading another farm starts clean.
- **Habits:** unit tests on made-up logs, without the game: a habit appears at its evidence; a rainy day does not count against watering; three misses drop an activity; asked and self-play days are not evidence; festival days are ignored; a weekday habit can appear from 56 days.
- **Co-op:** two players on the VM. The host waters, pets and kills a monster while the guest stands on the same map; the guest's log must show none of it.
- **Use:** in the game, "do my usual morning" on a save with a known habit runs the jobs in the habit's order; self-play with no goal follows the habit, and with `FollowMyHabits` off uses the checklist.

## Work items

Each can be set as a goal by itself. A0 decides whether the rest is worth building.

| # | Item | Done when | State |
|---|---|---|---|
| A0 | Experiment: can a day be recognised? | A throwaway watcher logs the by-hand script. Every "check" in the signals table is answered, as a single player and as a co-op guest; the cost per tick is measured. Signals that cannot be told apart reliably are dropped and this doc is revised | Done 2026-11-16, with the real watcher and trace logging instead of a throwaway. Answers: hoeing, sowing, watering, chest moves and shop sales by real key presses and clicks all match. Holding the tool button counts every repeat as a swing, and each one uses water (6 swings, 6 water). A mouse click that closes a menu can swing the held tool, and the watcher counted that real swing. `timesFished` moves when a cast starts (2 casts, 2 fish). Cost: about 5 microseconds per tick (single player), 22 for a co-op guest. Four changes, see "Changes from A0" |
| A1 | Watcher and activity log | The by-hand day matches line by line; the autopilot check passes for the jobs it is valid for; control changes cut slices; an abandoned day leaves no file | Done 2026-11-16. Autopilot check with jobs watched as hand: harvest 1 Corn, petted 4 (two runs), eggs 11 + 8, machines 2 for Beer x2, gift to Abigail of Cauliflower, 2 casts with Sunfish x2 all match the job reports. Asked and self-play slices are cut and labelled; a self-play job that runs on after F10 stays self-play. Several restarts without sleeping left no file; a slept day left `activity/<date>.json` |
| A2 | Activity in the day record, and `get_activity_log` | A hand-played day's record carries the lines; the next morning's seed shows at most eight per day; "when did I last go to the mines?" is answered correctly in the game | Done 2026-11-16. The record of summer 27 carries the lines. Asked in the game "When did I last go fishing, and where?" (the test save had no mine trip): the copilot called `get_activity_log` and answered summer 27 in Pelican Town, 2 casts, 2 Sunfish, and before that summer 24 at the Mountain |
| A3 | Habits, `habits.md` and `get_habits` | Unit tests pass; on the test save, after a set of made-up logs, `habits.md` shows the three habits with counts | Done 2026-11-16. 11 unit tests in the game-free test project. With 14 made-up days plus one real day, `habits.md` after sleeping: mornings water (10 of 10), harvest (10 of 13), tend the animals (13 of 14); bed around 10pm (14 of 14); rainy days at the Mountain, fishing (3 of 3) |
| A4 | Habits in the day seed; self-play follows them | With no goal and a morning habit, self-play's plan starts with that order; with the setting off it uses the checklist | Done 2026-11-16. The seed carries the habits section. F10 with no goal: the plan put tending the animals first after freeing backpack space and bed around 10pm; watering and harvesting were rightly left out (fall 1, no live crops). Self-play started mid-day did not see habits at first; `read_memory` now returns them with `followHabits`. The setting-off case is covered by agent tests |
| A5 | "Do my usual morning" | Jobs run in the habit's order; an activity with no job is named, not attempted | Done 2026-11-16. In the game: `get_habits`, then `water_crops`, `harvest_crops`, `tend_animals` in that order. All three habit steps have jobs, so the no-job case is covered by the prompt only |
| A6 | Co-op check, off switch, `/forget habits`, README | The guest's log shows only the guest's actions; with the setting off no file is written; the command removes the files | Done 2026-11-16. Two players on the VM: the host tended the animals (4 petted, 17 eggs) in the coop and hoed, sowed and watered on the farm with the guest standing next to it. The guest's log shows neither, and the host's own log shows its work. The nightly save reaches the guest: its activity file, day record and `habits.md` were written. Off: no file for that day, and no watching the next morning. `/forget habits` deleted 16 days and `habits.md`. The first-run notice shows once. README updated |

Suggested order: A0, then A1 and A2 (useful alone as a diary the copilot can answer from), then A3 to A6.

## Changes from A0

The experiment changed four signals and one rule:

- **Harvesting** is not "a crop's harvest item entered the backpack": cutting grass gives Fiber, which is also a crop's harvest. It is now a ready crop next to the farmer that stops being ready, credited when items reach the local backpack within three seconds (scythe harvests drop items that fly to the farmer a moment later).
- **Shared state in co-op.** Crop and machine state and the farm animals' "petted" flag are shared. A guest standing next to the host would have credited the host's work. Now emptied crops and machines count only when their items reach the local backpack, and a farm animal counts as petted only right after the local player pressed the action button next to it.
- **Buying:** an item left on the cursor reaches the backpack just after the shop closes; gains within half a second of a shop closing count as bought.
- **Map names:** the farm, the farmhouse and farm buildings all show the farm's name in the game, so log lines say "the farm", "the farmhouse", "Coop".
- **The day record** carries the eight longest lines (the same lines as the seed) and says how many more `get_activity_log` has, instead of all twenty.

Not checked by hand, because the mechanism is the same as for the counters that were: crafting, cooking, monsters killed and geodes cracked.

Found on the way, outside this design, and fixed on 2026-11-16:

- A co-op guest's `go_to_bed` found no route into its own cabin. The cause: the walker stands below a door to use it, and a cabin's porch tile below the door is blocked. It now stands on a free tile beside or diagonally below the door when the one below is blocked (the game accepts a door within one tile). Checked in co-op: the guest walked from the farm to its bed; the host's farmhouse and coop doors still work. A failed route now says why (for example "The way to its door at 77,19 is blocked").
- `buy_items` called without its required `item` crashed. Every tool call now checks the required arguments first and reports the missing ones.

## Later rounds

Not designed here.

- **Keep or sell:** which items the player ships, stores or processes. It needs item flow across days and a signal for loading machines.
- **Where things go:** which chest gets which kind of item. Needs a way to identify a chest that survives it being moved.
- **People:** whom the player talks to and gifts most.
- **A personal line in the morning briefing** ("rain today; you are usually at the lake"). Done: [habits in the briefing](2026-11-16-habits-in-the-briefing-design.md).
- **A one-time offer** when a morning habit first appears. Done: [habits in the briefing](2026-11-16-habits-in-the-briefing-design.md).
- **Routines proposed from habits**, in the routines design.

The first three produce habits about irreversible actions and fall under the rule above.

## Decisions

Settled on 2026-11-16: the owner left all three to the recommendations.

1. **Watching is on by default.** The data is of the same kind the model already receives (the state of the player's own game) and stays in local files. The README and a first-run chat message say that it is on and how to switch it off (`Apprentice.Enabled`).
2. **Self-play follows habits by default** (`SelfPlay.FollowMyHabits`). Round one's habits name no irreversible action, and the goal still leads.
3. **The name** is "Apprentice mode" in the docs; in the game the player only sees "your usual morning" and "habits".

## Review

An independent review on 2026-11-16 checked the first draft against the code. What changed:

| Finding | Change |
|---|---|
| The mod cannot tell asked jobs from self-play per map, and control changes mid-map. Counting asked jobs would let "do my usual morning" keep its own habit alive | Slices now also end when control changes. Only hand slices are evidence. A day the copilot did an activity is left out of that activity's count |
| Under full self-play a habit such as "ships Parsnip" would become unasked shipping | New section "Habits must not widen what the copilot does". Round one has no habits about irreversible actions |
| The autopilot is not a valid answer key for chest jobs, the auto-grabber and shipping, which change the game directly; and it never exercises hand-only paths | Asked jobs are logged from their own reports. The by-hand script is the main test and is larger; the autopilot check is kept only where the paths are the same |
| Habit rules did not hold together: weekday habits could never reach three days in a 14-day window; the fade rule did nothing; rain counted against watering; keep-or-sell could not be computed from the log | "Applies today" facts are stored each day; the day-type habit uses 56 days; the fade rule is three misses in a row; keep-or-sell, storage and people habits moved to later rounds |
| With a shared wallet, money and the shipping bin are the team's | Buying is read from the backpack and the shop's price; the shipping line says "the farm shipped" unless wallets are separate |
| `ChestInventoryChanged` is not limited to the local player | Chest moves are read from the local player's `InventoryChanged` while a chest menu is open |
| Day-end details: the activity file would be mistaken for a day record; the agent-written summary skipped the mod's lines; watcher state survived a return to title; deletion and `habits.md` had no save-rule path | Own `activity/` folder; lines appended in both cases; state cleared on return to title; deletion and `habits.md` follow the nightly save |
| The example showed a timed shipping line and two neighbouring Farm lines, which the signals cannot produce; each mine floor is its own map | Example corrected; mine floors merged |
| Three days of 20 lines in every request is large | At most eight lines per day in the seed |
| Modded names would persist in the seed | Ids stored; names filled in when read, shortened, line breaks removed |
| The game-free test project compiles only the Platform folder | The habits code gets its own game-free folder, added to that project |

Not verified, and left to A0: whether the per-player counters move only for the acting player in co-op; how `UsingTool` behaves with a held button, a charged tool, the rod and weapons; whether the nightly save event reaches a guest.
