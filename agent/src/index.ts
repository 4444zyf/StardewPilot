/**
 * StardewPilot pi extension.
 *
 * Connects to the bridge mod inside the running game, registers the bridge's tools as pi tools,
 * switches off every non-Stardew tool, and replaces pi's coding system prompt.
 * It also carries the in-game chat: player messages from the overlay become user messages,
 * proactive tip triggers are worded by the model, and replies go back to the game.
 *
 * Run from the repo root:  pi --no-builtin-tools -e ./agent/src/index.ts
 */
import type { ExtensionAPI, ExtensionContext } from "@earendil-works/pi-coding-agent";
import { BridgeClient, type McpTool, openWebSocket, readConnectionInfo, type Transport } from "./bridge-client.ts";
import { buildSystemPrompt } from "./system-prompt.ts";

const STATUS_KEY = "stardewpilot";

/** Opens the transport to the bridge. Replaced in tests. */
export type Connector = () => Promise<Transport>;

const defaultConnector: Connector = () => openWebSocket(readConnectionInfo());

export default function stardewPilot(pi: ExtensionAPI) {
	createStardewPilot(pi, defaultConnector);
}

export function createStardewPilot(pi: ExtensionAPI, connector: Connector) {
	let client: BridgeClient | undefined;
	let connecting: Promise<BridgeClient | undefined> | undefined;
	let lastError = "";
	const registered = new Set<string>();
	let bridgeTools: McpTool[] = [];
	let currentCtx: ExtensionContext | undefined;
	let settings: BridgeSettings = {};
	let running = false;
	let runKind: "reply" | "tip" = "reply";
	const queuedTips: TipTrigger[] = [];
	let reconnectTimer: ReturnType<typeof setInterval> | undefined;
	let active = false;

	/** Keep trying to reach the game in the background, so in-game chat works after a game restart. */
	function startReconnecting() {
		if (reconnectTimer || !active) return;
		reconnectTimer = setInterval(async () => {
			if (client && !client.isClosed) {
				clearInterval(reconnectTimer);
				reconnectTimer = undefined;
				return;
			}
			await ensureConnected();
		}, 5000);
		reconnectTimer.unref?.();
	}

	function stopReconnecting() {
		if (reconnectTimer) clearInterval(reconnectTimer);
		reconnectTimer = undefined;
	}

	function setStatus(text: string) {
		try {
			currentCtx?.ui.setStatus(STATUS_KEY, text);
		} catch {
			// The session ended or was replaced; its ctx can no longer be used.
			currentCtx = undefined;
		}
	}

	/** Only Stardew tools may be active. */
	function lockDownTools() {
		pi.setActiveTools([...registered]);
	}

	function registerBridgeTools(tools: McpTool[]) {
		bridgeTools = tools;
		for (const tool of tools) {
			if (registered.has(tool.name)) continue;
			registered.add(tool.name);
			pi.registerTool({
				name: tool.name,
				label: tool.name,
				description: tool.description,
				parameters: tool.inputSchema as never,
				annotations: { readOnlyHint: tool.annotations?.readOnlyHint ?? false },
				async execute(_toolCallId, params) {
					const c = await ensureConnected();
					if (!c) throw new Error(`Not connected to the game: ${lastError}`);
					const result = await c.callTool(tool.name, (params ?? {}) as Record<string, unknown>);
					return {
						content: result.content,
						details: undefined,
						isError: result.isError === true,
					};
				},
			});
		}
		lockDownTools();
	}

	async function connect(): Promise<BridgeClient | undefined> {
		try {
			const transport = await connector();
			const c = new BridgeClient(transport);
			transport.onClose(() => {
				if (client === c) client = undefined;
				setStatus("🌱 game disconnected");
				startReconnecting();
			});
			const init = await c.request<{ gameVersion?: string; settings?: BridgeSettings }>("initialize", {
				clientInfo: { name: "stardewpilot-pi", version: "0.1.0" },
			});
			settings = init.settings ?? {};
			registerBridgeTools(await c.listTools());
			c.onGameEvent((ev) => {
				if (ev.type === "day_started") setStatus("🌱 connected · new day");
			});
			c.onNotification((method, params) => handleBridgeNotification(method, params));
			client = c;
			lastError = "";
			setStatus(`🌱 connected · game ${init.gameVersion ?? "?"}`);
			return c;
		} catch (err) {
			lastError = err instanceof Error ? err.message : String(err);
			setStatus("🌱 game not connected");
			startReconnecting();
			return undefined;
		}
	}

	function ensureConnected(): Promise<BridgeClient | undefined> {
		if (client && !client.isClosed) return Promise.resolve(client);
		connecting ??= connect().finally(() => {
			connecting = undefined;
		});
		return connecting;
	}

	/** Messages the game pushes to the agent. */
	function handleBridgeNotification(method: string, params: unknown) {
		const p = (params ?? {}) as Record<string, unknown>;
		if (method === "chat/user_message" && typeof p.text === "string" && p.text.trim()) {
			// The player typed in the private in-game overlay.
			if (running) pi.sendUserMessage(p.text, { deliverAs: "followUp" });
			else {
				runKind = "reply";
				pi.sendUserMessage(p.text);
			}
		} else if (method === "tips/trigger") {
			queuedTips.push(p as unknown as TipTrigger);
			if (!running) startNextTip();
		} else if (method === "tips/warning" && typeof p.text === "string") {
			// Already shown to the player as fixed text; keep it as context for later answers.
			pi.sendMessage(
				{ customType: "stardew-warning", content: `[The game showed the player this warning] ${p.text}`, display: true },
				{ deliverAs: "nextTurn" },
			);
		}
	}

	function startNextTip() {
		const tip = queuedTips.shift();
		if (!tip) return;
		runKind = "tip";
		pi.sendMessage(
			{ customType: "stardew-tip", content: tipPrompt(tip, settings.gameLanguage), display: true },
			{ triggerTurn: true, deliverAs: "followUp" },
		);
	}

	pi.on("agent_start", async () => {
		running = true;
		client?.notify("agent/status", { busy: true });
	});

	pi.on("tool_execution_start", async (event) => {
		client?.notify("agent/status", { busy: true, activity: describeTool(event.toolName) });
	});

	// Send each finished answer to the in-game chat.
	pi.on("message_end", async (event) => {
		const message = event.message as { role?: string; content?: unknown };
		if (message.role !== "assistant" || !Array.isArray(message.content)) return;
		const parts = message.content as Array<{ type: string; text?: string }>;
		if (parts.some((part) => part.type === "toolCall")) return; // a step, not the answer
		const text = parts
			.filter((part) => part.type === "text" && part.text)
			.map((part) => part.text)
			.join("\n")
			.trim();
		if (text) client?.notify("chat/agent_message", { text, kind: runKind });
	});

	pi.on("agent_settled", async () => {
		running = false;
		runKind = "reply";
		client?.notify("agent/status", { busy: false });
		if (queuedTips.length > 0) startNextTip();
	});

	pi.on("session_start", async (_event, ctx) => {
		currentCtx = ctx;
		active = true;
		lockDownTools();
		await ensureConnected();
	});

	pi.on("session_shutdown", async () => {
		currentCtx = undefined;
		active = false;
		stopReconnecting();
		client?.close();
		client = undefined;
	});

	// Belt and braces: refuse any tool that is not ours, even if something re-enabled it.
	pi.on("tool_call", async (event) => {
		if (!registered.has(event.toolName)) {
			return { block: true, reason: `${event.toolName} is not a Stardew tool and is disabled in StardewPilot.` };
		}
	});

	pi.on("before_agent_start", async (event, ctx) => {
		currentCtx = ctx;
		const c = await ensureConnected();
		lockDownTools();
		return {
			systemPrompt: buildSystemPrompt({
				toolNames: c ? bridgeTools.map((t) => t.name) : [],
				connected: !!c,
				gameLanguage: settings.gameLanguage,
				tellMeEverything: settings.tellMeEverything === true,
				extra: c ? undefined : `Connection error: ${lastError}`,
			}),
		};
	});

	pi.registerCommand("stardew", {
		description: "Show or retry the StardewPilot game connection",
		handler: async (_args, ctx) => {
			currentCtx = ctx;
			const c = await ensureConnected();
			if (c) ctx.ui.notify(`Connected to the game. Tools: ${bridgeTools.map((t) => t.name).join(", ")}`, "info");
			else ctx.ui.notify(`Not connected: ${lastError}`, "warning");
		},
	});
}

interface BridgeSettings {
	gameLanguage?: string;
	tellMeEverything?: boolean;
	chattiness?: string;
}

interface TipTrigger {
	kind: string;
	important?: boolean;
	facts?: unknown;
	fallback?: string;
	gameLanguage?: string;
}

/** The instruction the model gets when a rule in the game fires. */
export function tipPrompt(tip: TipTrigger, gameLanguage?: string): string {
	const language = tip.gameLanguage ?? gameLanguage ?? "en";
	return [
		`[Proactive tip from the game: ${tip.kind}]`,
		`Facts: ${JSON.stringify(tip.facts ?? {})}`,
		`Default wording: ${tip.fallback ?? ""}`,
		`Write one short, friendly tip for the player (at most two sentences) in the game's language (${language}).`,
		"You may call a tool if one fact would make the tip clearly more useful; otherwise answer directly. Do not ask questions back.",
	].join("\n");
}

/** Short label for the overlay's "Thinking (...)" line. */
export function describeTool(name: string): string {
	return name.replace(/^get_/, "checking ").replace(/_/g, " ");
}
