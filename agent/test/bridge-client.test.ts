import assert from "node:assert/strict";
import { test } from "node:test";
import { BridgeClient, type GameEvent } from "../src/bridge-client.ts";
import { ReplayTransport, SilentTransport } from "./fake-transport.ts";

test("lists tools in MCP shape", async () => {
	const client = new BridgeClient(new ReplayTransport());
	const tools = await client.listTools();
	assert.ok(tools.length >= 10);
	const status = tools.find((t) => t.name === "get_status");
	assert.ok(status, "get_status is listed");
	assert.equal(status.inputSchema.type, "object");
	assert.equal(status.annotations?.readOnlyHint, true);
});

test("tool calls return text content and carry isError", async () => {
	const client = new BridgeClient(new ReplayTransport());
	const ok = await client.callTool("get_status", {});
	assert.equal(ok.isError, false);
	assert.equal(JSON.parse(ok.content[0].text).saveLoaded, true);

	const bad = await client.callTool("get_gift_tastes", { npc: "Nobody" });
	assert.equal(bad.isError, true);
	assert.match(bad.content[0].text, /No gift data/);
});

test("protocol errors reject the request", async () => {
	const client = new BridgeClient(new ReplayTransport());
	await assert.rejects(client.request("no/such/method"), /Unknown method/);
});

test("game events reach subscribers", async () => {
	const transport = new ReplayTransport();
	const client = new BridgeClient(transport);
	const seen: GameEvent[] = [];
	client.onGameEvent((e) => seen.push(e));
	transport.emit(JSON.stringify({ jsonrpc: "2.0", method: "notifications/game_event", params: { type: "day_started" } }));
	assert.deepEqual(seen, [{ type: "day_started" }]);
});

test("closing the connection rejects pending requests", async () => {
	const transport = new SilentTransport();
	const client = new BridgeClient(transport);
	const pending = client.request("tools/list");
	transport.close();
	await assert.rejects(pending, /closed/);
	assert.equal(client.isClosed, true);
	await assert.rejects(client.request("tools/list"), /closed/);
});

test("requests time out", async () => {
	const client = new BridgeClient(new SilentTransport(), 20);
	await assert.rejects(client.request("tools/list"), /did not answer/);
});
