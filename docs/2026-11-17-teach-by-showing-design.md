# Teach by showing

Date: 2026-11-17. Status: implemented on 2026-11-17 (see "Work items" and "Checked in the game"); not yet released. Revised after an independent review on 2026-11-17 (see "Review" at the end).

Apprentice mode learns habits by watching for weeks, and only three kinds of them. This round lets the player teach on purpose, in minutes: say "watch me", do a task once, say "that's my truffle run". The copilot turns what it saw into a **routine**, a named list of jobs, and runs it when asked.

Routines were outlined in the chores design and put off, because nobody had a good answer for who writes the list ([outline](2026-11-16-daily-helpers-and-chores-design.md)). The answer here: the player writes it, by playing.

**Words used here:**

- **Self-play:** the F10 mode in which the copilot chooses the work (`SelfPlay.Enabled`).
- **Full self-play:** the config flag `Autopilot.FullSelfPlay`, which switches confirmations off.

## An example

The player says "watch me", then:

1. picks up six truffles on the farm;
2. walks into the shed, takes the oil out of four oil makers and loads them with truffles;
3. puts the oil into the chest by the shed door;
4. says "that's my truffle run".

The copilot answers with the mod's playback:

> I saw: picked Truffle x6 on the farm; collected Truffle Oil from 4 Oil Makers in the shed and loaded them with Truffle; stored Truffle Oil x4 in the chest at (6,12).
>
> As a routine I would do:
> 1. pick up every Truffle on the farm;
> 2. collect from every Oil Maker in the shed and refill them with Truffle;
> 3. store all Truffle Oil in that chest.
>
> Save this as "truffle run"?

A question box in the game asks the same. After "Yes", "do my truffle run" runs the three steps with one model request.

## The three parts

1. **Lesson** (mod, no model): while a lesson is on, a recorder keeps an ordered list of what the player did.
2. **Steps** (mod, no model): pure code turns the list into jobs with arguments, and says which parts no job covers.
3. **Routine** (the mod stores and runs it; the agent words it): saved under a name, run as one job.

As in apprentice mode, the mod reads and computes, and the model words. **The model never writes a step.** It can only pass on the player's corrections, as a few named edits that the mod applies and checks.

## Part 1: the lesson

### Starting and ending

- **Start:** the player says "watch me" (or "let me show you ..."), and the agent calls `start_lesson`. A small line on the screen says "StardewPilot is watching (lesson)" for as long as it is on.
- **End:** the player says "done" or "that's my ...". The agent calls `end_lesson`, which returns the playback.
- **Ends by itself**, with a chat line saying so:
  - when the day ends (the lesson is dropped, because a routine cannot span days);
  - when a festival or cutscene starts (what was seen so far is kept and offered);
  - after six game hours, or after 60 events.
- **One lesson at a time.** Starting a second one asks whether to drop the first.
- **A job asked for during a lesson becomes a step**, with the exact arguments it ran with. "Now put my things away" in the middle of a lesson adds `put_things_away`. This is how the steps that are never taken from hand play get into a routine (see Part 2).
- **Self-play** cannot be on during a lesson; `start_lesson` refuses.

### The recorder

A new `LessonRecorder`, kept apart from the day log.

- **Where its signals come from:** the watcher's signal handlers feed it. Today the watcher does nothing at all when `Apprentice.Enabled` is off: `StartDay` returns before the day exists, and every handler stops on `day == null` (`Watcher.cs:92`).
- **Working with apprentice mode off:** the watcher must split "read the signals" from "write the day log". It then reads them when either is on.
- **Why it must work with the switch off:** a lesson is asked for by the player, so it runs even then. With the switch off, the lesson's events go only to the routine, never to the day log.
- **Order:** the day log keeps totals per slice. The recorder keeps an **ordered list** of events, each with the game time, the map and what happened.

| Event | Detail kept | Read today? |
|---|---|---|
| Arrived on a map | Map id | Yes (warp) |
| Harvested | Crop item and count | Count yes; item from the backpack, as now |
| Picked forage | Item and count | Count only; the item is new |
| Animals | Petted, produce gathered, **the building** | Building is new |
| Machines collected | **Machine kind**, count | Kind is new |
| Machines loaded | **Machine kind, item loaded** | New (T0) |
| Stored, fetched | Items, counts, **which chest** | Chest is new; the menu's `context` is already read (`Watcher.cs:303`) |
| Talked | Villager | Yes |
| Watering | Swings | Yes |
| Asked job | Job name and arguments | New |
| Anything else (fishing, mines, sowing, buying, shipping, gifts, crafting, ...) | Counted only, for the "left to you" list | Yes |

