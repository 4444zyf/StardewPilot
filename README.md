# StardewPilot

An agent-based copilot mod for Stardew Valley. It guides new players and can also control the farmer, either following the player's commands or playing toward a goal the player sets.

It has two parts:

- **Bridge mod** (`mod/`, C#, [SMAPI](https://smapi.io/)): exposes game state and actions over a local, token-protected WebSocket.
- **Agent** (`agent/`, a [pi](https://github.com/earendil-works/pi) extension, TypeScript): runs alongside the game and uses the bridge as its only tools.

See [the design](docs/2026-11-15-agent-copilot-design.md) for the full plan.

## Status

Steps 1 to 4 of the build order work: the read-only bridge, the in-game guide (private chat overlay, proactive tips, instant warnings), the commanded autopilot (the agent walks the farmer between maps and runs tasks such as planting, watering, shopping and shipping when you ask), and self-play toward a goal you set. From step 5: fishing (including the minigame), a basic mines-and-combat routine, and a one-click launcher. Latest release: [v0.3.1](https://github.com/4444zyf/StardewPilot/releases/tag/v0.3.1) on GitHub (not on Nexus): removes the Claude Pro/Max sign-in, which Anthropic's terms do not allow in other apps. v0.3.0 added knowledge tools that answer from the running game, Windows fixes tested in CI, and `pilot_diagnose`.

## Requirements

- Stardew Valley 1.6 on PC, with SMAPI 4.5+
- .NET 6 SDK or newer (to build the mod)
- Node.js 22.19+ and [pi](https://github.com/earendil-works/pi) 1.5+
- An AI model: a subscription (ChatGPT Plus/Pro, GitHub Copilot and others), an API key, or a local model. You set this up on the copilot settings page; no terminal needed. Claude works with an Anthropic API key only: Anthropic's terms do not allow a Claude Pro/Max subscription in other apps, so the copilot does not offer that sign-in.

**Systems.** Played and tested on Linux. **Windows: automated tests pass; not yet played by a person.** If you try it on Windows, please fill in the [Windows test report](https://github.com/4444zyf/StardewPilot/issues/new?template=windows-test.yml). macOS should work but is untested.

**Something wrong?** Type `pilot_diagnose` in the SMAPI console. It prints a self-check (versions, whether Node and pi were found, the copilot's state) without keys, tokens or links, ready to paste into an issue.

## Build and run

1. Build the mod. This also copies it into the game's `Mods` folder:

   ```sh
   dotnet build mod/StardewPilot -c Release
   ```

2. Start the game with SMAPI. The SMAPI console shows `Bridge listening on 127.0.0.1:45781`. On first run the mod writes a random token into its `config.json`, and writes the port and token to a connection file readable only by you (`~/.config/StardewPilot/bridge.json`, or `%APPDATA%\StardewPilot\bridge.json` on Windows).

3. Start the agent. The easy way: set `Launcher.Enabled` to `true` in the mod's `config.json` and restart the game. The mod then starts the agent wrapper, which runs pi for you.

4. Set up the model. In the game press **F8** and type `/settings`. The copilot settings page opens in your browser:
   - **Sign in** with a subscription, add an API key, or add a custom or local endpoint (Ollama, llama.cpp, any OpenAI-compatible service).
   - **Choose a model**, press **Test** (it checks that the model can call tools), then **Save and restart copilot**.

   You can also set up before starting the game: `node <mod folder>/agent/src/wrapper/main.ts setup` opens the same page.

   Sign-ins are stored by pi in the copilot's own pi folder (`~/.config/StardewPilot/pi`, or `%APPDATA%\StardewPilot\pi` on Windows), separate from any other use of pi. Advanced settings on the page can switch to your normal `~/.pi/agent` instead. The game never sees keys or sign-ins. The page only listens on 127.0.0.1 and needs the private link the game opens.

   For development you can still start pi yourself from the repo root, with pi's own `/login`:

   ```sh
   pi --no-builtin-tools -e ./agent/src/index.ts
   ```

   `--no-builtin-tools` keeps pi's shell and file tools off. The extension also enables only the Stardew tools, blocks every other tool call, and replaces pi's coding system prompt. Type `/stardew` to check the game connection.

Ask things like "what should I do today?", "what does Abigail like?" or "which crops need water?".

## In the game

- **F8** opens the private copilot chat. Only you see it; it never goes to the shared co-op chat. In single-player the clock stops while the window is open and while the agent is thinking.
- **Tips**: rule checks in the mod notice things worth saying (crops still dry in the evening, a birthday or festival today, rain tomorrow, low energy far from home). The agent words each tip in the game's language and it appears in the upper left. If no agent is connected, or it does not answer within 45 seconds, a fixed text is shown instead.
- **Warnings** that must land before you act are shown at once as fixed text, without waiting for the model: a Community Center bundle item dropped in the shipping bin or sold to a shop, being out after midnight, energy almost gone.

- **Autopilot**: ask in the chat ("plant my parsnip seeds and water them", "buy 5 cauliflower seeds"). The model plans; fixed C# routines do the work the way a player would, walking, spending energy and game time, and using the game's own menus and actions. Moving or using a tool yourself stops it at once; so does a cutscene, festival or dialogue. **F9** switches the autopilot off and on.
- **Rules the model cannot override**: at 11:30pm work stops and the farmer goes to bed; tool use stops before energy runs out; shipping, selling, gifting and purchases over 500g ask you first in an in-game question ("Yes", "Yes, and don't ask again", "No"). After three failed actions in a row the agent must stop and explain.

- **Self-play**: press **F10**, or ask "play by yourself, goal: earn 5,000g by summer". Without a goal the copilot follows a balanced first-year checklist. Each morning it writes a short plan for the day (shown in the chat; change it by telling the copilot), works through it, writes a summary at bedtime and goes to sleep. Moving, or pressing F10 again, hands control back. A daily model budget (`SelfPlay.DailyRequestBudget`) caps the cost; when it runs out the copilot stops, and goes to bed if it is late.
- **Apprentice mode (learning your habits)**: the copilot keeps a diary of what you do by hand, one line per stretch on one map (for example "6:00-8:40 the farm: Watering Can 44 swings, harvested Parsnip x15, petted 6 animals"), plus your bedtime. From the last two weeks it works out your habits: your usual morning in order, your usual bedtime, and what you do on rainy days or a given weekday. Each habit carries its evidence ("10 of 12 days"). Say "do my usual morning" and the copilot runs the matching jobs in your order. In self-play it plays your way (a goal you set still comes first). Only what you do yourself counts, never what the copilot did. A habit is never permission: shipping, selling, gifting and spending still ask. The diary is plain files in the memory folder (`activity/<date>.json`, `habits.md`), kept for 56 game days. The model you chose sees the lines in its day notes and through two tools, as it sees other game state; nothing goes anywhere else. Type `/forget habits` in the chat to delete it all, or set `Apprentice.Enabled` to `false` to stop watching.
- **Memory** lives in `Mods/StardewPilot/data/<farm>/player-<id>/` as plain files you can read and edit: `notes.md`, `goal.txt`, `plan.md` and `days/<date>.md`. It follows the game's save: what the copilot writes during a day is kept only if the day is saved (you sleep), just like the game. Each game day starts a fresh agent session seeded from these files.

Settings in the mod's `config.json`:

| Setting | Default | Meaning |
|---|---|---|
| `ChatKey` | `F8` | Opens the chat |
| `Chattiness` | `Important` | `Off`, `Important` or `Talkative` |
| `MaxTipsPerDay` | `6` | Proactive tips per game day (warnings do not count) |
| `Warnings` | `true` | Instant fixed-text warnings |
| `TellMeEverything` | `false` | Allow spoilers about content the save has not reached |
| `MorningBriefing` | `true` | A short briefing at the start of each game day (off when `Chattiness` is `Off`) |
| `PauseWhileThinking` | `true` | Single-player: stop the clock while the agent thinks (not while it acts) |
| `Autopilot.ToggleKey` | `F9` | Switches the autopilot off and on |
| `Autopilot.StopWorkAt` | `2330` | Game time when work stops and the farmer goes to bed |
| `Autopilot.MinEnergy` | `10` | Tool use stops before energy drops below this |
| `Autopilot.ConfirmSpendAbove` | `500` | Purchases above this ask first |
| `Autopilot.AlwaysAllow` | all `false` | Standing permissions: `Ship`, `Sell`, `Spend`, `Gift`, `Trash` |
| `Autopilot.FullSelfPlay` | `false` | No confirmations at all (other players' things in co-op still ask) |
| `SelfPlay.ToggleKey` | `F10` | Starts or stops self-play |
| `SelfPlay.DailyRequestBudget` | `120` | Model requests per game day (0 for no limit) |
| `SelfPlay.DailyTokenBudget` | `0` | Model tokens per game day (0 for no limit) |
| `SelfPlay.FollowMyHabits` | `true` | Self-play follows the habits the copilot learned by watching you |
| `Apprentice.Enabled` | `true` | Keep a diary of what you do and learn your habits (files stay in the mod's data folder) |
| `Launcher.Enabled` | `false` | Start the agent together with the game (one-click) |
| `Launcher.UseWrapper` | `true` | Start pi through the wrapper, which serves the settings page. `false` starts pi directly |
| `Launcher.NodeCommand` | `node` | The Node.js executable that runs the wrapper |
| `Launcher.PiCommand` | `pi` | The pi executable |
| `Launcher.Model` | empty | Optional first model, for example `openai/gpt-5`. After that the settings page decides |

## Tools

| Tool | What it returns |
|---|---|
| `get_status` | Date, time, weather, money, energy, location, held item, menu or event state |
| `get_inventory` | Backpack slots with items, quality and prices |
| `get_location` | A map's exits, buildings, NPCs, forage, machines and chests |
| `get_crops` | Crops by type: ready, need water, dead, days to harvest |
| `get_calendar` | Festivals and birthdays this season, tomorrow's weather |
| `get_relationships` | Hearts, talked today, gifts this week for every villager met |
| `get_gift_tastes` | A villager's loved, liked, disliked and hated gifts |
| `get_item_info` | Item prices and categories; seed seasons and growth times |
| `get_skills` | Skill levels, experience, professions |
| `get_quests` | Quest log and special orders |
| `get_bundles` | Community Center bundles, once the farmer can read them |
| `get_shop_stock` | What a shop sells today, with prices |
| `get_fish_info` | Where, when and in what weather a fish bites; which fish a map has and which can bite now |
| `get_crop_plan` | Crops for a season: seed prices, growth, sell price, harvests left, gold per day |
| `get_recipes` | Cooking and crafting recipes: ingredients, known or not, how learned, how many can be made now |
| `get_machine_info` | What a machine accepts and what it makes from an item, and how long it takes |
| `get_collections` | Collections progress and what can still go to the museum |
| `get_npc_schedule` | Where a villager is now and the rest of today's plan |
| `find_items` | Where the player's things are: backpack, chests on every map, fridges, the Junimo chest, ready machine output |
| `get_gift_ideas` | Gifts the player owns that a villager loves or likes, and gifts left today and this week |
| `get_bundle_plan` | What the bundles still need, what the player has, where to get the rest and until when |
| `get_day_briefing` | Today's overview: weather, luck, birthdays, what is ready, last chances, yesterday's result |
| `get_animals` | Farm animals, pets and their buildings: petted, produce ready, feeding spots, hay |
| `get_activity_log` | What the player did on recent days, as the mod watched it (today so far included) |
| `get_habits` | The player's habits with their evidence: usual morning, bedtime, rainy days and weekdays |

The knowledge tools answer from the running game: its data files, and its own functions where it has one (machine output, sell prices, craftable counts, the Collections page). Each result says which parts were computed by the mod instead (for example "possible now" for fish and gold per day for crops). The mod does not read the wiki.

Actions (each returns done, partly done or failed, with a reason):

| Tool | What it does |
|---|---|
| `walk_to` | Walks to a map, a tile or a villager, across maps through doors and map edges |
| `water_crops` | Waters every dry crop, refilling the can at the nearest water |
| `harvest_crops` | Harvests ready crops (hand or scythe) |
| `plant_seeds` | Uses empty soil, or clears and tills new soil near the house, plants, then waters |
| `collect_forage` | Picks up forage on a map |
| `buy_items` | Walks to the shop and buys through the shop menu; respects opening hours |
| `ship_items` | Puts items in the shipping bin (asks first; keeps one of any bundle item) |
| `store_in_chest`, `take_from_chest` | Moves items between the backpack and a chest; takes from fridges and the Junimo chest too, optionally by quality |
| `tend_animals` | Picks up eggs and grabber contents, puts out hay, pets the animals and the pet, milks or shears (not after 7pm) |
| `tend_machines` | Collects ready machine output on a map, and can refill one kind of machine with an item |
| `talk_to`, `give_gift` | Talks to or gifts a villager (gifting asks first) |
| `fish` | Finds open water, casts, strikes and plays the fishing minigame |
| `explore_mines` | Goes down mine floors: takes ladders, breaks rocks to find them, fights monsters next to the farmer; stops at low health or energy |
| `go_to_bed` | Walks home and sleeps |
| `use_tool`, `interact` | Low-level fallbacks on one tile |
| `stop_autopilot` | Stops the current action |

Memory and self-play: `read_memory`, `write_notes`, `write_day_plan`, `write_day_summary`, `set_self_play`.

The bridge speaks JSON-RPC with MCP method names (`initialize`, `tools/list`, `tools/call`), so other agent frameworks can use it too.

## Development

- CI (GitHub Actions, Linux and Windows) runs the agent and wrapper tests, the wrapper against the real pi, and the mod's system-specific code (`mod/Platform.Tests`). The game itself cannot run in CI.
- `dotnet run --project mod/Platform.Tests`: tests the mod's system-specific code (config folder, private files, status file, starting the agent, the bridge listener, `pilot_diagnose`) without the game.
- `node agent/test/ci/wrapper-smoke.ts`: starts the wrapper with the installed pi in a throwaway folder, signs in with a made-up key, and checks that pi starts and stops with it.

- `cd agent && npm install && npm test`: tests the extension offline against recorded bridge messages (`agent/test/fixtures`), and the wrapper's settings server and pi supervisor with fakes.
- `node agent/src/wrapper/main.ts status`: shows the wrapper's state and model.
- `scripts/restart-game.sh`: rebuilds the mod and restarts the game (Linux; starts SMAPI directly, so Steam need not run).
- A release build (`dotnet build mod/StardewPilot -c Release`) also writes an installable zip next to the DLL.
- Dev options in the mod's `config.json`: `Dev.AutoLoadSave` (a save folder name to load at the title screen) and `Dev.FreezeTime`.

## License

MIT. See [LICENSE](LICENSE).
