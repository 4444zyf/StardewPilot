# Routines that start themselves

Date: 2026-11-17. Status: implemented on 2026-11-17 (see "Work items" and "Checked in the game"); not yet released. Revised after an independent review on 2026-11-17 (see "Review" at the end).

Teach by showing ([design](2026-11-17-teach-by-showing-design.md)) gave the player routines: a named list of jobs, taught by playing, run when asked. Asking is still a chat message and a model request every time. This round lets the player give a routine a **schedule**: "do my truffle run every morning", "my town run at 4pm on Fridays", "my indoor chores when it rains".

When a schedule comes due, the mod offers the routine on the screen, and a morning routine can start by itself. Neither needs a model request.

**Words used here:**

- **Schedule:** when a routine is due: a time of day, and optionally which days.
- **Due:** the schedule matches today and the time has come.
- **Offer:** a line on the screen with a key that starts the routine.
- **By itself:** the routine starts with no key press, when the player leaves the farmer alone.
- **Self-play:** the F10 mode (`SelfPlay.Enabled`). **Full self-play:** the config flag `Autopilot.FullSelfPlay`.

## The main problem, and the answer

The farmer is the player's. A routine that starts unasked takes the farmer away, perhaps in the middle of something.

**The answer has two levels.**

1. **An offer, at any time of day.** When a routine comes due, the screen shows `"town run" is due: F7 to start`. Nothing moves until the player presses the key.
2. **By itself, only first thing in the morning.** A routine scheduled for 6:00 may start by itself, while the farmer is still at home and the player has touched nothing since waking. The first input of any kind cancels that for the day and leaves the offer.

Once a routine runs, any movement or tool input stops it at once. This is today's rule for every job (`ModEntry.cs:315`).

**Why by itself is limited to the morning:** at 6:00 the farmer is at home, on no horse, with no festival running, and the player has not begun anything. At any other time the farmer may be standing in the spa, waiting for a villager, or on the island, and five quiet seconds do not mean "take over".

## An example

The player has the routine "truffle run" and says: "do my truffle run every morning, by itself".

1. The agent calls `schedule_routine`. The game asks: *Start "truffle run" by itself every morning?* The player answers "Yes". It begins tomorrow.
2. Next morning the farmer wakes and the line appears: `"truffle run" starts by itself in 5 (F7 now, any key to keep the farmer)`.
3. The player touches nothing. After five seconds a toast says `"truffle run" started: move to take over`, and the farmer does the three steps.
4. A toast and a chat line report: `"truffle run" (every morning): all 3 steps done.`

On a day the player presses anything in those five seconds, nothing starts. The line stays as an offer until 8:00.

## Part 1: the schedule

A routine has at most one schedule. It is stored with the routine in `routines.json`, under `when`.

| Field | Values | Default |
|---|---|---|
| `at` | A game time on the ten-minute grid, from 6:00am (the morning) | Required |
| `days` | Any of Mon ... Sun | Every day |
| `weather` | `rain` or `dry` | Any |
| `start` | `offer` or `self` | `offer` |

- **Rain** is the watcher's test (`Game1.getFarm().IsRainingHere()`, `Watcher.cs:145`), so "when it rains" means the same as in the habits.
- **The weekday** is the watcher's (`Weekdays[(day - 1) % 7]`).
- **Not in round one:** seasons, and "when the truffles are out" or other triggers that read the farm's state. See "Later".

**Checks**, when the schedule is set and whenever the file is read:

- **Time:** `at` is at least three hours before the work stop time (`Autopilot.StopWorkAt`, 11:30pm by default). This one rule also covers `run_routine`'s refusal in the last hour.
- **`self` needs `at` 6:00.** At any other time a routine can only be offered.
- **`self` needs a routine that costs nothing.** A routine with a buying, shipping or gift step (`RoutineRules.Asks`) can only be offered. Its questions would open unasked.
- **A routine that fails the routine checks** has no schedule in effect; `list_routines` says why.

### Setting a schedule

