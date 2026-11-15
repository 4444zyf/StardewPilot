/**
 * The settings page server. Localhost only; every API call needs the page token in a custom header,
 * which also forces a CORS preflight that is never approved, so other websites cannot call it.
 */
import { timingSafeEqual } from "node:crypto";
import { readFileSync } from "node:fs";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { join } from "node:path";
import { THINKING_LEVELS, type PiFolder, type WrapperSettings } from "./files.ts";
import type { LoginFlows } from "./login-flows.ts";
import { CUSTOM_APIS, type PilotRuntime } from "./runtime.ts";
import type { AgentState } from "./supervisor.ts";

export interface StateView {
	state: AgentState;
	message: string;
	settings: WrapperSettings;
	piDir: string;
	piVersion: string;
	recentErrors: readonly string[];
}

export interface ServerDeps {
	token: string;
	pageDir: string;
	runtime(): PilotRuntime;
	flows: LoginFlows;
	state(): StateView;
	/** Save settings and restart pi (or report what is missing). */
	saveSettings(next: WrapperSettings): Promise<void>;
	restart(): Promise<void>;
}

const STATIC: Record<string, { file: string; type: string }> = {
	"/": { file: "index.html", type: "text/html; charset=utf-8" },
	"/app.js": { file: "app.js", type: "text/javascript; charset=utf-8" },
	"/style.css": { file: "style.css", type: "text/css; charset=utf-8" },
};

const SECURITY_HEADERS: Record<string, string> = {
	"Cache-Control": "no-store",
	"Content-Security-Policy": "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
	"X-Content-Type-Options": "nosniff",
	"Referrer-Policy": "no-referrer",
	"X-Frame-Options": "DENY",
};

const MAX_BODY = 64 * 1024;

class HttpError extends Error {
	readonly status: number;
	constructor(status: number, message: string) {
		super(message);
		this.status = status;
	}
}

function send(res: ServerResponse, status: number, body: unknown, type = "application/json; charset=utf-8") {
	const data = typeof body === "string" || Buffer.isBuffer(body) ? body : JSON.stringify(body);
	res.writeHead(status, { ...SECURITY_HEADERS, "Content-Type": type });
	res.end(data);
}

async function readJson(req: IncomingMessage): Promise<Record<string, unknown>> {
	if (!(req.headers["content-type"] ?? "").startsWith("application/json")) throw new HttpError(415, "Send JSON.");
	const chunks: Buffer[] = [];
	let size = 0;
	for await (const chunk of req) {
		size += (chunk as Buffer).length;
		if (size > MAX_BODY) throw new HttpError(413, "Request too large.");
		chunks.push(chunk as Buffer);
	}
	if (size === 0) return {};
	try {
		const value = JSON.parse(Buffer.concat(chunks).toString("utf8"));
		if (typeof value !== "object" || value === null || Array.isArray(value)) throw new Error();
		return value as Record<string, unknown>;
	} catch {
		throw new HttpError(400, "Invalid JSON.");
	}
}

function str(body: Record<string, unknown>, key: string, max = 2000): string {
	const v = body[key];
	if (typeof v !== "string") throw new HttpError(400, `Missing ${key}.`);
	if (v.length > max) throw new HttpError(400, `${key} is too long.`);
	return v;
}

function optStr(body: Record<string, unknown>, key: string, max = 2000): string | undefined {
	return body[key] === undefined || body[key] === "" ? undefined : str(body, key, max);
}

function tokenMatches(given: string | string[] | undefined, expected: string): boolean {
	if (typeof given !== "string") return false;
	const a = Buffer.from(given);
	const b = Buffer.from(expected);
	return a.length === b.length && timingSafeEqual(a, b);
}

export class PageServer {
	private readonly deps: ServerDeps;
	private readonly server: Server;
	private port = 0;
	private readonly tests = new Set<AbortController>();

	constructor(deps: ServerDeps) {
		this.deps = deps;
		this.server = createServer((req, res) => {
			this.handle(req, res).catch((err: unknown) => {
				const status = err instanceof HttpError ? err.status : 500;
				const message = err instanceof Error ? err.message : String(err);
				if (!res.headersSent) send(res, status, { error: message });
				else res.end();
			});
		});
	}

	/** Listen on the preferred port, or any free one if it is taken. */
	async listen(preferred: number): Promise<number> {
		const tryPort = (port: number) =>
			new Promise<number>((resolve, reject) => {
				const onError = (err: Error) => {
					this.server.off("listening", onListening);
					reject(err);
				};
				const onListening = () => {
					this.server.off("error", onError);
					resolve((this.server.address() as AddressInfo).port);
				};
				this.server.once("error", onError);
				this.server.once("listening", onListening);
				this.server.listen(port, "127.0.0.1");
			});
		try {
			this.port = await tryPort(preferred);
		} catch (err) {
			if ((err as NodeJS.ErrnoException).code !== "EADDRINUSE" || preferred === 0) throw err;
			this.port = await tryPort(0);
		}
		return this.port;
	}

	get url(): string {
		return `http://127.0.0.1:${this.port}/#t=${this.deps.token}`;
	}

	async close() {
		for (const t of this.tests) t.abort();
		this.server.closeAllConnections();
		await new Promise<void>((resolve) => this.server.close(() => resolve()));
	}

