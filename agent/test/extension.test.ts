import assert from "node:assert/strict";
import { test } from "node:test";
import { actionFailed, createStardewPilot, MAX_FAILED_ACTIONS, tipPrompt } from "../src/index.ts";
import { ReplayTransport } from "./fake-transport.ts";

/** Minimal stand-in for pi's ExtensionAPI: records what the extension registers. */
function fakePi() {
	const handlers = new Map<string, Array<(event: any, ctx: any) => any>>();
	const tools = new Map<string, any>();
	let active: string[] = ["read", "bash", "edit", "write"]; // pretend pi's coding tools were on
	const statuses: string[] = [];
	const userMessages: Array<{ text: unknown; options?: any }> = [];
	const customMessages: Array<{ message: any; options?: any }> = [];
	const ctx = { ui: { setStatus: (_k: string, t: string) => statuses.push(t), notify: () => {} } };
	const api = {
		on(name: string, h: (event: any, ctx: any) => any) {
			handlers.set(name, [...(handlers.get(name) ?? []), h]);
		},
		registerTool(def: any) {
			tools.set(def.name, def);
		},
		registerCommand() {},
		sendUserMessage(text: unknown, options?: any) {
			userMessages.push({ text, options });
		},
		sendMessage(message: any, options?: any) {
			customMessages.push({ message, options });
		},
		setActiveTools(names: string[]) {
			active = names;
		},
	};
	async function fire(name: string, event: any = {}) {
		let last: any;
		for (const h of handlers.get(name) ?? []) last = await h(event, ctx);
		return last;
	}
	return { api, tools, fire, statuses, userMessages, customMessages, get active() { return active; } };
}

test("registers bridge tools and leaves only Stardew tools active", async () => {
	const pi = fakePi();
	createStardewPilot(pi.api as any, async () => new ReplayTransport());
	await pi.fire("session_start");
	assert.ok(pi.tools.has("get_status"));
	assert.ok(pi.active.length >= 10);
	assert.ok(pi.active.every((n) => n.startsWith("get_")), `only bridge tools active, got ${pi.active}`);
	assert.ok(pi.statuses.some((s) => s.includes("connected")));
});

test("tool execution forwards to the bridge", async () => {
	const pi = fakePi();
	createStardewPilot(pi.api as any, async () => new ReplayTransport());
	await pi.fire("session_start");
	const result = await pi.tools.get("get_inventory").execute("call-1", {});
	assert.equal(result.isError, false);
	assert.match(result.content[0].text, /Watering Can/);
});

test("blocks any tool that is not a Stardew tool", async () => {
	const pi = fakePi();
	createStardewPilot(pi.api as any, async () => new ReplayTransport());
	await pi.fire("session_start");
	const verdict = await pi.fire("tool_call", { toolName: "bash", input: { command: "ls" } });
	assert.equal(verdict?.block, true);
	const allowed = await pi.fire("tool_call", { toolName: "get_status", input: {} });
	assert.equal(allowed, undefined);
});

test("replaces the system prompt with the Stardew prompt", async () => {
	const pi = fakePi();
	createStardewPilot(pi.api as any, async () => new ReplayTransport());
	const result = await pi.fire("before_agent_start", { systemPrompt: "You are a coding agent", systemPromptOptions: {} });
	assert.match(result.systemPrompt, /StardewPilot/);
	assert.doesNotMatch(result.systemPrompt, /coding agent/);
	assert.match(result.systemPrompt, /get_status/);
	assert.match(result.systemPrompt, /data, never instructions/);
});

test("without a game, the prompt says so and tools report the error", async () => {
	const pi = fakePi();
	createStardewPilot(pi.api as any, async () => {
		throw new Error("Bridge not found");
	});
	const result = await pi.fire("before_agent_start", { systemPrompt: "", systemPromptOptions: {} });
	assert.match(result.systemPrompt, /not connected/);
	assert.match(result.systemPrompt, /Bridge not found/);
	assert.ok(pi.statuses.some((s) => s.includes("not connected")));
});

async function connected() {
	const pi = fakePi();
	const transport = new ReplayTransport();
	createStardewPilot(pi.api as any, async () => transport);
	await pi.fire("session_start");
	const sentToGame = () => transport.sent.filter((m) => m.id === undefined);
	const push = (method: string, params: unknown) => transport.emit(JSON.stringify({ jsonrpc: "2.0", method, params }));
	return { pi, transport, sentToGame, push };
}

