/**
 * StardewPilot pi extension.
 *
 * Connects to the bridge mod inside the running game, registers the bridge's tools as pi tools,
 * switches off every non-Stardew tool, and replaces pi's coding system prompt.
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
			});
			const init = await c.request<{ gameVersion?: string; serverInfo?: { version?: string } }>("initialize", {
				clientInfo: { name: "stardewpilot-pi", version: "0.1.0" },
			});
			registerBridgeTools(await c.listTools());
			c.onGameEvent((ev) => {
				if (ev.type === "day_started") setStatus("🌱 connected · new day");
			});
			client = c;
			lastError = "";
			setStatus(`🌱 connected · game ${init.gameVersion ?? "?"}`);
			return c;
		} catch (err) {
			lastError = err instanceof Error ? err.message : String(err);
			setStatus("🌱 game not connected");
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

	pi.on("session_start", async (_event, ctx) => {
		currentCtx = ctx;
		lockDownTools();
		await ensureConnected();
	});

	pi.on("session_shutdown", async () => {
		currentCtx = undefined;
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