- **`schedule_routine(name, at, days, weather, start)`**: the agent passes what the player said.
  1. The mod checks the fields.
  2. The game asks in a question box, in the mod's own words: *Start "truffle run" by itself every morning?* or *Offer "town run" at 4:00pm on Fridays?*
  3. The schedule is saved only on "Yes". It takes effect the next day.
- **The question is always asked.** No standing permission and no full self-play covers it, as with saving a routine (`Permission.SaveRoutine`).
- **It runs as a runner job**, like `save_routine` (`RoutineService.cs:331`), because the question needs one. So:
  - it is refused while the autopilot is off (F9);
  - it is started without arguments, or a lesson would record it as a step (`Watcher.cs:321`);
  - the agent does not count it as a game action (`agent/src/index.ts`, `isGameAction`), so a "No" is not a failed action.
- **`unschedule_routine(name)`** removes the schedule with no question, because it only narrows what the copilot does.
- **`list_routines`** shows each schedule in words, and for today: due at a time, running, done, skipped, or not today.

**The file code must carry `when`.** Today `Load` and `Save` keep only the name, date and steps (`RoutineService.cs:70`, `:94`), and replacing a routine removes the old one whole (`:350`).

- **Replacing** (teaching again under the same name) keeps the schedule. The save question names it: *Replace the routine "truffle run" (starts by itself every morning)? ...*
- If the new steps cost something, `self` becomes `offer`, and the question says "offered every morning".
- **`forget_routine`** deletes the schedule with the routine.

**Hand edits** to `when` are allowed, like the rest of the file, and pass the same checks. A hand edit can set `self` with no question. This is deliberate: the file is the player's, and the agent has no tool that writes it.

### Why the model may write a schedule but not a step

A step can be any job with any arguments. A schedule is four small fields, and the question box shows all of them in the mod's words. What is saved is what the player saw.

## Part 2: when a routine comes due

All of this is the mod's own code, with no model request. Pure code decides what is due (`RoutineSchedule`, tested without the game).

### The window

A routine is due from `at` for two game hours (about a minute and a half of real time while the clock runs). After the window it is **skipped today**. An offer the player ignored leaves no message.

- **Once a day, whoever started it.** A routine that already ran today is not due again. This counts a run from its schedule, from "do my truffle run" in chat, and from the agent.
- **The record** is kept with the save, in the `modData` of the player's home: the date, and per routine the game time it started and whether it is done for today. (The first plan, a file with a time rule, failed in co-op; see "Built differently".)
  - A day quit without sleeping is loaded again without the record, so the routines come due again.
  - A guest who reconnects keeps it, because the host still holds the locations, so nothing runs twice.
- **Windows that closed before the save loaded** are skipped silently.
- **The file is not read every tick.** The routines are read at day start, after the mod's own writes, and every ten game minutes if the file's time changed.

### When the offer shows

The offer shows while all of these hold:

- the autopilot is on (F9) and no job is running;
- the agent is not in a turn (`AgentState.Busy`). Its next job would replace the routine (`JobRunner.cs:169`);
- the player can move (`Context.CanPlayerMove`), and there is no cutscene, festival or minigame (`Game1.eventUp`, `Game1.isFestival()`, `Game1.currentMinigame`);
- no lesson is recording;
- self-play is off (see "Self-play");
- the farmer is not in the mines, the Skull Cavern or the volcano;
- `run_routine` would not refuse it. With full self-play on, a routine that costs something is not offered (`RoutineService.cs:410`).

Otherwise the mod waits, and looks again until the window ends. One offer shows at a time: the earliest `at` first, then by name.

### Starting

- **F7** (config `Routines.StartKey`, any key or controller button) starts the offered routine. "Do my truffle run" in chat still works too.
- **By itself.** For a `self` routine, the line counts down five seconds and then starts it. The count runs only while all of these hold:
  - the offer conditions above;
  - the player has given **no input at all since waking**: no button press, held key, cursor movement or scroll, and the farmer has not moved;
  - the farmer is in their own home or on the farm;
  - today is not a festival day (`Utility.isFestivalDay()`);
  - the game window has focus and the game is not paused.
