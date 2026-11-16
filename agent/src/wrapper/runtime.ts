/**
 * The wrapper's view of pi's model runtime: providers, sign-in, models and a test request.
 * The server talks to this interface only, so tests can use a fake.
 */
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import type { ModelRuntime } from "@earendil-works/pi-coding-agent";
import { ensurePrivateDir, splitModel, writePrivateFile } from "./files.ts";

export interface AuthPromptOut {
	type: "text" | "secret" | "select" | "manual_code";
	message: string;
	placeholder?: string;
	options?: readonly { id: string; label: string; description?: string }[];
	signal?: AbortSignal;
}

export type AuthEventOut =
	| { type: "info"; message: string; links?: readonly { url: string; label?: string }[] }
	| { type: "auth_url"; url: string; instructions?: string }
	| { type: "device_code"; userCode: string; verificationUri: string; expiresInSeconds?: number }
	| { type: "progress"; message: string };

export interface Interaction {
	signal: AbortSignal;
	prompt(p: AuthPromptOut): Promise<string>;
	notify(e: AuthEventOut): void;
}

export interface ProviderInfo {
	id: string;
	name: string;
	/** Can sign in with an API key (has an interactive key setup). */
	apiKey: boolean;
	/** Name of the subscription / OAuth sign-in, if the provider has one. */
	oauth?: string;
	signedIn: boolean;
	/** Where the sign-in comes from (stored, environment, models.json ...). */
	source?: string;
	/** A credential stored in auth.json, which Sign out can remove. */
	stored: boolean;
	/** Defined in models.json by the player. */
	custom: boolean;
	modelCount: number;
}

export interface ModelInfo {
	provider: string;
	providerName: string;
	id: string;
	name: string;
	reasoning: boolean;
	contextWindow?: number;
}

export interface CustomProvider {
	id: string;
	name?: string;
	baseUrl: string;
	api: string;
	models: string[];
	apiKey?: string;
}

export interface TestResult {
	ok: boolean;
	seconds: number;
	toolCall: boolean;
	reply?: string;
	error?: string;
}

export interface PilotRuntime {
	readonly piDir: string;
	providers(): Promise<ProviderInfo[]>;
	models(): Promise<ModelInfo[]>;
	login(provider: string, type: "api_key" | "oauth", interaction: Interaction): Promise<void>;
	logout(provider: string): Promise<void>;
	saveCustomProvider(p: CustomProvider): Promise<void>;
	test(model: string, signal: AbortSignal): Promise<TestResult>;
	/** Why the model cannot be used now, or undefined when it can. */
	problemWith(model: string): Promise<string | undefined>;
}

export const CUSTOM_APIS = ["openai-completions", "openai-responses", "anthropic-messages"] as const;

/** The wrapper's identity in sign-in flows that ask for one. */
const AGENT_NAME = "StardewPilot";

/**
 * Subscription sign-ins the copilot must not offer or use. Anthropic's terms allow Claude Free/Pro/Max sign-in only
 * in Claude Code and Claude.ai; using it in another tool breaks the Consumer Terms and can get the account banned.
 * Claude stays available with an Anthropic API key.
 */
export const BLOCKED_SUBSCRIPTIONS: ReadonlyMap<string, string> = new Map([
	["anthropic", "Anthropic's terms do not allow a Claude Pro/Max subscription in other apps, and the account can be banned. Sign out of Anthropic and add an Anthropic API key instead."],
]);

/** Environment variables that carry a blocked subscription token; removed so neither the wrapper nor pi uses them. */
export const BLOCKED_SUBSCRIPTION_ENV = ["ANTHROPIC_OAUTH_TOKEN"] as const;

type PiModule = { ModelRuntime: typeof ModelRuntime };

export async function loadPiModule(packageDir: string): Promise<PiModule> {
	return (await import(pathToFileURL(join(packageDir, "dist", "index.js")).href)) as PiModule;
}

export class PiRuntime implements PilotRuntime {
	readonly piDir: string;
	private rt: ModelRuntime;
	private readonly pi: PiModule;

	private constructor(pi: PiModule, piDir: string, rt: ModelRuntime) {
		this.pi = pi;
		this.piDir = piDir;
		this.rt = rt;
	}

	static async create(pi: PiModule, piDir: string): Promise<PiRuntime> {
		return new PiRuntime(pi, piDir, await PiRuntime.open(pi, piDir));
	}

	private static async open(pi: PiModule, piDir: string): Promise<ModelRuntime> {
		ensurePrivateDir(piDir);
		return pi.ModelRuntime.create({
			authPath: join(piDir, "auth.json"),
			modelsPath: join(piDir, "models.json"),
			modelsStorePath: join(piDir, "models-store.json"),
		});
	}

	private get modelsPath() {
		return join(this.piDir, "models.json");
	}

	private readModelsJson(): { providers?: Record<string, Record<string, unknown>> } & Record<string, unknown> {
		if (!existsSync(this.modelsPath)) return {};
		return JSON.parse(readFileSync(this.modelsPath, "utf8"));
	}

	private customIds(): Set<string> {
		try {
			return new Set(Object.keys(this.readModelsJson().providers ?? {}));
		} catch {
			return new Set();
		}
	}