**Kinds left ready:** when the player leaves a map, and at `end_lesson` for the current map, the recorder notes which kinds of crop, forage and machine output were still ready there. This is only used to word the playback (see below).

**Rules:**

- **Ids, not names.** Items, maps and villagers are stored by id and named when shown, cut to 40 characters.
- **Only the local player**, with the watcher's signals and co-op rules.

## Part 2: from a lesson to steps

Pure code (`LessonSteps`), tested without the game. It walks the events in order and makes one step per stretch of the same activity on the same map.

### Which steps a lesson can give

Round one takes from hand play **only steps that are reversible or harmless**:

| Seen | Step |
|---|---|
| Watering | `water_crops(location)` |
| Harvesting | `harvest_crops(location, crop)`, one step per crop kind harvested |
| Picking forage | `collect_forage(location, items)`. Needs a new `items` argument |
| Animals | `tend_animals(building)`, one step per building tended |
| Machines | `tend_machines(location, machine, refillWith)`, one step per machine kind; `refillWith` when the player loaded them |
| Storing | `store_in_chest(items, chest)` with the chest's id (see "Chests") |
| Fetching | `take_from_chest(item, count, chest)`, with the count shown |
| Talking | `talk_to(npc)` |
| Asked job | The same job with the same arguments, if it is allowed (below) |

Everything else seen by hand is listed as **left to you**, with a hint: "to include buying, ask me to buy during the lesson".

- **Not taken from hand play:** buying, shipping and gifts. These cost something, and one showing cannot say how much: "shipped 10 of 50 Wine" is not "ship all Wine". They can only enter a routine as an asked job, with the exact arguments the player approved then.
- **Left out of round one entirely:**
  - fishing and the mines, where the risk is too high for an unwatched step;
  - sowing, because `plant_seeds` clears and tills new ground near the farmhouse (`ActionTools.cs:61`), which is not where the player sowed;
  - crafting and cooking, which have no job.

### Filters always narrow

A step is always limited to the kinds the player touched: the crops harvested, the forage picked, the machine kind used. It is never widened to "everything" by guessing.

The kinds left ready only change the wording. The playback says "every Truffle on the farm (you left the Daffodils)". The player can then widen a step in chat ("all the forage, not just truffles"), which is one of the named edits.

### Order

Steps keep the order shown. Neighbouring steps with the same job and arguments become one. Nothing is reordered: the player's order may matter (fetch the hops, then load the kegs).

## Part 3: routines

### Saving

1. `end_lesson` returns the events, the proposed steps and the parts left out, each with its label (raw or computed).
2. **The mod keeps the proposal**, under a lesson id.
3. The agent shows the playback, and the player corrects it in chat.
4. The agent calls `save_routine(lessonId, name, edits)`. The edits are a short list of named changes:
   - drop step N;
   - change step N's count;
   - widen step N's filter to all of that job's kind.
5. The mod applies the edits, checks the result, and shows the final steps in an **in-game question box**, in its own words: "Save 'truffle run': 1. ... 2. ... 3. ...?".
6. The routine is saved only after "Yes". So what is stored is what the player saw, whatever the model said.

**Checks**, the same at save time and at every run:

- **Allowed jobs:** the jobs in the table above, plus `put_things_away`, `buy_items`, `ship_items` and `give_gift` (as asked jobs only).
- **Never allowed:** `go_to_bed`, `walk_to`, `use_tool`, `interact`, `fish`, `explore_mines` and `plant_seeds`.
- **Arguments:** checked against the job's schema: types, enums and no unknown keys. Today the registry checks only that required arguments are present (`ToolRegistry.cs:62`), so this is a new validator.
- **Forbidden flags:** `includeBundleItems`, `allButTools`, `includeToolbar`.
- **Other farmers' homes:** no step may name a location that is another farmer's home.
- **Limits:**
  - at most 12 steps per routine and 20 routines;
  - names of letters, digits and spaces, up to 40 characters.