- **The first input ends by itself for the day**, for every routine. The offers stay.
- **Several `self` routines** due at 6:00 run one after another on the same count, in order. A stop by the player ends the chain.
- **The run is `run_routine`'s own code** (`RoutineService.Run`), started by the mod. Everything in the teach-by-showing design holds: the checks at every run, the first step not done ends it, questions always ask, the step events for the watcher.
- **A run that started by itself never opens a question.** The only one left is "Use a chest in another farmer's house?", asked when a chest was moved there (`Macros.cs:1007`). In such a run that step fails instead.

### Stopping, and after

The runner must say **why** a job stopped. Today the player taking over, a menu opening and a step partly done are all "partly done" with free text (`ModEntry.cs:315`, `JobRunner.cs:221`, `RoutineService.cs:443`). `JobResult` gets a cause: the job itself, the player, the screen, replaced, the end of the day, the watchdog.

| Happens | Then |
|---|---|
| All steps done | Toast and chat line. Done for today |
| A step fails or is partly done | Toast and chat line with the step and the reason. Done for today |
| The player moves, a menu or cutscene opens, F9, or a job the player asked for replaces it | The routine stops, as any job. If the window is still open, the offer comes back |
| The work stop time | The routine only stops. A run started by its schedule is **not** followed by the walk to bed (`ModEntry.cs:260`): nobody asked for the routine just then, so nobody asked to end the day |

**Messages.** The mod words them itself, with no model request.

- Today only the agent's messages, tips and warnings appear as a toast (`ModEntry.cs:91`); a system line is seen only in the chat (F8). A farmer that walks off by itself needs a visible reason, so a schedule's start and result are toasts, with no warning sound.
- **The same failure, quieter.** When a routine stops at the same step for the same reason as its last run, the result goes to the chat only, with no toast.
- **The agent** receives the result as context for its next turn, through a new message `routines/ran` labelled "[The mod ran the player's routine by its schedule]". It does not start a turn.

**The diary.** The watcher logs the steps as jobs the copilot did when asked (`Watcher.cs:249`). That is close enough: the schedule is the player's standing request.

### Nothing to do

A scheduled routine also runs on days with nothing ready. The jobs do not handle that quietly today:

| Job | With nothing to do |
|---|---|
| `tend_machines` | Checks first, ends "done" without walking (`Chores.cs:449`) |
| `water_crops` | In rain ends "done" without walking; but fails with no watering can in the backpack, even in rain (`Macros.cs:171`) |
| `harvest_crops`, `collect_forage`, the chest jobs, `talk_to` | Walk to the place first, then find nothing (`Macros.cs:260`, `:476`, `:1009`) |
| `tend_animals` | Walks into every building; fails after 7pm (`Chores.cs:32`) |

Round one accepts this:

- A by-itself routine starts at home in the morning, so its empty walks are short and on the farm.
- An offered routine is started by the player, who can see whether there is work.
- A step that needs something missing still fails: the truffle run's refill step fails on a day with no truffles. The quieter repeat message above keeps that from nagging.

A "this step has work" test per job would fix all of it. It is the first item under "Later".

## Part 3: the rest of the copilot

### The morning briefing

The briefing gets one line when a routine is scheduled for today: "Today: truffle run in the morning (by itself), town run at 4:00pm (offered)."

- **Facts:** `routinesToday = [{ name, at, start }]` in `get_day_briefing` and the seed's briefing facts. `DayBriefing.Build` needs the routines passed in (`DayBriefing.cs:36`).
- **Rank:** the briefing is at most five lines (`agent/src/index.ts:509`). The routines line is the least important and is dropped first.
- It is told as a fact. The briefing rule stays "no question back".

### Self-play

In self-play the copilot chooses the work and its jobs follow each other closely. **Schedules do nothing in self-play:** no offer, no start. The rule for the agent stays "run a routine only when the player asks".