	async providers(): Promise<ProviderInfo[]> {
		const stored = new Set((await this.rt.listCredentials()).map((c) => c.providerId));
		const custom = this.customIds();
		const out: ProviderInfo[] = [];
		for (const p of this.rt.getProviders()) {
			const status = this.rt.getProviderAuthStatus(p.id) as { configured: boolean; source?: string };
			out.push({
				id: p.id,
				name: p.name,
				apiKey: !!p.auth.apiKey?.login,
				oauth: BLOCKED_SUBSCRIPTIONS.has(p.id) ? undefined : p.auth.oauth?.name,
				signedIn: status.configured,
				source: status.source,
				stored: stored.has(p.id),
				custom: custom.has(p.id),
				modelCount: p.getModels().length,
			});
		}
		return out.sort((a, b) => a.name.localeCompare(b.name));
	}

	async models(): Promise<ModelInfo[]> {
		const names = new Map(this.rt.getProviders().map((p) => [p.id, p.name]));
		return (await this.rt.getAvailable()).map((m) => ({
			provider: m.provider,
			providerName: names.get(m.provider) ?? m.provider,
			id: m.id,
			name: m.name ?? m.id,
			reasoning: !!m.reasoning,
			contextWindow: m.contextWindow,
		}));
	}

	async login(provider: string, type: "api_key" | "oauth", interaction: Interaction): Promise<void> {
		const blocked = type === "oauth" ? BLOCKED_SUBSCRIPTIONS.get(provider) : undefined;
		if (blocked) throw new Error(blocked);
		await this.rt.login(provider, type, interaction, { agentName: AGENT_NAME });
	}

	async logout(provider: string): Promise<void> {
		await this.rt.logout(provider);
	}

	async saveCustomProvider(p: CustomProvider): Promise<void> {
		const data = this.readModelsJson();
		data.providers ??= {};
		data.providers[p.id] = {
			...(p.name ? { name: p.name } : {}),
			baseUrl: p.baseUrl,
			api: p.api,
			// A keyless local server still needs some key for pi to count the provider as usable.
			...(p.apiKey ? {} : { apiKey: "local" }),
			models: p.models.map((id) => ({ id })),
		};
		writePrivateFile(this.modelsPath, JSON.stringify(data, null, 2) + "\n");
		this.rt = await PiRuntime.open(this.pi, this.piDir);
		if (p.apiKey) {
			const key = p.apiKey;
			const controller = new AbortController();
			await this.rt.login(p.id, "api_key", {
				signal: controller.signal,
				prompt: async (q) => {
					if (q.type === "secret") return key;
					throw new Error(`Unexpected question from the custom provider setup: ${q.message}`);
				},
				notify: () => {},
			});
		}
	}

	async test(model: string, signal: AbortSignal): Promise<TestResult> {
		const ref = splitModel(model);
		const m = ref && this.rt.getModel(ref.provider, ref.id);
		if (!m) return { ok: false, seconds: 0, toolCall: false, error: `Unknown model ${model}` };
		const started = Date.now();
		try {
			const reply = await this.rt.completeSimple(m, {
				systemPrompt: "You are testing a connection. Follow the instruction exactly.",
				messages: [{ role: "user", content: "Call the ping tool with word set to \"hello\".", timestamp: Date.now() }],
				tools: [{
					name: "ping",
					description: "Connection check. Call it when asked.",
					parameters: { type: "object", properties: { word: { type: "string" } }, required: ["word"] },
				}],
			}, { signal });
			const seconds = (Date.now() - started) / 1000;
			if (reply.stopReason === "error" || reply.stopReason === "aborted")
				return { ok: false, seconds, toolCall: false, error: reply.errorMessage ?? reply.stopReason };
			const toolCall = reply.content.some((c) => c.type === "toolCall" && c.name === "ping");
			const text = reply.content.filter((c) => c.type === "text").map((c) => (c as { text: string }).text).join(" ").trim();
			return { ok: true, seconds, toolCall, reply: text.slice(0, 200) || undefined };
		} catch (err) {
			return { ok: false, seconds: (Date.now() - started) / 1000, toolCall: false, error: err instanceof Error ? err.message : String(err) };
		}
	}

	/** Wait for credential writes in flight (an OAuth refresh), so a rotated token is not lost on exit. */
	async settle(): Promise<void> {
		await this.rt.settleCredentialWrites();
	}

	async problemWith(model: string): Promise<string | undefined> {
		if (!model) return "No model chosen yet.";
		const ref = splitModel(model);
		if (!ref) return `"${model}" is not a provider/model reference.`;
		const provider = this.rt.getProvider(ref.provider);
		if (!provider) return `Unknown provider ${ref.provider}.`;
		if (!this.rt.getModel(ref.provider, ref.id)) return `${provider.name} has no model ${ref.id}.`;
		const blocked = BLOCKED_SUBSCRIPTIONS.get(ref.provider);
		if (blocked && (await this.rt.listCredentials()).some((c) => c.providerId === ref.provider && c.type === "oauth")) return blocked;
		if (!this.rt.hasConfiguredAuth(ref.provider)) {
			const check = await this.rt.checkAuth(ref.provider).catch(() => undefined);
			if (!check) return `Not signed in to ${provider.name}.`;
		}
		return undefined;
	}
}
