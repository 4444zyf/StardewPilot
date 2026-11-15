# StardewPilot

An agent-based copilot mod for Stardew Valley. It guides new players and can also control the farmer, either following the player's commands or playing toward a goal the player sets.

It has two parts:

- **Bridge mod** (`mod/`, C#, [SMAPI](https://smapi.io/)): exposes game state and actions over a local, token-protected WebSocket.
- **Agent** (`agent/`, a [pi](https://github.com/earendil-works/pi) extension, TypeScript): runs alongside the game and uses the bridge as its only tools.

See [the design](docs/2026-11-15-agent-copilot-design.md) for the full plan.

## Status

Step 1 of the build order (read-only bridge, chat in pi's terminal) works. The agent can read the game but cannot act yet.

## Requirements

- Stardew Valley 1.6 on PC, with SMAPI 4.5+
- .NET 6 SDK or newer (to build the mod)
- Node.js 22.19+ and [pi](https://github.com/earendil-works/pi) 1.5+
- A model provider configured in pi: a subscription login, an API key, or a local model

## Build and run

1. Build the mod. This also copies it into the game's `Mods` folder:

   ```sh
   dotnet build mod/StardewPilot -c Release
   ```

2. Start the game with SMAPI. The SMAPI console shows `Bridge listening on 127.0.0.1:45781`. On first run the mod writes a random token into its `config.json`, and writes the port and token to a connection file readable only by you (`~/.config/StardewPilot/bridge.json`, or `%APPDATA%\StardewPilot\bridge.json` on Windows).

3. Start the agent from the repo root:

   ```sh
   pi --no-builtin-tools -e ./agent/src/index.ts
   ```

   `--no-builtin-tools` keeps pi's shell and file tools off. The extension also enables only the Stardew tools, blocks every other tool call, and replaces pi's coding system prompt. Type `/stardew` to check the game connection.

Ask things like "what should I do today?", "what does Abigail like?" or "which crops need water?".

## Tools (step 1)

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

The bridge speaks JSON-RPC with MCP method names (`initialize`, `tools/list`, `tools/call`), so other agent frameworks can use it too.

## Development

- `cd agent && npm install && npm test`: tests the extension offline against recorded bridge messages (`agent/test/fixtures`).
- `scripts/restart-game.sh`: rebuilds the mod and restarts the game through Steam (Linux).
- Dev options in the mod's `config.json`: `Dev.AutoLoadSave` (a save folder name to load at the title screen) and `Dev.FreezeTime`.

## License

MIT. See [LICENSE](LICENSE).