Letting the self-play agent run due routines is left for later. It needs the due routines passed with each self-play step (`selfplay/next`), not only in the morning seed.

### Apprentice mode

A routine's steps are already logged as the copilot's jobs, step by step (`StepFinished`, `ModEntry.cs:77`). Nothing changes: a morning the routine watered does not count as the player's watering habit.

### Co-op

- Schedules are per player, like routines. Each player's own game decides what is due for them.
- The clock does not pause in co-op, so a window can pass while a guest reads a menu.
- The rules for other farmers' homes are the routine's own.

## Off switches

- `Routines.StartThemselves` (config, on by default): off means no offers and no starts. Schedules stay saved. With no schedule set, the feature does nothing, and every schedule was approved in the game.
- F9 (autopilot off): no offers and no starts until it is on again.
- Any input in the morning: nothing starts by itself that day.
- "Stop doing my truffle run every morning": `unschedule_routine`.

## Limits

- **Clock, weekday and weather only.** No "when the kegs are ready", no "after the truffle run", no "every other day".
- **By itself only at 6:00, from home, untouched.** Everything else is an offer. This is on purpose.
- **One schedule per routine.** "Mornings, and again at 5pm" needs two routines.
- **Empty runs walk** (see "Nothing to do").
- **A routine that fails for a normal reason fails every such day.** Nothing learns from it yet.
- **An offered routine starts where the farmer is** and walks from there. From the island or the desert the first step fails, because the walker knows no way back (`Navigation.cs:31`).
- **Not in self-play.**

## Later

- **A "has work" test per job.** With it: skip a due routine that has nothing to do, and state triggers such as "when the truffles are out" (with a count that makes the walk worth it, and a limit per day).
- **By itself at other times,** once there is a safe test for "the player is not using the farmer" away from home.
- **Self-play runs due routines.**
- **Teach by correcting:** a routine that keeps stopping at the same step proposes a change.
- **A routine from a habit:** the usual morning, offered as a scheduled routine.
- **Seasons** in a schedule; **resume** from the step where the player took over.

## Testing

**Unit tests** (no game), for `RoutineSchedule`:

- the schedule checks: the time grid, the latest time, bad days, `self` at another time than 6:00, `self` with a step that costs something;
- due or not: weekday, rain and dry, before `at`, inside and after the window, already run today;
- the run record: its text form read back, bad parts skipped, a new date clearing the runs but not the last stops;
- the order of several due routines;
- the words: "every morning", "at 4:00pm on Fridays", "every morning when it rains".

**In the game, with the test kit:**

- **Setting:**
  - "do my truffle run every morning by itself" in chat gives the question box; "No" saves nothing, and the agent does not count a failed action;
  - a routine with a gift step, or a 4pm schedule, asked to start by itself is refused, and can be offered;
  - a hand edit setting `self` on a routine with a gift step is not in effect, and `list_routines` says why;
  - teaching the routine again keeps the schedule, and the question names it;
  - a schedule set at 6:30 for 6:00 does nothing until the next day.
- **Starting:**
  - untouched at 6:00: the toast, then the routine starts after five seconds;
  - one key press, a mouse movement or a held movement key during the count: no start that day; F7 starts it;
  - two `self` routines at 6:00 run one after the other;
  - an offer at 4pm starts on F7 and never by itself.
- **Not starting:**
  - while the agent is in a turn; in the mines; during a lesson; with self-play on; with the autopilot off; with the config switch off;
  - by itself on a festival day;
  - a rain schedule on a dry day; a Friday schedule on a Thursday;
  - after "do my truffle run" was asked at 6:10, nothing is offered again that day.
- **Stopping:**
  - moving during the run stops it; the offer comes back and nothing counts down;
  - a job asked in chat during the run replaces it; the offer comes back;
  - a failed step gives the toast; the same failure the next day gives only the chat line;
  - a routine still running at the work stop time stops, and the farmer is not walked to bed;
  - a by-itself run whose chest stands in another farmer's house fails that step with no question.