**File:** `routines.json` in the memory folder, per farm and player.

- **Written at once**, not at the nightly save. A routine is the player's decision, not game state.
- Writing it at once also means the nightly save never overwrites a hand edit. That matters because pending writes win over the file (`MemoryStore.cs:50`).
- **Hand edits are allowed.** A routine that fails the checks is refused when run, with the reason.

**Other tools:**

- `list_routines` (read-only);
- `forget_routine(name)`.

`/forget habits` does not touch routines; the player made them on purpose.

### Chests

A chest is known by an id that the mod stamps into the chest's `modData` when the routine is saved. The game check of 2026-11-16 (P0 in the put-away design) showed the id:

- survives a save;
- survives a kick, which can move a chest up to three tiles;
- survives an upgrade to a big chest;
- is lost when the chest is picked up.

So a chest that was moved still works, and a new chest placed on the old tile never receives the items. If no chest has the id, the step fails: "the chest for this step is gone; teach this step again".

Junimo chests are never chest steps: they are shared and hold nine stacks.

### Running

`run_routine(name)` is one runner job. Inside it, the steps run one after another with no model request in between.

**What the runner needs** (it does not support this today):

- **Each step gets its own `JobContext`.** Today `Report` and `Problems` belong to the job, and any problem makes the whole job "partly done" (`JobRunner.cs:204`).
- **A step wrapper** catches every `JobStop` the step throws (failed or partly done) and a `ToolError` (for example a bad map name in a hand-edited file).
  - Today `Attempt` catches only `Failed` (`Macros.cs:968`), and a `ToolError` becomes "Internal error".
  - After each step the wrapper does the clean-up that `Finish` does now: it releases simulated buttons and the farmer, and resets `ExpectingMenu`, closing a menu the step opened.
- **A step event:** each step is announced to the watcher and to the self-play log under its own job name. Today `Finished` fires once per runner job, so `Act.OfJob` would see only `run_routine`, and a day the routine did the watering would count against the watering habit.

**What ends a routine**, in code terms:

| Happens | Routine |
|---|---|
| `runner.Stop` from outside the step: the player moves, a menu or cutscene, F9, the watchdog, a new job | Ends at once, as any job does |
| The step finishes done, including "nothing to do" | Next step |
| The step throws, fails or is partly done: shop closed, chest gone, backpack full, energy floor reached | **Ends.** A later step would act on the wrong state: a failed fetch would leave nothing to load |
| The player says "No" to a question | **Ends**, with the reason "you said no to step N". Today a "No" is thrown as a failure (`Macros.cs:556`); the report should still say it was a "No" |

**Questions, stricter than for a single job:**

- `buy_items` asks only above 500g today (`ConfirmSpendAbove`, `Macros.cs:550`).
- "Don't ask again" and full self-play switch the questions off (`Confirm.cs:25`).

A routine runs unwatched and repeatedly, so inside a routine, shipping, buying and gifting **always ask**, ignoring standing permissions, full self-play and the 500g limit. This needs an `alwaysAsk` flag on `Confirm.Ask`.

**Time and energy:**

- `run_routine` refuses to start within one game hour of the work stop time (11:30pm by default).
- At the stop time the mod already stops every job and walks the farmer to bed (`ModEntry.cs:246`). A routine is stopped the same way; this is the existing end-of-day rule, not a routine step.
- The energy floor exists only inside tool use (`Actions.cs:65`), as a "partly done" stop. A tool step that reaches it ends the routine, as above.

**The report:** one line per step (done, partly done, failed or stopped, and why), then which steps did not run. The agent receives it as one tool result.

**The agent:** `run_routine` is counted as a game action and toward the agent's three-failures rule (`agent/src/index.ts`), like the other jobs.

### Routines and self-play

- **Refused:** when self-play or full self-play is on, `run_routine` refuses a routine with a shipping, buying or gifting step. This is checked when the routine starts and again before each step.
- **The day seed** names only routines without such steps, under "the player's routines; run one only when the player asks".

This keeps the apprentice-mode rule: nothing learned may widen what the copilot does unasked.

## Co-op and privacy

