# Put it away my way

Date: 2026-11-16. Status: implemented on 2026-11-16; not yet released.

The copilot can store items in one chest it is told about (`store_in_chest`). This round adds one job: "put my things away" empties the backpack into the player's own chests, **the way the player already sorts them**. Copper Ore goes to the chest that already holds Copper Ore. A new mineral goes to the chest that holds most minerals.

This is the "Where things go" item from apprentice mode's later rounds ([design](2026-11-16-apprentice-mode-design.md)). It was first designed as a learned habit: chest ids stamped into `modData` and a per-chest store log. The review showed that the chests' own contents already are the player's sorting, visible on day one, with no ids, no new log, no co-op stamping and nothing to forget. That design is kept under "Not done" in case the contents turn out not to be enough.

## Why it is safe to act on

Storing is reversible: the item stays the player's, in a chest the player already uses for it. It runs only when asked: "put my things away", or as the storing step of "do my usual morning". Self-play that follows the player's habits may run it as that step too, because it is reversible.

## Which containers count

Only real storage:

- **Placed chests** whose type is plain or big: wood, stone and big chests. That means `playerChest`, not a gift box, and a `SpecialChestType` of `None` or `BigChest`.
- **Fridges:** the house fridge and mini-fridges.
- **Never:**
  - mini-shipping bins (their contents are sold);
  - hoppers (auto-loaders), enrichers and auto-grabbers;
  - the Junimo chest (a shared transfer box of nine stacks);
  - dressers;
  - anything in another farmer's home.

**A bug today:** mini-shipping bins, hoppers and enrichers are `Chest` objects created as player chests, so three places treat them as chests. This round fixes all three, because otherwise "put away" could sell things:

- `store_in_chest`'s chest choice (`GoToChest`);
- `find_items` (`ItemFinder`);
- the watcher, which logs a deposit into them as "stored".

The watcher logs a mini-shipping bin deposit as selling, and ignores hoppers and enrichers.

## Where an item goes

Checked for each backpack item, in this order:

1. **Same item:** the counted chest that holds the most of that item, by count (any quality).
2. **Same kind:** the counted chest that holds the most stacks of the item's category. It needs at least two stacks, so one stray stack does not decide. Category 0 (none) and litter (`-999`) do not count.
3. **Otherwise** the item stays in the backpack, and the report says "no chest has this kind of item yet".

The fridge is only chosen by these rules too: eggs go there when the fridge holds eggs.

## What stays in the backpack

- **Tools**, always.
- **The toolbar row** (the first 12 slots), unless `includeToolbar` is true. Players keep the day's seeds, food and bombs there.
  - On the starting 12-slot backpack the toolbar row is the whole backpack. The job then says so, instead of doing nothing silently.
- **The item in the farmer's hand when the job starts**, recorded at the start because walking changes the selected slot.

## The job `put_things_away`

| Argument | Default | Meaning |
|---|---|---|
| `includeToolbar` | false | Also put away the first row |
| `items` | all | Only these items |

1. **Plan:** each item that can be put away gets its chest. The chests are then grouped by map: the current map first, then the farm, the farmhouse and the rest by name. There is no route optimiser; the walker goes map to map.
2. **At each chest:** walk next to it, take the chest's lock (`GetMutex().RequestLock`, as the game does when a player opens it), then add the items with `Chest.addItem`, which adds to existing stacks first. Then release the lock.
   - A chest that is locked by someone else is skipped after a short wait.
3. **Problems:**
   - **A full chest:** the items stay and are named.
   - **An unreachable chest or map:** skipped, and the job goes on.
   - **The usual job limits** (three failures and stop, bedtime, energy floor) apply.
4. **The report:** what went where (chest by map, tile and colour), and what stayed and why (no chest for its kind, chest full, toolbar, in hand).
5. **`store_in_chest` gets the lock too.** It never took the lock, so storing while another player has the chest open was a race.

## Apprentice mode and "do my usual morning"

- `Act.Job(Act.Store)` is null today, so a usual morning that includes storing names it but does not run it. It becomes `put_things_away`, and `Act.OfJob("put_things_away")` is `Store`. Without that, a day the copilot did the storing would count against the habit.
- **Storing runs last.** In the morning steps, storing is moved after the other steps, whatever its usual time, so seeds and fertilizer are not put away before `plant_seeds` needs them. The step keeps its own usual time in the text.
- Because the job list changes, a player whose usual morning includes storing gets the briefing's one-time offer once more. That is wanted, because the offer now does more.

## Co-op

- Chests are shared. A guest sees chests on maps it has not loaded as possibly stale (as `find_items` says). The plan uses them anyway, and the chest is checked again on arrival, when the map is live.
- The lock makes two players storing at once safe.
- Other farmers' homes are never looked into or stored in.

## Limits

- **A new kind with no chest yet stays** in the backpack. The copilot does not invent a sorting.
- **Quality is ignored**, so a player who keeps gold items in their own chest is not followed.
- **A chest the player uses for two kinds** is fine; one used for "everything" attracts everything that has no better home. That is still the player's own sorting.

## Not done (the learned design)

These were designed in the first draft and dropped after the review:

- chest ids in `modData` (they survive a save, a kick and an upgrade; checked in the game on 2026-11-16, P0);
- per-chest store events in the day log;
- item and category habits with two-thirds evidence.

They would only add value for items that are in no chest yet. If they are built later:

- a kick moves a chest up to three tiles, so look a chest up by id only;
- count as stored only what the chest gained, because the chest menu also has a trash can;
- only the host drops chests that are gone.

## Work items

| # | Item | Check | State |
|---|---|---|---|
| P1 | Real-storage filter in `GoToChest`, `ItemFinder` and the watcher; lock in `store_in_chest` | A mini-shipping bin is not offered as a chest by `find_items` or `store_in_chest`; a hand deposit into it is logged as selling | Done |
| P2 | Pure `PutAway` planner (same item, then category, keeps), with tests | Unit tests | Done |
| P3 | `put_things_away` job and report | On the test save: ores, fish and a new mineral each end in their chest; toolbar and held item kept; full chest reported; an item of no known kind stays | Done |
| P4 | Morning habit: storing's job, storing last; agent prompt ("put my things away"); README | "do my usual morning" with storing runs it last | Done |

## Checked in the game (2026-11-16)

On the test save, with a minerals chest (two minerals), a mini-shipping bin holding 99 Copper Ore and 50 eggs as a trap, a chest full of coal, the fridge with 12 eggs and the big chest with 30 Copper Ore:

- **The 12-slot backpack:** nothing moved, and the note said the toolbar row is kept by default.
- **24 slots:**
  - Copper Ore went to the big chest, not the bin; eggs went to the fridge; the new mineral (Frozen Tear) went to the minerals chest.
  - Coal stayed, with "the chest in the farmhouse (25,6) is full", and the job ended "partly done".
  - The toolbar row and the Bream in hand stayed.
- **`find_items`** no longer lists the mini-shipping bin.
- **Watcher**, with the chest menus driven by the test kit:
  - a deposit into the bin was logged as "sold Copper Ore x2";
  - an item stored in a chest was logged as stored;
  - an item removed while a chest was open but not added to it (as the trash can does) was not logged as stored.
- **In chat**, "put my things away, the toolbar too": the agent called `put_things_away` with the toolbar included. Cauliflower went to the chest with two stacks of Parsnip, and Stone to the chest holding Stone. The reply named what stayed (bait, sunfish, corn seeds) and why.
- **Not checked in the game:** two players storing into one chest at once (the lock), and a guest's run. The lock is the game's own `NetMutex`, used the way the chest menu uses it.