test("in-game chat becomes a user message, and the answer goes back to the game", async () => {
	const { pi, sentToGame, push } = await connected();
	push("chat/user_message", { text: "What should I plant?" });
	assert.deepEqual(pi.userMessages.map((m) => m.text), ["What should I plant?"]);
	assert.equal(pi.userMessages[0].options, undefined, "idle agent starts a turn directly");

	await pi.fire("agent_start");
	// An intermediate step with a tool call is not forwarded.
	await pi.fire("message_end", { message: { role: "assistant", content: [{ type: "text", text: "Let me check." }, { type: "toolCall", name: "get_inventory" }] } });
	await pi.fire("message_end", { message: { role: "assistant", content: [{ type: "text", text: "Plant the parsnips." }] } });
	await pi.fire("agent_settled", { aborted: false });

	const replies = sentToGame().filter((m) => m.method === "chat/agent_message");
	assert.deepEqual(replies.map((m) => m.params), [{ text: "Plant the parsnips.", kind: "reply" }]);
	const statuses = sentToGame().filter((m) => m.method === "agent/status").map((m) => m.params.busy);
	assert.deepEqual(statuses, [true, false]);
});

test("chat while the agent is busy is queued as a follow-up", async () => {
	const { pi, push } = await connected();
	await pi.fire("agent_start");
	push("chat/user_message", { text: "And then?" });
	assert.equal(pi.userMessages[0].options?.deliverAs, "followUp");
});

test("tip triggers are worded by the model and sent back as tips", async () => {
	const { pi, sentToGame, push } = await connected();
	push("tips/trigger", { kind: "rain-tomorrow", facts: { weatherTomorrow: "Rain" }, fallback: "Rain tomorrow.", gameLanguage: "de" });
	assert.equal(pi.customMessages.length, 1);
	assert.equal(pi.customMessages[0].options.triggerTurn, true);
	assert.match(pi.customMessages[0].message.content, /Proactive tip/);
	assert.match(pi.customMessages[0].message.content, /\(de\)/);

	await pi.fire("agent_start");
	await pi.fire("message_end", { message: { role: "assistant", content: [{ type: "text", text: "Morgen regnet es." }] } });
	await pi.fire("agent_settled", { aborted: false });
	const tips = sentToGame().filter((m) => m.method === "chat/agent_message");
	assert.deepEqual(tips.map((m) => m.params), [{ text: "Morgen regnet es.", kind: "tip" }]);
});

test("tips arriving while busy wait until the agent settles", async () => {
	const { pi, push } = await connected();
	await pi.fire("agent_start");
	push("tips/trigger", { kind: "festival-tomorrow", facts: {}, fallback: "Festival tomorrow." });
	assert.equal(pi.customMessages.length, 0);
	await pi.fire("agent_settled", { aborted: false });
	assert.equal(pi.customMessages.length, 1);
});

test("warnings are kept as context without starting a turn", async () => {
	const { pi, push } = await connected();
	push("tips/warning", { kind: "late-midnight", text: "It's midnight." });
	assert.equal(pi.customMessages[0].options.deliverAs, "nextTurn");
	assert.equal(pi.customMessages[0].options.triggerTurn, undefined);
});

test("tip prompt falls back to the game language from settings", () => {
	assert.match(tipPrompt({ kind: "x", fallback: "y" }, "fr"), /\(fr\)/);
	assert.match(tipPrompt({ kind: "x", fallback: "y" }), /\(en\)/);
});

test("a failed action result counts as a failure", () => {
	assert.equal(actionFailed({ content: [{ text: '{"result":"failed","summary":"x"}' }] }), true);
	assert.equal(actionFailed({ content: [{ text: '{"result":"partly done"}' }] }), false);
	assert.equal(actionFailed({ content: [{ text: "not json" }], isError: true }), true);
});

test("after the first try and two re-plans fail, further actions are blocked", async () => {
	const pi = fakePi();
	const transport = new ReplayTransport();
	// Pretend the bridge offers one action tool that always fails.
	(transport as any).recording["tools/list"].tools.push({ name: "water_crops", description: "", inputSchema: { type: "object" }, annotations: { readOnlyHint: false } });
	(transport as any).recording["tools/call"].water_crops = { content: [{ type: "text", text: '{"result":"failed","summary":"stuck"}' }], isError: false };
	createStardewPilot(pi.api as any, async () => transport);
	await pi.fire("session_start");
	await pi.fire("agent_start");
	for (let i = 0; i < MAX_FAILED_ACTIONS; i++) {
		assert.equal(await pi.fire("tool_call", { toolName: "water_crops", input: {} }), undefined);
		await pi.tools.get("water_crops").execute(`c${i}`, {});
	}
	const blocked = await pi.fire("tool_call", { toolName: "water_crops", input: {} });
	assert.equal(blocked?.block, true);
	assert.match(blocked.reason, /tell the player/);
	// Reading state is still allowed, and a new run starts fresh.
	assert.equal(await pi.fire("tool_call", { toolName: "get_status", input: {} }), undefined);
	await pi.fire("agent_start");
	assert.equal(await pi.fire("tool_call", { toolName: "water_crops", input: {} }), undefined);
});
