# Daily helpers and chores: design

Date: 2026-11-16
Status: all work items done on 2026-11-16 (see the State column); not yet released. Revised after an independent review on 2026-11-16 (see "Review" at the end); the owner set the goal to implement it, so the open decisions follow the recommendations.

After v0.3.1 the copilot can answer questions from the game and run single jobs. This design adds two things: helpers that make it useful every game day without being asked (Part 1), and chores that cover the daily farm work the autopilot cannot do yet (Part 2). Larger ideas are listed in Part 3 and are not designed here.

## What already exists

Several of the ideas overlap with things already built. They are listed here so nothing is built twice.

| Idea | Already there | Missing |
|---|---|---|
| Morning briefing | Tips for today's birthdays and festival (from 6:10am), evening tips for tomorrow's weather, festival and birthdays | One joined-up morning message; cart, luck, machines, quests, tool upgrade, yesterday's result |
| Bundle tracker | `get_bundles` (what is still needed), warnings when a bundle item is sold or shipped | Whether the player already owns the item, where to get it now, and until when |
| Gift advisor | `get_gift_tastes`, `get_relationships` (gifts given today and this week), `give_gift` | Matching tastes against what the player owns |
| Item finder | `get_location` lists the chests of one map | Searching all maps at once |
| Evening recap | A day summary: written by the agent in self-play, or by the mod itself on any day the autopilot ran a job | Days with no autopilot job leave no record; shipping income is not in it |
| Memory between sessions | Notes, standing goal, today's plan, the last three day summaries, all per farm and player | Nothing. This idea is dropped from the list |

## Part 1: Daily helpers

All of Part 1 is read-only. The rule from the knowledge design applies: every result says which parts are **raw** (copied from the game), **asked** (answered by one of the game's own functions) or **computed** (worked out by our code, so worded as an estimate).

### `find_items`

Answers "where is my copper?" and gives the other helpers their view of what the player owns.

- **Input:** an item name or id, or nothing for a full list grouped by item.
- **Output:** each place the item is, with count and quality.
- **Source:** every map in the game's own list of maps, building interiors included. The game also has a walk over all items (`Utility.ForEachItemContext`), but it returns far more than storage: every stone and weed on every map, machines themselves, furniture, hats on pets, shop buy-back lists, and offline players' backpacks. So the tool looks only at a fixed list of places:

  | Kept | Reported as |
  |---|---|
  | The local farmer's backpack | Backpack |
  | Chests the player placed, on any map and inside farm buildings (mini-fridges included) | Map, tile, chest |
  | Fridges | Map, tile |
  | The shared Junimo chest contents | "Junimo chest", once, with the tile of one placed Junimo chest |
  | Machines whose output is ready | Map, tile, machine |

- **How obtained:** the choice of places is ours. Computed.
- **Left out on purpose:** other players' backpacks, equipment and homes (cabins and the farmhouse if it is not the player's own), gift boxes not yet opened, dressers, items on the ground, the shipping bin.
- **`fetchable`:** each place says whether `take_from_chest` can fetch from it: chests, fridges and the Junimo chest (see the next item).
- **Co-op guest:** maps no player is on can be out of date. Results from them carry the same `possiblyStale` flag the state tools use. Farm building interiors are kept live for a guest (checked in a two-player session), so storage on the farm is not flagged.

### `take_from_chest`: more places

Today the action only finds chests the player placed on a map, reads a Junimo chest as empty, and cannot choose a quality. So that the agent can fetch what the finder found, it gets:

- fridges and Junimo chests;
- an optional minimum quality (bundles need this).

Farm-building chests, auto-grabbers and dressers stay out of this round.

### `get_gift_ideas`