- **Day rules:**
  - quit at noon without sleeping and load again: the morning routine comes due again;
  - sleep: it comes due the next day.
- **The agent:** after a run, "what did you just do?" is answered from the `routines/ran` context. The briefing names today's routines.
- **Co-op:**
  - a guest's schedule starts on the guest's game only;
  - a guest who leaves at 9:00 and joins again does not get the morning routine twice;
  - a guest with a menu open through the whole window: skipped, with no message.

## Work items

T0 decides the details of the rest. T1 to T3 and T5 give offers, which are usable on their own; T4 adds by itself.

| # | Item | Done when | State |
|---|---|---|---|
| T0 | Checks in the game | Each answered with a note: (a) every kind of input can be read each tick: presses, held keys, cursor, scroll, controller sticks; (b) window focus and pause can be read; (c) at 6:00 the count and the briefing do not collide: the clock held while the agent thinks, the fade-in after waking; (d) `Context.CanPlayerMove` is false with a fishing line cast and during a festival; (e) F7 is free in the game and in SMAPI | Done (see "T0 answers") |
| T1 | `RoutineSchedule` (pure): the `when` fields, checks, due test, run record, order, words; tests | Unit tests | Done: five tests in `ScheduleTests` |
| T2 | `when` carried by `Load`, `Save`, replace and forget; `schedule_routine` with the question box, `unschedule_routine`; `list_routines` shows the schedule and today's state; agent exclusions | The setting tests | Done |
| T3 | The due loop: window, the show conditions, the offer line, the start key, the run record, the cached file | The offer tests | Done |
| T4 | By itself: the untouched test, the count, the chain, no questions in such a run | The starting tests | Done |
| T5 | A stop cause on `JobResult`; the stop table; no bed walk after a schedule's run; toasts and chat lines; the quieter repeat; `routines/ran` to the agent | The stopping tests; the agent test | Done |
| T6 | Briefing line and facts; config switch and key; system prompt; README | The briefing shows today's routines | Done |

### T0 answers

- **(a) Input:** presses come from SMAPI's `ButtonPressed` (keyboard, mouse and controller buttons). Each tick the mod also reads held keyboard keys, the cursor (more than four pixels), the scroll wheel, the controller's sticks and triggers, and the farmer moving while no job runs. A key press and a mouse movement were checked in the game. There is no controller on the test machine, so the test kit set SMAPI's own controller state (`SInputState.ControllerState`, which the game reads through `Game1.input.GetGamePadState()`) before each update for half a second. A tilted left stick, and on a second reload a pulled right trigger, each stopped the count ("Routines: input (the controller)").
- **(b) Focus and pause:** `Game1.game1.IsActive`, `Game1.paused` and `Game1.HostPaused`. The count does not run without focus, and the cursor is not read then, so coming back to the window does not count as input.
- **(c) The briefing:** no collision. On a morning with the agent's briefing, the agent's turn holds the offer (`AgentState.Busy`); the routine started five seconds after the briefing arrived. Two seconds of grace after the day starts cover the fade-in.
- **(d) `Context.CanPlayerMove`:** false with a fishing line cast (checked at the beach during `fish`: `UsingTool` and `isFishing` true). **True inside a festival** (checked at the Luau), as the review suspected, so the explicit `Game1.isFestival()` check is what hides the offer there; `Utility.isFestivalDay()` keeps by-itself starts off for the whole day.
- **(e) F7:** free: the game does not use it, and pressing it started the routine on offer.

## Checked in the game (2026-11-17)

On the test save, with the agent (Qwen) connected:

- **Setting:** `schedule_routine` for the truffle run, 6am, by itself, showed *Start "truffle run" by itself every morning, if you touch nothing after waking? (From tomorrow. Any key keeps the farmer.)*; after "Yes" the file held `when` with `setOn`, and `list_routines` said "takes effect tomorrow". A gift routine asked to start by itself, and a 9pm schedule, were refused with the reasons.
- **By itself:** on the next morning, untouched, the agent's briefing came first ("... "truffle run" starts by itself if you leave the farmer alone after waking"), then the count line, then the toast *"truffle run" started by itself: move to take over*, and the routine ran.
- **Cancelled:** reloading the morning and pressing Shift during the count, or moving the mouse, stopped the count ("Routines: input (LeftShift)" and "(the cursor moved)"); nothing started, and the offer stayed.
- **Offers:** F7 started the offered routine. A failed step gave the toast *"gift test" (every morning) stopped at step 1 (give Daffodil to Lewis ...): There is no Daffodil in the backpack.*, and the next due routine was offered.
- **Once a day:** `run_routine` from chat marked the routine "ran today" and it was no longer offered.
- **Stops from outside:** a farm cutscene during a by-itself run, and a `water_crops` asked in chat during an offered run ("Replaced by a new job"), brought the routine back as an offer, not counting down.
- **Work stop time:** an offered routine running when the clock was set to 11:30pm stopped with *... work stops at 11:30pm. The farmer was not taken to bed, because nobody asked for the routine just then.*, and the farmer stayed put.
- **The quiet repeat:** the truffle run stopped at step 2 for the same reason as its last scheduled run; no toast was shown, only the chat line.
- **Reload:** quitting at 6:40 and loading again dropped the morning's runs, and the routines were due again.
- **Hidden:** with the autopilot off (F9) and during a lesson, no offer showed; after the lesson, it came back.
- **Replace and remove:** teaching "long walk" again asked *Replace the routine "long walk" (offered every morning)? ...* and kept the schedule. `unschedule_routine` removed one.
- **The chain:** with two by-itself routines at 6:00, the second started as soon as the first was done, with no second count.
- **Hand edits:** a gift routine set to `self` by hand was listed as "not in effect", with the reason, and left out of the briefing.

Further checks, later the same day:

- **Another farmer's house:** a by-itself routine storing into a chest stamped in the guest's cabin failed at once with *The chest is in Guesty's house, and a routine that started by itself does not ask; start it with the key or in chat to be asked.* No question opened.
- **Self-play:** with self-play on (F10), no offer showed. After switching it off, the offer came back once the agent's last self-play turn had ended (`AgentState.Busy` holds it until then).
- **The mines:** at mine level 3, inside a routine's window, no offer showed; at home again, it did.
- **A festival day (the Luau, summer 11):** untouched in the morning, a by-itself routine did not start and was only offered. At the Luau itself, inside a 10am routine's window, no offer showed.
- **Co-op (host and guest on the VM):**
  - the guest's by-itself routine started on the guest's game after both slept, and the host started nothing;
  - with the guest's inventory open from 6:40 to 8:00, its offered routine's window passed with no offer and no message; `list_routines` said "its time has passed today";
  - the guest left at 8:30 and joined again: the morning routine was still "ran today", and nothing started;
  - **inside the window** (the host's clock frozen at 6:00 with `Dev.FreezeTime`): the guest ran its morning routine by itself, left, and joined again at 6:00 with no input. The routine was "ran today" and did not start again, and the guest's other routine was still offered. The first version of the run record failed this test (see "Built differently").
- **The record and the save, in single-player:** after a by-itself run at 6:00, loading the same morning again without saving made the routine due again, and it ran. After sleeping, it ran again on the next morning.

**Controller:** there is no controller on the test machine, and a virtual one needs root. The sticks and triggers were checked through SMAPI's controller state instead (see "T0 answers"). Controller buttons arrive through SMAPI's `ButtonPressed`, the same path as the keyboard keys that were checked.

## Built differently from the design

- **A by-itself routine goes first.** When an offered and a by-itself routine are due at the same time, the by-itself one is counted down first while it may start; otherwise the order is by time, then name. Found in the game: an offer due at 6:00 hid a by-itself routine behind it.
- **No skip messages at all.** A routine whose window passed is only shown as "its time has passed today" in `list_routines`.
- **The count starts again** from five seconds when a condition breaks (for example the agent starts a turn), rather than pausing.
- **A stop by the player, a screen or a replacing job** goes to the chat only, with no toast: the player caused it or can see it.
- **`list_routines`** says "not in effect" for a schedule that fails its checks, with the reasons.
- **Today's runs are kept in the `modData` of the player's home**, keyed by the player's id, not in `routine-runs.json` with a time rule. That file now keeps only how each scheduled routine last ended. Found in co-op: a guest who reconnected in the same ten game minutes as the morning run had the run dropped by the time rule and ran the routine a second time. The time rule cannot tell "the host loaded the morning again" from "a guest reconnected". The home's `modData` follows the save: loaded again without saving, it is gone, and the routine is due again. Because the host holds the locations, it survives a guest reconnecting. The farmer's own `modData` was tried first; a reconnecting guest got it back empty.
- **No-question runs:** besides the chest in another farmer's house, `Confirm.Ask` itself refuses in a by-itself run, so no future step can open a question either.

## Review (2026-11-17)

An independent review checked the first draft against the code. Its findings, and what changed:

- **Must fix, accepted:**
  - **An unasked routine could end in an unasked sleep.** At the stop time the mod stops the job and walks the farmer to bed, answering the sleep question itself. A schedule's run now only stops.
  - **Agent turns.** The agent's next job replaces a running job, so a routine could start while the model was thinking and be killed. Nothing is offered during an agent turn, and "replaced" brings the offer back.
  - **A routine could run twice a day.** "Once a day" counted only schedule starts. Any run today now counts.
  - **The hands-off rule contradicted itself and was too weak.** The draft started a routine whenever the player stood still for five seconds in the window. Now the first input cancels by itself for the day.
  - **Input detection was too narrow.** `IsTakeControlInput` misses toolbar keys, the cursor, the scroll wheel and held keys. The untouched test now reads every kind of input.
  - **Festivals.** `Context.CanPlayerMove` may not exclude a festival, and the walker has no festival check. Festivals are now named in the show conditions, and nothing starts by itself on a festival day.
  - **The result line would not be seen.** System lines are not toasts. Starts and results are now toasts.
  - **"Nothing to do is quiet" was false.** Most jobs walk before they look. The doc now states what each job does and accepts it for round one.
  - **Right size.** By itself is now only for the 6:00 schedule, from home. This removed the horse, travel, the spa and most festival cases.
- **Should fix, accepted:**
  - **Three kinds of stop looked the same.** `JobResult` gets a stop cause.
  - **Full self-play.** A routine that `run_routine` would refuse is not offered.
  - **The self-play part had no plumbing.** It is cut from round one.
  - **`when` would be dropped by today's file code.** The carry-over on load, save, replace and forget is now in T2.
  - **`schedule_routine` needs a runner job.** The doc now says what follows from that.
  - **A by-itself run could still open a question** (another farmer's home). That step now fails instead.
  - **Reading the file every tick.** The routines are now cached.
  - **A test that cannot happen in single-player** (a menu open through the window; the clock stands still in menus). It is now a co-op test.
  - **A guest who reconnects lost the record.** The record is now a file, with entries later than the current time dropped on load. (Replaced after a co-op test by the home's `modData`; see "Built differently".)
  - **Several routines due at once.** They chain on one count, and a player stop ends the chain.
  - **Lines every day.** An ignored offer leaves no message; a repeated failure goes to the chat only.
- **Smaller points, accepted:** seasons cut; one time rule (three hours before the stop); the example no longer claims the player has read the briefing; the briefing line's rank; a new agent message instead of the warning's; the key can be a controller button; the config default; a schedule begins the next day; hand edits setting `self` are deliberate; the diary label.
- **Not taken:** a "has work" test per job in this round. It is the right fix for empty runs, but it is a job-by-job change of its own. With by itself limited to the morning at home, the empty walks are short, so it is first under "Later".
