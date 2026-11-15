# StardewPilot

An agent-based copilot mod for Stardew Valley. It guides new players and can also control the farmer, either following the player's commands or playing toward a goal the player sets.

It has two parts:

- **Bridge mod** (C#, [SMAPI](https://smapi.io/)): exposes game state and actions over a local WebSocket.
- **Agent** ([pi](https://github.com/earendil-works/pi), TypeScript): runs alongside the game and uses the bridge as its tools.

**Status:** design stage. Nothing is built yet.

See [the design](docs/2026-11-15-agent-copilot-design.md) for the full plan.

## Requirements (planned)

- Stardew Valley 1.6 on PC, with SMAPI 4.5+
- Node.js 22.19+ and pi
- A model provider: a subscription login, an API key, or a local model

## License

MIT. See [LICENSE](LICENSE).