- **Input:** a villager, or nothing. With nothing it covers villagers whose birthday is today or in the next seven days, then villagers who can still take a gift this week.
- **Output:** for each villager, the items the player owns that they love or like, best first, with where each item is (from `find_items`), plus how many gifts they can still take today and this week.
- **Taste:** asked of the game for each owned item (`NPC.getGiftTasteForThisItem`). This includes universal tastes and the special cases the data tables do not show.
- **Gifts left:** the game's rule is inside its gift-giving code and cannot be asked without holding the item, so we apply the same rule ourselves: one gift a day, two a week, and the weekly limit does not apply on a birthday or to a spouse. Computed.
- **Never suggested:** tools, quest items, and the number of each item a bundle still needs. Extra copies beyond what the bundles need can be suggested (`spare`). The bundle slots that need the best quality are filled first, each with the lowest quality that is good enough, so one stack is never held back twice. The existing bundle check ignores counts, so this is a new count-aware one.
- **Spoilers:** each idea says whether the player has already discovered that taste in the game (`Farmer.hasGiftTasteBeenRevealed`). See "Spoilers" below for how the agent uses it.

### `get_bundle_plan`

The knowledge design left out a full "how to get this item" index as large and spoiler-heavy. This tool is deliberately narrower: only the items a visible bundle still needs, and only the four kinds of source the knowledge tools already read well.

`get_bundles` stays as it is. The new tool adds, for each missing item:

- **Owned:** where the player already has it at the needed quality (from `find_items`).
- **How to get it:**

  | Kind | Source | What is reported |
  |---|---|---|
  | Crop | `Data/Crops`, shop stock | Seed, shop and price, seasons, and whether it would still ripen if planted today |
  | Fish | `get_fish_info` | Maps, seasons, hours, weather, and whether it can bite now |
  | Shop item | Today's stock of the known shops and the Traveling Cart | Shop and price today |
  | Anything else | Not read | "Source not in the data I read". The tool does not guess |

  Forage, made items and animal products are left for a later round.
- **Available until:** only when the item is truly seasonal: a crop when the greenhouse is not open, a fish that does not bite all year. It gives the last day this season it can still be got (for a crop, the last planting day) and whether the next season also has it. Computed. Nothing is reported for items available all year.
- **Bundles that need only some of their items** report how many more are needed, so the agent does not chase all of them.
- **No bundles:** when the Community Center is not open yet, is finished, or the farm chose Joja, the tool returns that reason, as `get_bundles` does.
- **Remixed bundles** work without extra code, because the tool reads the save's own bundle list.
- **Spoilers:** a source in a place the save has not reached is marked `reached: false`, as in the fish tool.

There is no separate "last chance" tip. The morning briefing carries that line.

### End-of-day record

The mod already writes a plain day summary when the agent wrote none, but only if the autopilot ran at least one job. Two changes:

- It is written on every day, including days the player played entirely by hand.
- It includes the value of the local player's shipping bin, from the game's own sell prices, taken just before the day ends. The game pays out after that moment, so the money figure stays "before shipping", as today.

In co-op with separate wallets, both figures are the local player's own.

### Morning briefing

One message at the start of the day instead of several separate tips.

- **Facts gathered by the mod** in one new tool, `get_day_briefing`. Lines with nothing to report are left out:
  - weather today, and tomorrow's forecast;
  - luck, as one of the TV fortune teller's levels (see below);
  - birthdays today, each with the best gift idea the player owns;
  - a festival today;
  - crops ready to harvest, crops that die tomorrow because the season ends, and machines with output ready;
  - the Traveling Cart, when it is in the forest today: only the items a bundle still needs;
  - quests that run out today or tomorrow, and a finished tool upgrade waiting at the blacksmith;
  - bundle items that can be got for at most three more days this season;
  - yesterday: the value shipped and the money change, from the end-of-day record.
- **Luck:** the TV's own fortune function cannot be used: it draws random numbers and picks between two wordings. We apply the TV's thresholds to the day's luck value and report its level (very bad, bad, slightly bad, neutral, good, very good, the best). Computed.
- **Cart:**
  - "In the forest today" is asked of the game's own check, not worked out from the weekday. The Night Market and Desert Festival carts are not covered.
  - The game builds a shop's stock from a random generator seeded by the save and the day, and takes off what was already bought. So the morning's answer should be what the player sees. This is checked when building by asking twice on a cart day and comparing with the cart's menu, because a shop entry's own conditions could still be random.
