/**
 * End-to-end check of the wrapper against the real, installed pi. Run by CI on Windows and Linux:
 *
 *   node test/ci/wrapper-smoke.ts
 *
 * It uses a throwaway config folder whose path has a space and non-ASCII letters, signs in with a made-up key,
 * chooses a model, waits for pi to run (with an empty extension, so no game and no model request is needed),
 * then closes the wrapper's stdin like the game does when it quits, and checks that pi and the wrapper are gone.
 */
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const here = fileURLToPath(new URL(".", import.meta.url));
const main = join(here, "..", "..", "src", "wrapper", "main.ts");
const base = mkdtempSync(join(tmpdir(), "sp smoke Ünïcode "));
const ext = join(base, "empty-extension.ts");
writeFileSync(ext, "export default function () {}\n");

// The wrapper keeps its files under the platform config folder; point that at the throwaway folder.
const env = { ...process.env, XDG_CONFIG_HOME: base, APPDATA: base };
const statusFile = join(base, "StardewPilot", "agent-status.json");
const log = (msg: string) => console.log(`[smoke] ${msg}`);

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
async function until<T>(what: string, fn: () => T | undefined | Promise<T | undefined>, ms = 30_000): Promise<T> {
	const end = Date.now() + ms;
	for (;;) {
		const v = await fn();
		if (v !== undefined && v !== false) return v as T;
		if (Date.now() > end) throw new Error(`timed out waiting for ${what}`);
		await sleep(200);
	}
}
const readStatus = () => {
	try {
		return JSON.parse(readFileSync(statusFile, "utf8"));
	} catch {
		return undefined;
	}
};
function isAlive(pid: number) {
	try {
		process.kill(pid, 0);
		return true;
	} catch {
		return false;
	}
}

log(`config folder: ${base}`);
const wrapper = spawn(process.execPath, [main, "run", "--port", "0", "--extension", ext], { env, stdio: ["pipe", "inherit", "inherit"] });
let wrapperExit: number | null | undefined;
wrapper.on("exit", (code) => (wrapperExit = code));

try {
	const first = await until("the status file", () => readStatus()?.settingsUrl ? readStatus() : undefined);
	assert.equal(first.state, "needs_setup", `expected needs_setup, got ${first.state}: ${first.message}`);
	const url = new URL(first.settingsUrl);
	const token = url.hash.replace(/^#t=/, "");
	const origin = `${url.protocol}//${url.host}`;
	const api = async (path: string, body?: unknown) => {
		const res = await fetch(`${origin}/api/${path}`, {
			method: body === undefined ? "GET" : "POST",
			headers: { "X-Pilot-Token": token, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
			body: body === undefined ? undefined : JSON.stringify(body),
		});
		const data = await res.json();
		if (!res.ok) throw new Error(`${path}: ${res.status} ${JSON.stringify(data)}`);
		return data;
	};
	log("wrapper is up and needs setup");

	// The page refuses calls without the token.
	assert.equal((await fetch(`${origin}/api/state`)).status, 401);

	// Sign in with a made-up key, the way the page does.
	const flow = await api("login", { provider: "qwen-token-plan-cn", type: "api_key" });
	const prompt = await until("the key question", async () => (await api(`login/${flow.id}`)).prompts[0]);
	await api(`login/${flow.id}/answer`, { prompt: prompt.id, value: "sk-made-up-key-for-ci" });
	await until("sign-in to finish", async () => (await api(`login/${flow.id}`)).state === "done");
	const auth = join(base, "StardewPilot", "pi", "auth.json");
	assert.ok(existsSync(auth), "pi's auth.json should be in the copilot's own pi folder");
	log("signed in; credentials are in the copilot's pi folder");

	// Choose a model; the wrapper starts pi.
	await api("settings", { model: "qwen-token-plan-cn/qwen3.8-flash" });
	const running = await until("pi to run", () => (readStatus()?.state === "running" && readStatus()?.piPid ? readStatus() : undefined), 60_000);
	const piPid = running.piPid as number;
	assert.ok(isAlive(piPid), "pi should be running");
	log(`pi is running (pid ${piPid}): ${running.message}`);
	const state = await api("state");
	assert.equal(state.state, "running");

	// Close stdin, as the game does when it exits. The wrapper must stop pi and exit by itself.
	wrapper.stdin!.end();
	await until("the wrapper to exit", () => wrapperExit !== undefined, 15_000);
	assert.equal(wrapperExit, 0);
	const final = readStatus();
	assert.equal(final.state, "stopped");
	assert.equal(final.settingsUrl, "", "the status file must not keep the page link after exit");
	log("wrapper exited cleanly after stdin closed");

	// No pi may be left behind.
	await until("pi to be gone", () => !isAlive(piPid), 5_000);
	assert.ok(!isAlive(wrapper.pid!), "wrapper process still alive");
	log("smoke test passed");
} catch (err) {
	console.error(err);
	if (wrapperExit === undefined) wrapper.kill();
	process.exitCode = 1;
}