- **Only the local player** is recorded. A guest can teach and run routines; routines are per player.
- **Other farmers' homes are a gap today, outside routines too.**
  - `Permission.OthersProperty` exists, but no job ever asks it.
  - `store_in_chest` and `take_from_chest` reach any map with no owner check (`Macros.cs:992`).
  - Routines refuse such locations (see "Checks"). The plain jobs should ask; this is work item T6.
- **Where the data goes:** `routines.json` is a local file. The model the player chose receives the lesson's playback once, at `end_lesson`, and routine names in the day seed.
- **Text from the game is data:**
  - the file holds ids; names are filled in when read and cut short;
  - the routine's name comes from the player, is cleaned by the mod and is shown in quotes.

## Limits

- **One showing:**
  - no "if it rains", no "until the backpack is full", no clock times;
  - counts are what was shown, until the player changes them.
- **Narrow by default:** a routine does what the player touched, unless they widen it in chat.
- **No tile work:**
  - "clear this corner" or "plant that field" cannot be taught;
  - fishing, the mines and sowing are left out of round one.
- **Computed signals can be wrong** (harvesting, machines, farm animals), as in the day log. The playback and the save question are the checks.
- **A picked-up chest** breaks its steps until they are taught again.

## Later

- A routine in the morning briefing ("run it now?"), and routines proposed from habits. Schedules are designed in [routines that start themselves](2026-11-17-routines-that-start-themselves-design.md).
- Conditions and repeats.
- Two showings, to settle counts and filters without asking.
- Fishing, mines and sowing steps, once each has a way to stay on the player's own ground and risk.
- Crafting and cooking jobs.

## Testing

**Unit tests** (no game):

- `LessonSteps`:
  - the example above;
  - filters narrow to the kinds touched;
  - order kept, neighbours merged;
  - an asked job copied;
  - buying seen by hand becomes "left to you";
  - bed is never a step.
- The routine checks:
  - an unknown job, a bad type, enum or key;
  - a forbidden job or flag;
  - another farmer's home;
  - too many steps;
  - a hand-edited file;
  - edits applied to a proposal.

**In the game, with the test kit:**

- **Teach and run:** teach the truffle run, and a "town run" (fetch a gift from a chest, ask for a seed purchase during the lesson, give the gift). Read the playback, save through the question box, sleep, and run both.
- **Running:**
  - move during step two: the routine ends;
  - close the shop: the step fails and the routine ends;
  - answer "No" to the gift: the routine ends with that reason;
  - try to start at 10:40pm: refused;
  - a step that reaches the energy floor ends the routine.
- **Safety:**
  - with "don't ask again" set for shipping, a routine still asks;
  - a 100g purchase in a routine asks;
  - with self-play on, a routine with a gift is refused and one without runs;
  - a model call to `save_routine` with `includeBundleItems` is refused.
- **Chests:**
  - kick the chest: the step still works;
  - pick it up and place another on the tile: the step fails and the new chest is untouched.
- **Files:**
  - a hand edit made during a day with a pending memory save survives the night;
  - a bad map name in the file gives a clear refusal, not an internal error.
- **Apprentice mode:**
  - a routine that waters does not count against the watering habit;
  - a lesson with the switch off writes no day log.
- **Co-op:**
  - a guest teaches and runs a routine;
  - the host's actions during the guest's lesson are not recorded;
  - a step naming the host's house is refused.

## Work items

T0 decides whether the rest is worth building.

| # | Item | Done when | State |
|---|---|---|---|
| T0 | Checks in the game | Each answered with a note: (a) the machine kind collected and an item loaded by hand can be read; (b) `collect_forage` picks up truffles; (c) kinds left ready can be read at warp and on the current map; (d) the jobs end "done", not failed, when there is nothing to do; (e) a chest id stamped at save time is found by a guest | Done: all five yes (see "T0 answers") |
| T1 | Runner: per-step context, the step wrapper (all stops, `ToolError`, clean-up), the step event for the watcher and the self-play log | A test job of three steps where step two throws "partly done" ends with a clean report and the farmer free | Done |
| T2 | `LessonRecorder`; the watcher split between reading signals and writing the day log; `start_lesson`, `end_lesson`; the on-screen line | A scripted lesson gives the expected events, with apprentice mode on and off | Done |
| T3 | `LessonSteps` (pure), with tests; an `items` filter for `collect_forage` | Unit tests | Done |
| T4 | Argument validator; routine checks; chest id stamping; `routines.json` written at once; `save_routine` with named edits and the in-game question; `list_routines`, `forget_routine` | The check tests; a saved routine survives a quit without sleeping and a hand edit | Done |
| T5 | `run_routine`: end rules, `alwaysAsk`, the start-time refusal, report; self-play refusal; agent counting | The running and safety tests | Done |
| T6 | Owner check for `store_in_chest` and `take_from_chest` outside routines (asks `OthersProperty`) | A guest storing into the host's house is asked | Done |
| T7 | Agent prompt ("watch me", "that's my ...", "do my ..."); day seed; README | A full teach-and-run in chat | Done |

