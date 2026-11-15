import assert from "node:assert/strict";
import { request } from "node:http";
import { after, before, describe, it } from "node:test";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import type { WrapperSettings } from "../src/wrapper/files.ts";
import { LoginFlows } from "../src/wrapper/login-flows.ts";
import type { CustomProvider, Interaction, ModelInfo, PilotRuntime, ProviderInfo, TestResult } from "../src/wrapper/runtime.ts";
import { PageServer, type StateView } from "../src/wrapper/server.ts";

const SECRET = "sk-very-secret-test-key-123";
const PAGE_DIR = join(dirname(fileURLToPath(import.meta.url)), "..", "src", "wrapper", "page");

/** A fake pi runtime: an API-key provider and an OAuth provider with a browser callback. */
class FakeRuntime implements PilotRuntime {
	readonly piDir = "/tmp/fake-pi";
	stored = new Map<string, string>();
	custom: CustomProvider[] = [];
	/** Resolves the OAuth "callback" from outside, like a browser redirect would. */
	oauthCallback: (() => void) | undefined;

	async providers(): Promise<ProviderInfo[]> {
		return [
			{ id: "keyco", name: "KeyCo", apiKey: true, signedIn: this.stored.has("keyco"), stored: this.stored.has("keyco"), custom: false, modelCount: 1, source: this.stored.has("keyco") ? "stored" : undefined },
			{ id: "subco", name: "SubCo", apiKey: false, oauth: "SubCo (Plus)", signedIn: this.stored.has("subco"), stored: this.stored.has("subco"), custom: false, modelCount: 1 },
			...this.custom.map((c) => ({ id: c.id, name: c.name ?? c.id, apiKey: true, signedIn: true, stored: false, custom: true, modelCount: c.models.length })),
		];
	}

	async models(): Promise<ModelInfo[]> {
		return this.stored.has("keyco") ? [{ provider: "keyco", providerName: "KeyCo", id: "m1", name: "Model 1", reasoning: false }] : [];
	}

	async login(provider: string, type: "api_key" | "oauth", i: Interaction): Promise<void> {
		if (type === "api_key") {
			const key = await i.prompt({ type: "secret", message: "Enter KeyCo API key" });
			this.stored.set(provider, key);
			return;
		}
		i.notify({ type: "auth_url", url: "https://auth.example/authorize?x=1" });
		const withdraw = new AbortController();
		const pasted = i.prompt({ type: "manual_code", message: "Paste the redirect URL", signal: withdraw.signal });
		const callback = new Promise<string>((resolve) => (this.oauthCallback = () => resolve("from-callback")));
		const code = await Promise.race([pasted, callback]);
		withdraw.abort();
		pasted.catch(() => {});
		this.stored.set(provider, `oauth:${code}`);
	}

	async logout(provider: string) {
		this.stored.delete(provider);
	}

	async saveCustomProvider(p: CustomProvider) {
		this.custom.push(p);
	}

	async test(model: string): Promise<TestResult> {
		return { ok: true, seconds: 0.5, toolCall: model.endsWith("m1") };
	}

	async problemWith(model: string) {
		return model === "keyco/m1" && this.stored.has("keyco") ? undefined : "Not signed in to KeyCo.";
	}
}

const TOKEN = "test-token-abcdefghijklmnopqrstuvwxyz";

let server: PageServer;
let port = 0;
let rt: FakeRuntime;
let settings: WrapperSettings;
let restarts = 0;

function call(method: string, path: string, opts: { body?: unknown; token?: string | null; host?: string; origin?: string } = {}): Promise<{ status: number; body: any; raw: string; headers: Record<string, unknown> }> {
	return new Promise((resolve, reject) => {
		const headers: Record<string, string> = { Host: opts.host ?? `127.0.0.1:${port}` };
		if (opts.token !== null) headers["X-Pilot-Token"] = opts.token ?? TOKEN;
		if (opts.origin) headers.Origin = opts.origin;
		const data = opts.body === undefined ? undefined : JSON.stringify(opts.body);
		if (data) headers["Content-Type"] = "application/json";
		const req = request({ host: "127.0.0.1", port, method, path, headers }, (res) => {
			let raw = "";
			res.setEncoding("utf8");
			res.on("data", (c) => (raw += c));
			res.on("end", () => {
				let body: any;
				try {
					body = JSON.parse(raw);
				} catch {
					body = undefined;
				}
				resolve({ status: res.statusCode ?? 0, body, raw, headers: res.headers });
			});
		});
		req.on("error", reject);
		if (data) req.write(data);
		req.end();
	});
}

