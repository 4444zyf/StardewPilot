/**
 * Runs pi sign-in flows for the settings page. pi asks questions through prompt() and reports
 * links and codes through notify(); the page polls a flow and answers its open questions.
 * Answers (keys included) go straight to pi and are never stored here.
 */
import { randomBytes } from "node:crypto";
import type { AuthEventOut, AuthPromptOut, PilotRuntime } from "./runtime.ts";

export interface OpenPrompt {
	id: string;
	type: AuthPromptOut["type"];
	message: string;
	placeholder?: string;
	options?: readonly { id: string; label: string; description?: string }[];
}

export interface FlowView {
	id: string;
	provider: string;
	type: "api_key" | "oauth";
	state: "running" | "done" | "error" | "cancelled";
	events: AuthEventOut[];
	prompts: OpenPrompt[];
	error?: string;
}

interface Pending {
	view: OpenPrompt;
	resolve: (value: string) => void;
	reject: (err: Error) => void;
}

class Flow {
	readonly id = randomBytes(8).toString("hex");
	readonly controller = new AbortController();
	state: FlowView["state"] = "running";
	events: AuthEventOut[] = [];
	pending = new Map<string, Pending>();
	error?: string;
	finishedAt = 0;
	private nextPrompt = 1;
	readonly provider: string;
	readonly type: "api_key" | "oauth";

	constructor(provider: string, type: "api_key" | "oauth") {
		this.provider = provider;
		this.type = type;
	}

	prompt(p: AuthPromptOut): Promise<string> {
		if (this.state !== "running") return Promise.reject(new Error("Sign-in was cancelled."));
		const id = String(this.nextPrompt++);
		return new Promise<string>((resolve, reject) => {
			const view: OpenPrompt = { id, type: p.type, message: p.message, placeholder: p.placeholder, options: p.options };
			this.pending.set(id, { view, resolve, reject });
			// pi withdraws a question when something else answers the step (an OAuth callback beats a pasted code).
			p.signal?.addEventListener("abort", () => {
				if (this.pending.delete(id)) reject(new Error("Question withdrawn."));
			}, { once: true });
		});
	}

	notify(e: AuthEventOut): void {
		// Keep only what the page shows; drop anything unexpected.
		switch (e.type) {
			case "info": this.events.push({ type: "info", message: e.message, links: e.links }); break;
			case "auth_url": this.events.push({ type: "auth_url", url: e.url, instructions: e.instructions }); break;
			case "device_code": this.events.push({ type: "device_code", userCode: e.userCode, verificationUri: e.verificationUri, expiresInSeconds: e.expiresInSeconds }); break;
			case "progress": this.events.push({ type: "progress", message: e.message }); break;
		}
		if (this.events.length > 50) this.events.splice(0, this.events.length - 50);
	}

	finish(state: FlowView["state"], error?: string) {
		if (this.state !== "running") return;
		this.state = state;
		this.error = error;
		this.finishedAt = Date.now();
		for (const p of this.pending.values()) p.reject(new Error("Sign-in ended."));
		this.pending.clear();
	}

	view(): FlowView {
		return {
			id: this.id,
			provider: this.provider,
			type: this.type,
			state: this.state,
			events: this.events,
			prompts: [...this.pending.values()].map((p) => p.view),
			error: this.error,
		};
	}
}

/** Flows are dropped this long after they end, or after this long if abandoned. */
const KEEP_MS = 10 * 60 * 1000;

export class LoginFlows {
	private readonly flows = new Map<string, Flow>();
	private readonly runtime: () => PilotRuntime;
	private readonly onChange: () => void;

	constructor(runtime: () => PilotRuntime, onChange: () => void = () => {}) {
		this.runtime = runtime;
		this.onChange = onChange;
	}

	start(provider: string, type: "api_key" | "oauth"): FlowView {
		this.prune();
		// One sign-in at a time per provider: a new one replaces an abandoned dialog.
		for (const f of this.flows.values()) {
			if (f.provider === provider && f.state === "running") this.cancel(f.id);
		}
		const flow = new Flow(provider, type);
		this.flows.set(flow.id, flow);
		this.runtime()
			.login(provider, type, {
				signal: flow.controller.signal,
				prompt: (p) => flow.prompt(p),
				notify: (e) => flow.notify(e),
			})
			.then(
				() => {
					flow.finish("done");
					this.onChange();
				},
				(err: unknown) => {
					if (flow.state === "running") flow.finish("error", err instanceof Error ? err.message : String(err));
				},
			);
		return flow.view();
	}

	get(id: string): FlowView | undefined {
		return this.flows.get(id)?.view();
	}

	answer(id: string, promptId: string, value: string): boolean {
		const flow = this.flows.get(id);
		const pending = flow?.pending.get(promptId);
		if (!flow || !pending) return false;
		flow.pending.delete(promptId);
		pending.resolve(value);
		return true;
	}

	cancel(id: string): boolean {
		const flow = this.flows.get(id);
		if (!flow) return false;
		flow.controller.abort();
		flow.finish("cancelled");
		return true;
	}

	cancelAll() {
		for (const id of this.flows.keys()) this.cancel(id);
	}

	private prune() {
		const now = Date.now();
		for (const [id, f] of this.flows) {
			const age = f.finishedAt ? now - f.finishedAt : 0;
			if (age > KEEP_MS || (f.state === "running" && this.flows.size > 20)) {
				this.cancel(id);
				this.flows.delete(id);
			}
		}
	}
}