### T0 answers

- **(a) Machines:** yes. The kind is read when a ready machine next to the farmer empties. Loading is read when an empty or ready machine next to the farmer starts working right after the backpack lost an item (within two seconds).
- **(b) Truffles:** yes. Spawned truffles are forage to `collect_forage`. The lesson reads forage itself: spawned objects next to the farmer that disappear, matched with the item reaching the backpack, because the game's forage counter does not say which item.
- **(c) Kinds left ready:** yes, from the old map at warp and from the current map at `end_lesson`.
- **(d) Nothing to do:** `collect_forage` with no forage ends "done". A step that needs something missing fails, which is right: `tend_machines` with a refill item that is not in the backpack fails, and the routine ends there.
- **(e) Guests:** yes. A chest id stamped by the guest reached the host, and after the host kicked the chest one tile, the guest's routine found it and stored into it.

## Checked in the game (2026-11-17)

On the test save, with the test kit acting as the player (it presses the action button on tiles, opens chests and moves items, as a hand press does):

- **The truffle run:**
  - The farmer picked two truffles (a daffodil was left), collected two Oil Makers, loaded them with truffles, and stored the oil in a chest.
  - The playback was exactly the design's example, including "(you left the Daffodil)".
  - The question box listed the three steps; after "Yes" the file held item ids and the chest's id.
  - `run_routine` did all three steps and left the daffodil.
- **Ending:**
  - With nothing ready, step 1 ended "done" and step 2 failed ("There is no Truffle in the backpack"). The routine ended "partly done", and step 3 was listed as not run.
  - Moving the farmer during a routine ended the whole routine at once.
- **Chests:**
  - **Kicked:** a chest kicked one tile was still found by its id.
  - **Gone:** with the routine's chest gone and a new chest on its tile, the step failed ("The chest for this step is gone ... Teach this step again") and the new chest stayed empty.
- **Safety:**
  - **Standing permission:** with "don't ask again" set for gifts, a routine's gift step still asked, with only Yes and No. "No" ended the routine with that reason.
  - **Self-play:** with full self-play on, a routine with a gift was refused, and one without ran.
  - **Late start:** a start at 10:40pm was refused ("work stops at 11:30pm").
- **Hand edits:**
  - A bad map name failed with the game's own message, not an internal error.
  - A routine with `includeBundleItems` and `go_to_bed`, and one naming a cabin, were refused with the reasons.
  - Hand edits survived a night with pending memory writes.
- **Other farmers' homes:** `store_in_chest` into the guest's cabin asked "Use a chest in Guesty's house?" before walking, and "No" stopped it.
- **Apprentice mode:**
  - The routine's steps appeared in the day log under their own job names, as the copilot's.
  - With `Apprentice.Enabled` off, a lesson still recorded, and the activity log stayed off.
  - A lesson was dropped at the end of the day.
- **Co-op (two players on the VM):**
  - The host picked up a truffle next to the guest during the guest's lesson; it was not recorded.
  - The guest taught and ran a routine.
  - A guest routine naming the host's house was refused.
- **In chat (Qwen):** "watch me, I'll show you my truffle run" → `start_lesson`. Then "done, that's my truffle run" → `end_lesson`; the reply showed what was seen and the three steps, and asked to save. "yes save it" → `save_routine`, the game question, and saved. "do my truffle run" → `run_routine` with all steps done.

**Not checked in the game:**

