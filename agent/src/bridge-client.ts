/**
 * JSON-RPC client for the StardewPilot bridge mod.
 *
 * The transport is pluggable so tests can replay recorded bridge messages without the game.
 */
import { readFileSync } from "node:fs";
import { homedir, platform } from "node:os";
import { join } from "node:path";

export interface McpTool {
	name: string;
	description: string;
	inputSchema: Record<string, unknown>;
	annotations?: { readOnlyHint?: boolean };
}

export interface ToolCallResult {
	content: Array<{ type: "text"; text: string }>;
	isError?: boolean;
}

export interface GameEvent {
	type: string;
	data?: unknown;
}

/** Minimal message transport: a WebSocket in production, a fake in tests. */
export interface Transport {
	send(text: string): void;
	close(): void;
	onMessage(handler: (text: string) => void): void;
	onClose(handler: (reason: string) => void): void;
}

export interface ConnectionInfo {
	url: string;
	token: string;
}

/** Where the mod writes its port and token. Matches ConnectionFile.cs. */
export function connectionFilePath(): string {
	const base =
		platform() === "win32"
			? (process.env.APPDATA ?? join(homedir(), "AppData", "Roaming"))
			: platform() === "darwin"
				? join(homedir(), ".config")
				: (process.env.XDG_CONFIG_HOME ?? join(homedir(), ".config"));
	return join(base, "StardewPilot", "bridge.json");
}

/** Read connection details from env vars or the mod's connection file. */
export function readConnectionInfo(): ConnectionInfo {
	if (process.env.STARDEWPILOT_URL && process.env.STARDEWPILOT_TOKEN) {
		return { url: process.env.STARDEWPILOT_URL, token: process.env.STARDEWPILOT_TOKEN };
	}
	const path = connectionFilePath();
	let raw: string;
	try {
		raw = readFileSync(path, "utf8");
	} catch {
		throw new Error(`Bridge not found: ${path} does not exist. Start Stardew Valley with SMAPI and the StardewPilot mod.`);
	}
	const parsed = JSON.parse(raw) as { url?: string; token?: string };
	if (!parsed.url || !parsed.token) throw new Error(`Bridge connection file ${path} is incomplete.`);
	return { url: parsed.url, token: parsed.token };
}

/** Open a WebSocket transport using Node's built-in WebSocket. Resolves once open. */
export function openWebSocket(info: ConnectionInfo, timeoutMs = 3000): Promise<Transport> {
	return new Promise((resolve, reject) => {
		const url = new URL(info.url);
		url.searchParams.set("token", info.token);
		const ws = new WebSocket(url);
		let settled = false;
		const timer = setTimeout(() => {
			if (settled) return;
			settled = true;
			ws.close();
			reject(new Error(`Timed out connecting to the bridge at ${info.url}.`));
		}, timeoutMs);

		const messageHandlers: Array<(text: string) => void> = [];
		const closeHandlers: Array<(reason: string) => void> = [];
		ws.addEventListener("message", (ev) => {
			const text = typeof ev.data === "string" ? ev.data : Buffer.from(ev.data as ArrayBuffer).toString("utf8");
			for (const h of messageHandlers) h(text);
		});
		ws.addEventListener("close", (ev) => {
			for (const h of closeHandlers) h(ev.reason || `code ${ev.code}`);
			if (!settled) {
				settled = true;
				clearTimeout(timer);
				reject(new Error(`Bridge refused the connection (${ev.code}). Is the token current? Restart the game if the mod was updated.`));
			}
		});
		ws.addEventListener("error", () => {
			// A refused handshake (wrong token, wrong port) reports only an error, without a close event.
			if (settled) return;
			settled = true;
			clearTimeout(timer);
			reject(new Error(`Could not connect to the bridge at ${info.url}. Is the game running, and is the token current?`));
		});
		ws.addEventListener("open", () => {
			if (settled) return;
			settled = true;
			clearTimeout(timer);
			resolve({
				send: (text) => ws.send(text),
				close: () => ws.close(),
				onMessage: (h) => messageHandlers.push(h),
				onClose: (h) => closeHandlers.push(h),
			});
		});
	});
}

interface Pending {
	resolve(value: unknown): void;
	reject(error: Error): void;
	timer: ReturnType<typeof setTimeout>;
}

export class BridgeClient {
	private nextId = 1;
	private readonly pending = new Map<number, Pending>();
	private readonly eventHandlers: Array<(event: GameEvent) => void> = [];
	private readonly notificationHandlers: Array<(method: string, params: unknown) => void> = [];
	private closed = false;
	private readonly transport: Transport;
	private readonly requestTimeoutMs: number;

	constructor(transport: Transport, requestTimeoutMs = 120_000) {
		this.transport = transport;
		this.requestTimeoutMs = requestTimeoutMs;
		transport.onMessage((text) => this.handleMessage(text));
		transport.onClose((reason) => {
			this.closed = true;
			for (const [, p] of this.pending) {
				clearTimeout(p.timer);
				p.reject(new Error(`Bridge connection closed (${reason}).`));
			}
			this.pending.clear();
		});
	}

	get isClosed(): boolean {
		return this.closed;
	}

	onGameEvent(handler: (event: GameEvent) => void): void {
		this.eventHandlers.push(handler);
	}

	/** Any server notification, including game events. */
	onNotification(handler: (method: string, params: unknown) => void): void {
		this.notificationHandlers.push(handler);
	}

	request<T>(method: string, params?: unknown): Promise<T> {
		if (this.closed) return Promise.reject(new Error("Bridge connection is closed."));
		const id = this.nextId++;
		return new Promise<T>((resolve, reject) => {
			const timer = setTimeout(() => {
				this.pending.delete(id);
				reject(new Error(`Bridge did not answer '${method}' within ${this.requestTimeoutMs / 1000}s.`));
			}, this.requestTimeoutMs);
			this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject, timer });
			this.transport.send(JSON.stringify({ jsonrpc: "2.0", id, method, params }));
		});
	}

	/** Fire-and-forget message to the bridge. */
	notify(method: string, params?: unknown): void {
		if (this.closed) return;
		this.transport.send(JSON.stringify({ jsonrpc: "2.0", method, params }));
	}

	async listTools(): Promise<McpTool[]> {
		const result = await this.request<{ tools: McpTool[] }>("tools/list");
		return result.tools;
	}

	callTool(name: string, args: Record<string, unknown>): Promise<ToolCallResult> {
		return this.request<ToolCallResult>("tools/call", { name, arguments: args });
	}

	close(): void {
		this.transport.close();
	}

	private handleMessage(text: string): void {
		let msg: { id?: number; result?: unknown; error?: { message: string }; method?: string; params?: unknown };
		try {
			msg = JSON.parse(text);
		} catch {
			return;
		}
		if (msg.method) {
			for (const h of this.notificationHandlers) h(msg.method, msg.params);
			if (msg.method === "notifications/game_event") {
				for (const h of this.eventHandlers) h(msg.params as GameEvent);
			}
			return;
		}
		if (typeof msg.id !== "number") return;
		const p = this.pending.get(msg.id);
		if (!p) return;
		this.pending.delete(msg.id);
		clearTimeout(p.timer);
		if (msg.error) p.reject(new Error(msg.error.message));
		else p.resolve(msg.result);
	}
}
