import assert from "node:assert/strict";
import { test } from "node:test";
import { createStardewPilot } from "../src/index.ts";
import { ReplayTransport } from "./fake-transport.ts";

/** Minimal stand-in for pi's ExtensionAPI: records what the extension registers. */
function fakePi() {
	const handlers = new Map<string, Array<(event: any, ctx: any) => any>>();
	const tools = new Map<string, any>();
	let active: string[] = ["read", "bash", "edit", "write"]; // pretend pi's coding tools were on
	const statuses: string[] = [];
	const ctx = { ui: { setStatus: (_k: string, t: string) => statuses.push(t), notify: () => {} } };
	const api = {
		on(name: string, h: (event: any, ctx: any) => any) {
			handlers.set(name, [...(handlers.get(name) ?? []), h]);
		},
		registerTool(def: any) {
			tools.set(def.name, def);
		},
		registerCommand() {},
		setActiveTools(names: string[]) {
			active = names;
		},
	};
	async function fire(name: string, event: any = {}) {
		let last: any;
		for (const h of handlers.get(name) ?? []) last = await h(event, ctx);
		return last;
	}
	return { api, tools, fire, statuses, get active() { return active; } };
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