- the energy floor ending a routine;
- a purchase in a routine. It asks through the same `AlwaysAsk` path as the gift that was checked; the 500g limit is skipped in the code.
- a lesson ending at 60 events or six game hours;
- a very long routine in the question box (cut at 300 characters; the full list is posted in the chat too).

## Built differently from the design

- **The steps in the chat:** besides the question box, the mod posts the final steps into the chat in its own words, because a long routine does not fit the box.
- **Asked jobs:**
  - An asked job that ended "partly done" still becomes a step, with the arguments it ran with.
  - An asked chest job with a tile becomes a chest-id step.
  - An asked job whose map defaults to the current one (`collect_forage` and the chest jobs) gets that map written in, so the routine does not depend on where it starts.
- **Job start (found while testing):** a job that starts and ends within one tick (a gift to a villager standing next to the farmer) was counted as done by hand, in the day log too. The runner now announces a job start, and the watcher closes the hand slice before the job's first step.
- **The report:** when a routine is stopped from outside, it names the step that was running (`stoppedDuring`).
- **Bugs found while testing, fixed (checked in the game on 2026-11-17):**
  - **The walker crossed beds.** In the farmhouse and in a cabin the game's pathfinder walked over the bed, and the game's "Go to sleep for the night?" question stopped the job. Seen with the chest next to the bed and with `collect_forage` in a cabin. Now a path that crosses a bed is re-planned around it, unless the bed is where the walk ends. Storing into the chest beside the bed now goes around it, with no question.
  - **`go_to_bed` failed from the bed.** When the farmer already stood on the bed (on waking, or after answering "No"), the game did not ask again, and the job failed with "Reached the bed, but the game did not offer to sleep". It now steps off the bed first. From the waking spot it stepped off, walked back, and slept.
  - **A moved chest was worded by its old tile.** Steps now name where the chest stands now: after a kick, `list_routines` said "(26,4)" instead of "(24,4)". The file keeps the id, and the place is only used for words.

## Review (2026-11-17)

An independent review checked the first draft against the code. Its findings, and what changed:

- **Must fix, accepted:**
  - **Bedtime.** At the stop time the mod walks the farmer to bed. The draft said the routine "ends there". The doc now says the existing end-of-day rule applies, and adds a refusal to start within an hour of it.
  - **Other farmers' homes.** `OthersProperty` is never asked. Routines now refuse such locations, and T6 fixes the plain jobs.
  - **Small purchases never ask, and standing permissions apply.** Inside a routine, ship, buy and gift now always ask.
  - **The model could write any schema-valid step.** The mod now keeps the proposal and accepts only named edits. Risky flags are forbidden, and saving needs an in-game "Yes".
  - **Ship-all from a partial showing.** Buying, shipping and gifts are no longer taken from hand play; they enter a routine only as asked jobs.
  - **The self-play rule used the wrong flag.** It now applies to both self-play and full self-play, checked before each step.
- **Should fix, accepted:**
  - **"No" was indistinguishable from a failure.** The routine now ends on either, so no new outcome is needed; the report still names a "No".
  - **No three-failure counter exists in the mod.** It was dropped; the routine ends at the first failed step, so a later step never acts on the wrong state.
  - **The energy floor is only inside tool use.** The doc now says so.
  - **There is no schema validator.** It is now a work item.
  - **The watcher does nothing with the switch off.** The split is now in T2.
  - **The draft did not mark which recorded details are new.** They are now marked.
  - **Chest by tile.** Steps now use a `modData` id; Junimo chests are excluded.
  - **`plant_seeds` tills new ground.** Sowing is cut, along with fishing and the mines.
  - **Hand edits versus pending writes.** The file is now written at once.
  - **Chaining did not fit the runner** (`Attempt` caught only failures; shared Report and Problems; no clean-up; one `Finished` event). It is now T1, ahead of everything else.
  - **The "what was left" rule was unsound.** Filters now always narrow to the kinds touched.
- **Simpler first round, partly accepted:** the review suggested shipping only "asked jobs become steps". Steps learned from play are kept for the reversible jobs, because they are the point of the feature. T1 and the asked-job path come first and are usable on their own.
- **Unclear wording, fixed:** "self-play" versus "full self-play" is now defined; "a stop the step did not cause" is now defined in code terms; routines in the day seed now carry the "only when asked" wording, and the self-play refusal is enforced in the mod.