async function waitFor<T>(fn: () => Promise<T | undefined>, ms = 2000): Promise<T> {
	const end = Date.now() + ms;
	for (;;) {
		const v = await fn();
		if (v !== undefined) return v;
		if (Date.now() > end) throw new Error("timed out");
		await new Promise((r) => setTimeout(r, 20));
	}
}

describe("settings page server", () => {
	before(async () => {
		rt = new FakeRuntime();
		settings = { piFolder: "own", model: "", thinking: "off" };
		const flows = new LoginFlows(() => rt);
		server = new PageServer({
			token: TOKEN,
			pageDir: PAGE_DIR,
			runtime: () => rt,
			flows,
			state: (): StateView => ({ state: "needs_setup", message: "", settings, piDir: rt.piDir, piVersion: "test", recentErrors: [] }),
			saveSettings: async (next) => {
				settings = next;
				restarts++;
			},
			restart: async () => {
				restarts++;
			},
		});
		port = await server.listen(0);
	});

	after(async () => {
		await server.close();
	});

	it("serves the page without the token, with strict headers", async () => {
		const res = await call("GET", "/", { token: null });
		assert.equal(res.status, 200);
		assert.match(res.raw, /Copilot settings/);
		assert.match(String(res.headers["content-security-policy"]), /default-src 'self'/);
		assert.equal(res.headers["cache-control"], "no-store");
	});

	it("refuses API calls without the right token", async () => {
		assert.equal((await call("GET", "/api/state", { token: null })).status, 401);
		assert.equal((await call("GET", "/api/state", { token: "wrong" })).status, 401);
		assert.equal((await call("GET", "/api/state")).status, 200);
	});

	it("refuses other hosts (DNS rebinding) and other origins", async () => {
		assert.equal((await call("GET", "/api/state", { host: "evil.example" })).status, 403);
		assert.equal((await call("GET", "/", { host: `evil.example:${port}` })).status, 403);
		assert.equal((await call("GET", "/api/state", { origin: "https://evil.example" })).status, 403);
		assert.equal((await call("GET", "/api/state", { origin: `http://127.0.0.1:${port}` })).status, 200);
		assert.equal((await call("GET", "/api/state", { host: `localhost:${port}` })).status, 200);
	});

	it("never approves a CORS preflight", async () => {
		const res = await call("OPTIONS", "/api/state", { token: null, origin: "https://evil.example" });
		assert.equal(res.status, 403);
		assert.equal(res.headers["access-control-allow-origin"], undefined);
	});

	it("runs an API-key sign-in and never echoes the key", async () => {
		const start = await call("POST", "/api/login", { body: { provider: "keyco", type: "api_key" } });
		assert.equal(start.status, 200);
		const id = start.body.id as string;
		const prompt = await waitFor(async () => (await call("GET", `/api/login/${id}`)).body.prompts[0]);
		assert.equal(prompt.type, "secret");

		const answered = await call("POST", `/api/login/${id}/answer`, { body: { prompt: prompt.id, value: SECRET } });
		assert.equal(answered.status, 200);
		const done = await waitFor(async () => {
			const v = (await call("GET", `/api/login/${id}`)).body;
			return v.state === "done" ? v : undefined;
		});
		assert.equal(done.state, "done");
		assert.equal(rt.stored.get("keyco"), SECRET);

		for (const path of ["/api/state", "/api/providers", "/api/models", `/api/login/${id}`]) {
			const res = await call("GET", path);
			assert.ok(!res.raw.includes(SECRET), `${path} leaked the key`);
		}
		assert.ok(!answered.raw.includes(SECRET));
	});

	it("rejects an answer to a question that is not open", async () => {
		const start = await call("POST", "/api/login", { body: { provider: "keyco", type: "api_key" } });
		const res = await call("POST", `/api/login/${start.body.id}/answer`, { body: { prompt: "99", value: "x" } });
		assert.equal(res.status, 409);
		await call("POST", `/api/login/${start.body.id}/cancel`, { body: {} });
	});

	it("runs an OAuth sign-in finished by the browser callback, withdrawing the paste field", async () => {
		const start = await call("POST", "/api/login", { body: { provider: "subco", type: "oauth" } });
		const id = start.body.id as string;
		const view = await waitFor(async () => {
			const v = (await call("GET", `/api/login/${id}`)).body;
			return v.prompts.length > 0 ? v : undefined;
		});
		assert.deepEqual(view.events[0], { type: "auth_url", url: "https://auth.example/authorize?x=1" });
		assert.equal(view.prompts[0].type, "manual_code");

		rt.oauthCallback!();
		const done = await waitFor(async () => {
			const v = (await call("GET", `/api/login/${id}`)).body;
			return v.state === "done" ? v : undefined;
		});
		assert.deepEqual(done.prompts, []);
		assert.equal(rt.stored.get("subco"), "oauth:from-callback");
	});

	it("runs an OAuth sign-in finished by a pasted redirect URL", async () => {
		rt.stored.delete("subco");
		const start = await call("POST", "/api/login", { body: { provider: "subco", type: "oauth" } });
		const id = start.body.id as string;
		const prompt = await waitFor(async () => (await call("GET", `/api/login/${id}`)).body.prompts[0]);
		await call("POST", `/api/login/${id}/answer`, { body: { prompt: prompt.id, value: "http://localhost/cb?code=abc" } });
		await waitFor(async () => ((await call("GET", `/api/login/${id}`)).body.state === "done" ? true : undefined));
		assert.equal(rt.stored.get("subco"), "oauth:http://localhost/cb?code=abc");
	});

	it("cancels a sign-in", async () => {
		const start = await call("POST", "/api/login", { body: { provider: "subco", type: "oauth" } });
		const res = await call("POST", `/api/login/${start.body.id}/cancel`, { body: {} });
		assert.equal(res.body.state, "cancelled");
		assert.deepEqual(res.body.prompts, []);
	});

	it("refuses a sign-in type the provider does not offer", async () => {
		assert.equal((await call("POST", "/api/login", { body: { provider: "subco", type: "api_key" } })).status, 400);
		assert.equal((await call("POST", "/api/login", { body: { provider: "nope", type: "api_key" } })).status, 404);
	});

	it("validates custom endpoints", async () => {
		const ok = { id: "ollama", baseUrl: "http://localhost:11434/v1", api: "openai-completions", models: "qwen3:14b, llama3.3" };
		assert.equal((await call("POST", "/api/custom-provider", { body: { ...ok, id: "Bad ID" } })).status, 400);
		assert.equal((await call("POST", "/api/custom-provider", { body: { ...ok, id: "keyco" } })).status, 400);
		assert.equal((await call("POST", "/api/custom-provider", { body: { ...ok, baseUrl: "file:///etc/passwd" } })).status, 400);
		assert.equal((await call("POST", "/api/custom-provider", { body: { ...ok, api: "shell" } })).status, 400);
		assert.equal((await call("POST", "/api/custom-provider", { body: { ...ok, models: " " } })).status, 400);
		const res = await call("POST", "/api/custom-provider", { body: ok });
		assert.equal(res.status, 200);
		assert.deepEqual(rt.custom.at(-1)?.models, ["qwen3:14b", "llama3.3"]);
	});

	it("saves a usable model and refuses an unusable one", async () => {
		const before = restarts;
		assert.equal((await call("POST", "/api/settings", { body: { model: "other/x" } })).status, 400);
		assert.equal((await call("POST", "/api/settings", { body: { thinking: "extreme" } })).status, 400);
		const res = await call("POST", "/api/settings", { body: { model: "keyco/m1", thinking: "low" } });
		assert.equal(res.status, 200);
		assert.deepEqual(settings, { piFolder: "own", model: "keyco/m1", thinking: "low" });
		assert.equal(restarts, before + 1);
	});

	it("tests a model", async () => {
		const res = await call("POST", "/api/test", { body: { model: "keyco/m1" } });
		assert.deepEqual(res.body, { ok: true, seconds: 0.5, toolCall: true });
	});

	it("rejects non-JSON bodies", async () => {
		const res = await new Promise<number>((resolve, reject) => {
			const req = request({ host: "127.0.0.1", port, method: "POST", path: "/api/logout", headers: { Host: `127.0.0.1:${port}`, "X-Pilot-Token": TOKEN, "Content-Type": "text/plain" } }, (r) => {
				r.resume();
				resolve(r.statusCode ?? 0);
			});
			req.on("error", reject);
			req.end("provider=keyco");
		});
		assert.equal(res, 415);
	});
});