	private async handle(req: IncomingMessage, res: ServerResponse) {
		// DNS rebinding: only answer requests addressed to this server by its local name.
		const host = req.headers.host ?? "";
		if (host !== `127.0.0.1:${this.port}` && host !== `localhost:${this.port}`) throw new HttpError(403, "Wrong host.");
		const origin = req.headers.origin;
		if (origin !== undefined && origin !== `http://127.0.0.1:${this.port}` && origin !== `http://localhost:${this.port}`)
			throw new HttpError(403, "Cross-origin requests are not allowed.");

		const url = new URL(req.url ?? "/", "http://localhost");
		const path = url.pathname;

		if (req.method === "GET" && STATIC[path]) {
			const { file, type } = STATIC[path];
			send(res, 200, readFileSync(join(this.deps.pageDir, file)), type);
			return;
		}
		if (!path.startsWith("/api/")) throw new HttpError(404, "Not found.");
		if (!tokenMatches(req.headers["x-pilot-token"], this.deps.token)) throw new HttpError(401, "Open the settings page from the game (type /settings in the copilot chat), or from the link the wrapper printed.");

		const result = await this.api(req, path);
		send(res, 200, result ?? {});
	}

	private async api(req: IncomingMessage, path: string): Promise<unknown> {
		const rt = this.deps.runtime();
		const flows = this.deps.flows;
		const get = req.method === "GET";
		const post = req.method === "POST";
		const body = post ? await readJson(req) : {};

		if (get && path === "/api/state") return this.deps.state();
		if (get && path === "/api/providers") return { providers: await rt.providers(), customApis: CUSTOM_APIS };
		if (get && path === "/api/models") return { models: await rt.models(), thinkingLevels: THINKING_LEVELS };

		if (post && path === "/api/login") {
			const provider = str(body, "provider", 100);
			const type = str(body, "type", 20);
			if (type !== "api_key" && type !== "oauth") throw new HttpError(400, "type must be api_key or oauth.");
			const info = (await rt.providers()).find((p) => p.id === provider);
			if (!info) throw new HttpError(404, `Unknown provider ${provider}.`);
			if (type === "oauth" ? !info.oauth : !info.apiKey) throw new HttpError(400, `${info.name} has no ${type === "oauth" ? "subscription sign-in" : "API key sign-in"}.`);
			return flows.start(provider, type);
		}
		const flowMatch = /^\/api\/login\/([0-9a-f]{16})(\/answer|\/cancel)?$/.exec(path);
		if (flowMatch) {
			const [, id, action] = flowMatch;
			if (get && !action) {
				const view = flows.get(id);
				if (!view) throw new HttpError(404, "This sign-in has ended. Start again.");
				return view;
			}
			if (post && action === "/answer") {
				if (!flows.answer(id, str(body, "prompt", 20), str(body, "value", 8000)))
					throw new HttpError(409, "That question is no longer open.");
				return flows.get(id);
			}
			if (post && action === "/cancel") {
				flows.cancel(id);
				return flows.get(id);
			}
		}

		if (post && path === "/api/logout") {
			await rt.logout(str(body, "provider", 100));
			return {};
		}

		if (post && path === "/api/custom-provider") {
			const id = str(body, "id", 41).trim();
			if (!/^[a-z0-9][a-z0-9-]{0,40}$/.test(id)) throw new HttpError(400, "The ID may use lower-case letters, digits and dashes.");
			const builtIn = (await rt.providers()).find((p) => p.id === id && !p.custom);
			if (builtIn) throw new HttpError(400, `${id} is a built-in provider. Choose another ID.`);
			const baseUrl = str(body, "baseUrl", 500).trim();
			let parsed: URL;
			try {
				parsed = new URL(baseUrl);
			} catch {
				throw new HttpError(400, "The base URL is not a valid URL.");
			}
			if (parsed.protocol !== "http:" && parsed.protocol !== "https:") throw new HttpError(400, "The base URL must start with http:// or https://.");
			const api = str(body, "api", 40);
			if (!(CUSTOM_APIS as readonly string[]).includes(api)) throw new HttpError(400, `Unknown API type ${api}.`);
			const models = str(body, "models", 2000).split(/[\s,]+/).map((m) => m.trim()).filter(Boolean);
			if (models.length === 0) throw new HttpError(400, "List at least one model ID.");
			await rt.saveCustomProvider({ id, name: optStr(body, "name", 100), baseUrl, api, models, apiKey: optStr(body, "apiKey", 8000) });
			return {};
		}

		if (post && path === "/api/test") {
			const controller = new AbortController();
			this.tests.add(controller);
			const timer = setTimeout(() => controller.abort(), 90_000);
			try {
				return await rt.test(str(body, "model", 300), controller.signal);
			} finally {
				clearTimeout(timer);
				this.tests.delete(controller);
			}
		}

		if (post && path === "/api/settings") {
			const current = this.deps.state().settings;
			const piFolder = (optStr(body, "piFolder", 10) ?? current.piFolder) as PiFolder;
			if (piFolder !== "own" && piFolder !== "shared") throw new HttpError(400, "piFolder must be own or shared.");
			const thinking = optStr(body, "thinking", 10) ?? current.thinking;
			if (!(THINKING_LEVELS as readonly string[]).includes(thinking)) throw new HttpError(400, `Unknown thinking level ${thinking}.`);
			const model = body.model === undefined ? current.model : str(body, "model", 300);
			if (model && piFolder === current.piFolder) {
				const problem = await rt.problemWith(model);
				if (problem) throw new HttpError(400, problem);
			}
			await this.deps.saveSettings({ piFolder, model, thinking });
			return this.deps.state();
		}

		if (post && path === "/api/restart") {
			await this.deps.restart();
			return this.deps.state();
		}

		throw new HttpError(404, "Unknown API call.");
	}
}
