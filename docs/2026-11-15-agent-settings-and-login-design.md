# Agent settings page and pi wrapper: design

Date: 2026-11-15
Status: built and tested on 2026-11-15 (API-key sign-in, model test, pi supervision, in-game `/settings`); subscription sign-in needs a real login by the owner

## Problem

Today a player must install pi, run `/login` or edit pi's `models.json` in a terminal, then start the game with `Launcher.Enabled`. If the model is missing or its sign-in has expired, pi starts and fails silently, and the game only says "Agent not connected". That is fine for development and not for a public release.

## What we build

1. **A wrapper** (`stardewpilot-agent`, Node, part of `agent/`) that the mod starts instead of pi. It runs pi, and it serves the settings page.
2. **A settings page** in the player's browser, served by the wrapper on localhost. It handles sign-in, API keys, custom endpoints and the model choice.
3. **Small mod changes**: start the wrapper, show its status in the chat overlay, and open the settings page from the game.

The design rule from the main design still holds: **the mod never handles credentials.** Keys and sign-ins go from the browser to the wrapper to pi's own credential store. The game only sees a status line and a link.

## Why a browser page, not an in-game menu

- Sign-in to subscriptions (ChatGPT, GitHub Copilot) is OAuth in a browser anyway; the page is already where the player is.
- Pasting a long key into a Stardew text box is awkward; a browser handles paste, password managers and IMEs.
- The page works before the game is started (`stardewpilot-agent setup`) and when the agent is not running at all, which is exactly when the player needs it.

## Verified facts (pi 1.5.2, probed 2026-11-15)

- pi's `ModelRuntime` (exported by `@earendil-works/pi-coding-agent`) exposes `getProviders()`, `getProviderAuthStatus(id)`, `checkAuth(id)`, `getAvailable(id)`, `listCredentials()`, `login(id, "api_key" | "oauth", interaction)` and `logout(id)`.
- `login` drives any provider's flow through two callbacks: `prompt()` (types `text`, `secret`, `select`, `manual_code`) and `notify()` (events `info`, `auth_url`, `device_code`, `progress`). A web form can render all of them. Checked with a throwaway auth file: Qwen Token Plan CN asks for one secret; Cloudflare asks for a secret and an account ID.
- 43 built-in providers. Subscription sign-ins include **OpenAI Codex (ChatGPT Plus/Pro)**, Anthropic (Claude Pro/Max; removed, see below), GitHub Copilot, xAI, Kimi, OpenRouter. This settles the design's open question: a ChatGPT subscription is supported, as provider `openai-codex`. A real sign-in by the owner is still needed to confirm it end to end.
- Qwen Token Plan CN is a built-in provider (`qwen-token-plan-cn`), so our test setup no longer needs a hand-written `models.json` entry.
- `PI_CODING_AGENT_DIR` moves pi's whole config folder (auth, models, settings, sessions).

## Wrapper

Entry: `agent/src/wrapper/main.ts`, run with Node 22.19+ (type stripping, no build step, like the extension). It finds pi from `--pi` (default: `pi` on `PATH`), resolves the package folder from the binary, and imports `ModelRuntime` from it. No second copy of pi is shipped.

Commands:

- `run` (what the mod starts): serve the page, start pi when a usable model is set, supervise it, exit when stdin closes (the game quit).
- `setup`: serve the page only and open the browser. For setting up before the first game start.
- `status`: print the current state and exit.

**pi config folder.** By default the wrapper gives pi its own folder, `~/.config/StardewPilot/pi/` (mode 700), through `PI_CODING_AGENT_DIR`. The copilot's sign-ins and sessions then stay apart from any coding use of pi on the same machine, and game sessions do not fill the player's pi history. An advanced switch on the page, "Use my own pi setup (~/.pi/agent)", shares the normal folder instead, for players who already configured pi.

**Wrapper settings** live in `~/.config/StardewPilot/agent.json` (mode 600) and hold no secrets: which pi folder to use, the chosen provider and model, and the thinking level.

**Supervising pi.** The wrapper starts `pi --mode rpc --no-builtin-tools -e <extension> --model <provider/model>` with the chosen pi folder. It reads pi's stderr into a short ring buffer for the page. If pi exits on its own, it is restarted with backoff (2 s, then 5 s, 15 s, 60 s); after five fast failures in a row it stops and reports an error. Saving a new model restarts pi. When the wrapper's stdin closes, it closes pi's stdin, waits 3 s, then kills it.

**States**, reported on the page and to the game:

| State | Meaning |
|---|---|
| `needs_setup` | No model chosen, or the chosen provider has no usable sign-in |
| `starting` | pi is being started |
| `running` | pi is running |
| `error` | pi keeps failing; the message says why (last stderr line) |

**Status file.** The wrapper writes `~/.config/StardewPilot/agent-status.json` (mode 600): state, a one-line message, the model, and the settings page URL. The mod reads it; this needs no new protocol, and it works even when pi (and so the bridge connection) is down. The URL carries the page token, which is why the file is private.

## Settings page

Served at `http://127.0.0.1:45782/` (the next port up from the bridge; any free port if taken). Plain HTML, CSS and JavaScript, no framework, no build, no external requests.

Layout, top to bottom:

1. **Status bar.** State badge, the model in use, a Restart button, and pi's last error if any.
2. **Sign in.** Three groups:
   - *Subscriptions*: one button per OAuth provider (ChatGPT Plus/Pro, GitHub Copilot, ...). Claude Pro/Max is left out on purpose: since 2026-02-19 Anthropic's Consumer Terms say Free/Pro/Max sign-in may be used only in Claude Code and Claude.ai, and other tools may not offer it. The wrapper hides the button, refuses that sign-in, will not start pi with a stored Claude subscription credential (the player is told to sign out and add an API key), and drops `ANTHROPIC_OAUTH_TOKEN` from the environment. Claude remains available with an Anthropic API key.
   - *API key*: a searchable provider list with an "Add key" button.
   - *Custom or local endpoint*: name, base URL, API type (OpenAI-compatible by default), optional key, model IDs. For Ollama, llama.cpp, or any compatible service. Written to the pi folder's `models.json`; the key, if any, goes to `auth.json` like any other.
   - Below the groups: providers that are signed in, with where the sign-in comes from (stored, environment variable) and a Sign out button for stored ones.
3. **Model.** A list of models from signed-in providers only, the thinking level, a **Test** button, and **Save and restart**. Test sends one short request with one dummy tool and reports the time taken and whether the model made the tool call. The copilot depends on tool calls, so a model that cannot make them gets a visible warning.
4. **Advanced.** "Use my own pi setup" switch; where files are kept.

**The sign-in dialog** renders the login flow step by step: a field for each prompt (a password field for secrets, a list for selections), an "Open sign-in page" button for `auth_url`, the code and link for `device_code`, and progress lines. For OAuth it also shows the "paste the final URL here" field pi offers, for machines where the browser's callback cannot reach the wrapper. Cancel aborts the flow.

The page is in English first. Strings sit in one table so they can be translated later.

### Page API

All under `/api/`, JSON:

| Method and path | Purpose |
|---|---|
| `GET /api/state` | wrapper state, model, settings, recent pi errors |
| `GET /api/providers` | providers with name, auth methods, sign-in status and source |
| `GET /api/models` | models of signed-in providers |
| `POST /api/login` | start a flow: `{provider, type}`, returns a flow ID |
| `GET /api/login/:id` | flow events so far, the open prompt if any, done or error |
| `POST /api/login/:id/answer` | answer the open prompt |
| `POST /api/login/:id/cancel` | abort the flow |
| `POST /api/logout` | `{provider}` |
| `POST /api/custom-provider` | add or replace a custom endpoint |
| `POST /api/test` | test a model |
| `POST /api/settings` | save model, thinking level, pi folder choice; restarts pi |
| `POST /api/restart` | restart pi |

The page polls the flow once a second while a dialog is open. Polling keeps the server simple, and sign-in is rare.

### Security

The page can add credentials and start network requests that cost money, so it must only answer the player's own browser tab:

- Listens on 127.0.0.1 only.
- A random 32-byte token is created at each wrapper start. The URL carries it in the fragment (`#t=...`), which browsers never send to servers or put in Referer headers. The page moves it to `sessionStorage` and removes it from the address bar.
- Every API request must carry the token in an `X-Pilot-Token` header. A custom header forces a CORS preflight, which the server never approves, so other websites cannot call the API.
- The `Host` header must be `127.0.0.1:<port>` or `localhost:<port>` (blocks DNS rebinding). A request with any other `Origin` is refused.
- Responses never contain a key or token, not even masked. Secrets are passed straight to pi's login and not kept or logged.
- Headers: `Cache-Control: no-store`, a strict Content-Security-Policy (`default-src 'self'`, `frame-ancestors 'none'`), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`.
- Links to outside sign-in pages open in a new tab with `rel="noopener noreferrer"`.

## Mod changes

- `Launcher` config: the launcher now starts the wrapper (`NodeCommand`, default `node`, running `agent/src/wrapper/main.ts run`). `PiCommand` and `Model` are passed on as `--pi`/`--model`; `--model` only seeds the page's choice when none is saved. `Launcher.UseWrapper = false` keeps the old direct pi start for development.
- The chat overlay's status line uses the wrapper's state when pi is not connected, for example "Copilot needs setup: type /settings". Typing `/settings` in the chat opens the settings page in the browser (`xdg-open`, `open` or `start`, by platform). An SMAPI console command `pilot_settings` does the same.
- When a save loads and the state is `needs_setup`, one toast says so.

## Testing

- Wrapper unit tests (Node test runner, no network): the HTTP server with a fake runtime covers token, Host and Origin checks, a full API-key flow, an OAuth flow with `auth_url` and `manual_code`, cancel, and that no response contains a secret.
- Supervisor tests with a fake pi script: start, restart with backoff, stop on stdin close.
- By hand on the VM: set up the Qwen Token Plan CN key through the page in the separate pi folder, choose `qwen3.8-max`, run Test, start the game with the launcher, chat in game.
- By the owner: ChatGPT Plus/Pro sign-in through the page.

## Out of scope

- Game-side settings (chattiness, budgets, hotkeys) stay in SMAPI's `config.json`. A Generic Mod Config Menu page can come later.
- Usage and cost dashboards.
- Remote access to the page from another machine.