- **Delivery:** the agent starts a fresh session at the start of every game day and seeds it from memory. The briefing facts travel in that seed message, and the seed asks for the briefing as its first reply. A separate tip at 6:10am would race the session change and could be lost.
- **Wording:** the model rewrites the mod's own text, one point per line: every point kept, at most five lines, birthdays, festival and last chances first. It answers from the facts only and calls no tools; in an early test it looked up gift ideas itself and suggested a taste the player had not discovered. The two-sentence limit of ordinary tips does not apply.
- **Birthdays without a known gift:** the facts say that no owned gift has a taste the player has discovered, so the model does not wrongly say the player owns nothing the villager likes.
- **No agent:** if no agent is connected, the mod shows its own fixed-text list.
- **Once per day:** loading a save again on the same game day does not repeat it.
- **Day 1 and festival days:** on the first day of a new save it shows as on any other day; most lines are empty. On a festival day the festival is the first line.
- **Settings:** a new `MorningBriefing` option. It is off when chattiness is Off. It does not count against `MaxTipsPerDay`. When it is on, the separate morning birthday and festival tips are not sent.
- **Self-play:** the facts are part of the day's seed as above, and the agent plans the day from them instead of writing a chat message.

**Cost.** Today a day the player plays by hand, without asking anything, costs no model requests. With the briefing on, each game day costs at least one request, with the day's seed as input, and more if the model calls a tool to improve it. In self-play it counts against the daily request budget. See open decision 2.

### Spoilers

The knowledge design's rule is that unreached content is left out unless the player asks. The helpers follow it:

- **What the copilot says unasked** (the briefing, tips) uses only gift tastes the player has discovered and names only places the save has reached, unless "tell me everything" is on.
- **When the player asks** ("what can I give Abigail?"), the agent may use undiscovered tastes but says that it is doing so.

### Guide prompt

The system prompt gets a short section on when to use the new tools, in the same style as the knowledge tools section.

## Part 2: Chores

Part 2 acts in the game, so the existing autopilot rules apply unchanged: jobs act the way a player does (walk there, press the button), stop when the player moves, respect energy and bedtime, and ask before irreversible actions.

### `get_animals` (read-only)

- **Each animal:** kind, name, building, whether it is inside or out on the farm, age, hearts, whether it was petted today, and whether it has produce ready and which tool that needs. Raw.
- **Each building:** door open or closed, feeding spots filled and empty, and whether it feeds by itself.
- **Hay:** the farm keeps one shared hay count; it is not stored per hopper. The tool reports that count and the silo capacity.
- **Pets:** the cat or dog is listed too (name, hearts, petted today), as the game's own animal page lists it.

### `tend_animals`

One job per building, or all buildings:

1. Pet every animal not petted today, inside the building or out on the farm.
2. Pick up produce lying in the building. Empty an auto-grabber if there is one (through its item menu).
3. Milk or shear animals with produce ready, when the farmer has the tool.
4. When the building does not feed by itself: take hay from the hopper and put it out, one portion per animal living there (the hopper's own limit).
5. When all buildings are tended, pet the cat or dog if it is on the farm or in the player's house.

Rules found in the game's code that the job must follow:

- **Not after 7pm:** from then on, petting opens a "trying to sleep" message instead. The job refuses to start and says why.
- **Safe hands:** petting while holding a Golden Animal Cracker uses the cracker up, and the hopper only gives hay to empty hands. The job selects an empty slot or a tool first, as other jobs already do, and stops if it cannot.
- **Hay:** the hopper hands out only as much hay as there are animals without hay on the benches. Animals that grazed outside are full and leave yesterday's hay.
- **Milk Pail and Shears** look for the animal's harvest area (`FarmAnimal.GetHarvestBoundingBox`), which is not where its body is; the job aims there. Produce of a higher quality does not stack with normal produce, so the backpack check uses the real item.
- **Doorways:** an animal standing in a building's doorway blocks the path; navigation now waits briefly and looks again before calling the way blocked.

The result lists what was done and what could not be done, for example "no Milk Pail" or "no hay left". Buying hay stays a separate `buy_items` call, because it spends money. Truffles outdoors are left to `collect_forage`, which picks up any spawned object on a map.

### `tend_machines`

- **Collect:** take the output of every ready machine on a map, optionally only one kind of machine. Stops when the backpack is full.
- **Refill (optional):** load a named item from the backpack into empty machines of one kind, for example "fill the kegs with Hops".
  - Before walking, the job asks the game whether the machine accepts the item (the same probe `get_machine_info` uses).
  - Some machines need several of the item, or fuel as well (a furnace needs five ore and one coal). The job reads both from the machine's rules and reports what is missing instead of trying.

### Routines (outline only)

A routine would be a named, ordered list of action-tool calls, for example "morning": `water_crops`, `harvest_crops`, `tend_animals`, `tend_machines`, run by the mod with no model request between the steps and started with a chat command such as `/routine morning`.

The review showed this is the riskiest part and does not fit the job runner as it is, so it is **not a work item here** and gets its own design. That design must settle at least:

- **One owner at a time:** the job runner replaces a running job when a new one starts. A routine and the agent would cancel each other's jobs.
- **What ends a routine:** the player moving ends a job as "partly done", not "failed". Any stop the step did not cause itself must end the whole routine.
- **Steps that must not be allowed:** `go_to_bed` has no confirmation, so a routine containing it could end the day at 6am.
- **Confirmations:** full self-play switches all confirmations off, and "don't ask again" is permanent. A routine must not widen either.
- **Running at day start:** the autopilot switch is on by default and is not saved, so it cannot be the safety gate. Day start also fires when a save is loaded, and festival days need skipping.
- **Hand edits:** the file must be checked when it is run, not only when it is saved, and the nightly memory save must not overwrite a hand edit.

Until then the agent can already chain the new jobs itself, one model request per step.

## Part 3: Later candidates

Not designed here. Each gets its own design when started.

1. **Routines**, as outlined above.
2. **Better mines and combat:** choose a weapon, eat when health is low, retreat safely, use the elevator.
3. **Translations:** the model already answers in the player's language, but the mod's fixed texts (warnings, fallback tips, the settings page) are English only. SMAPI's translation files would carry them, Chinese first.
4. **More sources for the bundle plan** (forage, made items, animal products) and **more places to fetch from** (farm-building chests, auto-grabbers).
5. **Chest sorting:** an autopilot job that moves items between chests by kind. `find_items` is its first building block.
6. **In-game settings menu** and a **usage view**, as listed in the knowledge design.
7. **Voice:** speaking to the copilot and hearing it. It needs speech services or local speech models, which is a new dependency and a new privacy question.
8. **AI farmhand in co-op:** the copilot joins as a second farmer. The main design decided the copilot "always drives the local player's own farmer, never a separate bot character", and this would reverse that. It also needs a second running copy of the game (about 600 MB of memory in our two-player test), and a co-op guest's game lacks villager schedules and has out-of-date data for maps no player is on. Not recommended now.

## Co-op and privacy

- The new tools read only what the local player can see. Chests are shared in co-op, so they are included. Other players' backpacks are not.
- Animals and machines belong to the farm, so tending them counts as normal farm work. Jobs do not enter other players' cabins.
- Nothing in this design sends new kinds of data anywhere: the facts go to the model the player chose, as tool results do today.

## Testing

- **Read-only tools:** an answer-check list as in the knowledge design. Each tool is run on the test save and compared with what the game shows: the chests and fridge opened by hand, the bundle menu, the animals' own menus.
- **Gifts:** there is no game screen for "gifts left", so it is checked by giving gifts: the game must accept or refuse as the tool said, on an ordinary day, after two gifts in a week, and on a birthday.
- **Cart stock:** on a cart day the tool is asked twice and both answers are compared with the cart's menu.
- **Jobs:** the test save gets a coop and a barn with animals and a row of machines (added with debug commands). Each job is run and the game state compared before and after.
- **Guide:** five questions answered in the game, one per new tool plus the briefing.

## Work items

Each can be set as a goal by itself.

| # | Item | Done when | State |
|---|---|---|---|
| D1 | `find_items` | Counts match the backpack, two chests on different maps, the fridge and a Junimo chest on the test save; stones, furniture and machines themselves do not appear; a co-op guest gets the stale flag | Done 2026-11-16. On the test save: Copper Ore 42 = Farm chest 30 + house chest 12; Parsnip 5 with normal x3 and gold x2; Egg 6 in the fridge; Copper Bar 4 in the Junimo chest; Beer in a ready Keg. Map stones, furniture and the Keg itself are not liste. Co-op (two-player LAN session on the VM): the guest saw the farm's chests and a chest in the coop, and not the host's farmhouse; when the host took 2 Coal from the coop chest with nobody inside, the guest saw 5 at once, so farm building interiors are live and correctly not flagged; Town is flagged possiblyStale on the guest by the same check |
| D2 | `take_from_chest`: fridge, Junimo chest, quality | An item is fetched from each; a gold-quality item is chosen over a normal one when asked | Done 2026-11-16. Eggs from the fridge, Copper Bars from the Junimo chest, a gold Parsnip chosen when gold was asked and a normal one otherwise. A full backpack now says so instead of "no such item". Store and take also match the shown name, because several items are called "Stone" |
| D3 | `get_gift_ideas` | For two villagers the game reacts to the gift as the tool said; the game accepts or refuses in the three gift-limit cases as the tool said; an item a bundle needs once is held back once, not entirely | Done 2026-11-16. Gus (loves Orange, birthday, 2 gifts that week): tool said 1 left, game took it, +640 points. Lewis (likes Orange): tool said 1, game took it, +45. Pierre after 2 gifts that week: tool said 0, game refused. Pam: 2 of 5 Parsnips spare (1 normal for Spring Crops, 2 gold kept for Quality Crops); Daffodil 6 of 7 |
| D4 | `get_bundle_plan` | For one crop, one fish and one shop item the source and "available until" match the game data; an all-year item has no date; another kind of item says the source is not read; a Joja save returns the reason | Done 2026-11-16. Melon: Pierre 80g, 12 days, last planting day summer 16; Sunfish: town and forest in spring and summer, until summer 28 (matches get_fish_info); Hay and Wood: today's stock at Marnie's and Robin's; Catfish and Spring Crops in summer say "not this season"; Copper Bar and forage say the source is not read; a Joja save returns the reason. Fishing junk (Wood, Stone) is not counted as fish; fish seasons use the reached places when there are any; the Missing Bundle is left out |
| D5 | End-of-day record on every day | A day played by hand leaves a summary with the right money and shipping-bin value | Done 2026-11-16. Summer 8 played by hand (100g received, 3 Parsnips in the bin, slept): the record says +100g before shipping, 105g in the bin (the game's shipping screen also showed 105g) and no autopilot jobs. Mini shipping bins are counted too |
| D6 | Morning briefing | Shows once on a normal day, a birthday, a festival day and a cart day, and not again after reloading the save; cart lines match the cart's menu and two calls agree; the setting switches it off; with no agent the fixed text shows | Done 2026-11-16. Normal day (summer 9), Maru's birthday (summer 10), the Luau (summer 11, festival first) and a cart day (summer 12) each showed one briefing; reloading summer 9 did not repeat it. Cart lines (Pomegranate 900g, Bream 800g, Maki Roll 1100g) match the cart's own menu, and two calls agree. With the copilot stopped, the fixed text showed at once; with MorningBriefing off, nothing showed |
| D7 | Guide prompt and spoiler rule | Five guide questions answered correctly in the game; a briefing on a save with an undiscovered taste does not use it | Done 2026-11-16. Five questions answered correctly in the game: where the copper ore is (42: farm chest 30, house chest 12), gifts for Maru (Cauliflower loved, marked as not discovered, one held back for a bundle), the Summer Crops plan (last planting days and Pierre's prices), today's priorities, and fetching 2 eggs from the fridge (it freed a backpack slot first after the new "backpack is full" message). On Maru's birthday the briefing did not suggest the undiscovered Cauliflower |
| C1 | `get_animals` | Matches the animals' own menus and the buildings on the test save; the hay count matches the silo | Done 2026-11-16. Coop (2 chickens, one outside) and Barn (1 cow) on the test save: 4 feeding spots each, hay 20 of 240 silo capacity, eggs on the floor and in the auto-grabber, milk ready. Names, hearts and petted state match the game's Animals tab; the cat is listed as there |
| C2 | `tend_animals` | All animals petted (inside and outside), produce collected, feeding spots filled; missing tool and no hay are reported; it refuses after 7pm; a held Golden Animal Cracker is not used up | Done 2026-11-16. All animals petted inside and outside, plus the cat; floor and grabber eggs collected; milk collected (3 of 3 in a row after aiming at the harvest area); hay put out one per animal (2 in the coop, 1 in the barn, silo 20 to 17). No Milk Pail and no hay are reported; it refuses at 7pm; a held Golden Animal Cracker is kept |
| C3 | `tend_machines` | Ready output collected; kegs refilled with a named item; a furnace without coal and an item the machine refuses are both reported before walking | Done 2026-11-16. Two ready Kegs emptied (Beer x2); two empty Kegs filled with Hops (Pale Ale, 2250 minutes); a Furnace with Copper Ore and no Coal, and a Keg with Copper Ore, are refused before walking (0 game minutes) |

Suggested order: D1 to D7, then a release (0.4.0). Then C1 to C3 and a second release (0.5.0). Routines follow with their own design.

## Decisions

Settled on 2026-11-16, following the recommendations, when the owner set the goal to implement this design.

1. **Luck in the briefing.** The player normally learns the day's luck by watching TV. Recommendation: include it. The TV is in the house from day one and costs nothing to watch.
2. **Briefing on by default.** It changes a hand-played day from no model requests to at least one. Recommendation: on by default when the chattiness setting is Important or Talkative, since players at those settings already accept tips that cost requests. The setting switches it off.
3. **Routines.** Recommendation: build Part 1 and the two chore jobs first, then design routines separately.
4. **AI farmhand.** Recommendation: not now, for the reasons in Part 3.

## Review

An independent review on 2026-11-16 checked the first draft against the mod's code and the 1.6.15 game code. Its findings, all taken up above:

- **Item walk:** it returns placed objects, furniture, buy-back lists and offline players, so `find_items` now keeps a fixed list of places.
- **Fetching:** `take_from_chest` could not reach fridges or Junimo chests or choose a quality. This is now work item D2.
- **Briefing timing:** a tip at 6:10am would race the day's session change. The briefing now travels with the day's seed.
- **Luck:** the TV's fortune function draws random numbers, so the thresholds are used.
- **Cart:** the day-seeded stock is confirmed in the game code; the shop's own entry conditions could not be read, so D6 checks it in the game.
- **Day record:** the first draft wrongly said it existed only in self-play.
- **Gift limit:** the birthday exception is confirmed; the rule cannot be asked of the game, so it is computed and checked by giving gifts.
- **Spoilers:** unasked output is now limited to discovered tastes and reached places.
- **Bundle plan:** cut to crops, fish and shops; "available until" only for truly seasonal items; the separate tip is dropped.
- **Animals:** hay is one farm-wide count; petting is blocked after 7pm; safe hands are required.
- **Routines:** moved out to their own design, with the problems found listed.
- **Cost:** the briefing's real cost is now stated.
