/**
 * Runs pi in RPC mode with the StardewPilot extension, restarts it when it dies, and stops it on request.
 */
import { type ChildProcess, spawn } from "node:child_process";

export type AgentState = "needs_setup" | "starting" | "running" | "error" | "stopped";

export interface PiLaunch {
	command: string;
	extension: string;
	piDir: string;
	model: string;
	thinking: string;
	cwd: string;
}

/** Waits before restart number n (1-based). */
const BACKOFF_MS = [2000, 5000, 15000, 60000];
/** A run shorter than this counts as a fast failure. */
const FAST_FAIL_MS = 30_000;
const MAX_FAST_FAILS = 5;
/** pi counts as running once it has stayed up this long. */
const STARTUP_MS = 3000;

export class Supervisor {
	state: AgentState = "stopped";
	message = "";
	private child: ChildProcess | undefined;
	private launch: PiLaunch | undefined;
	private restartTimer: ReturnType<typeof setTimeout> | undefined;
	private runningTimer: ReturnType<typeof setTimeout> | undefined;
	private fastFails = 0;
	private startedAt = 0;
	private readonly errors: string[] = [];
	private readonly onChange: () => void;
	private readonly timing: { backoff: number[]; fastFailMs: number; startupMs: number };

	constructor(onChange: () => void, timing?: Partial<{ backoff: number[]; fastFailMs: number; startupMs: number }>) {
		this.onChange = onChange;
		this.timing = { backoff: BACKOFF_MS, fastFailMs: FAST_FAIL_MS, startupMs: STARTUP_MS, ...timing };
	}

	/** Recent stderr lines from pi, newest last. */
	get recentErrors(): readonly string[] {
		return this.errors;
	}

	get pid(): number | undefined {
		return this.child?.pid;
	}

	private set(state: AgentState, message = "") {
		this.state = state;
		this.message = message;
		this.onChange();
	}

	/** Mark that pi cannot start yet (no model or no sign-in). Stops a running pi. */
	async needsSetup(reason: string) {
		await this.stop();
		this.set("needs_setup", reason);
	}

	/** Start pi, or restart it with new settings. */
	async start(launch: PiLaunch) {
		await this.stop();
		this.launch = launch;
		this.fastFails = 0;
		this.spawnPi();
	}

	private spawnPi() {
		const l = this.launch;
		if (!l) return;
		const args = ["--mode", "rpc", "--no-builtin-tools", "-e", l.extension, "--model", l.model];
		if (l.thinking && l.thinking !== "off") args.push("--thinking", l.thinking);
		this.set("starting", `Starting pi with ${l.model}`);
		this.startedAt = Date.now();
		let child: ChildProcess;
		try {
			child = spawn(l.command, args, {
				cwd: l.cwd,
				env: { ...process.env, PI_CODING_AGENT_DIR: l.piDir },
				stdio: ["pipe", "pipe", "pipe"],
				windowsHide: true,
			});
		} catch (err) {
			this.set("error", `Could not start pi: ${err instanceof Error ? err.message : String(err)}`);
			return;
		}
		this.child = child;
		child.stdout?.resume(); // RPC events are not needed; drain so pi never blocks on a full pipe
		let partial = "";
		child.stderr?.setEncoding("utf8");
		child.stderr?.on("data", (chunk: string) => {
			const lines = (partial + chunk).split("\n");
			partial = lines.pop() ?? "";
			for (const line of lines) this.addError(line);
		});
		child.on("error", (err) => this.addError(`pi: ${err.message}`));
		child.on("exit", (code, signal) => {
			if (this.child !== child) return; // an old pi we stopped on purpose
			this.child = undefined;
			clearTimeout(this.runningTimer);
			this.onExit(code, signal);
		});
		this.runningTimer = setTimeout(() => {
			if (this.child === child) this.set("running", `Using ${l.model}`);
		}, this.timing.startupMs);
	}

	private addError(line: string) {
		const text = line.trim();
		if (!text) return;
		this.errors.push(text.slice(0, 500));
		if (this.errors.length > 30) this.errors.shift();
	}

	private onExit(code: number | null, signal: NodeJS.Signals | null) {
		const ran = Date.now() - this.startedAt;
		this.fastFails = ran < this.timing.fastFailMs ? this.fastFails + 1 : 0;
		const last = this.errors.at(-1) ?? "";
		const how = signal ? `signal ${signal}` : `code ${code}`;
		if (this.fastFails >= MAX_FAST_FAILS) {
			this.set("error", `pi keeps stopping (${how}). ${last}`.trim());
			return;
		}
		const wait = this.timing.backoff[Math.min(Math.max(0, this.fastFails - 1), this.timing.backoff.length - 1)];
		this.set("starting", `pi stopped (${how}); restarting in ${Math.round(wait / 1000)} s. ${last}`.trim());
		this.restartTimer = setTimeout(() => {
			this.restartTimer = undefined;
			this.spawnPi();
		}, wait);
	}

	/** Stop pi: close its stdin (it exits on its own), then kill it after a grace period. */
	async stop() {
		clearTimeout(this.restartTimer);
		clearTimeout(this.runningTimer);
		this.restartTimer = undefined;
		const child = this.child;
		this.child = undefined;
		if (!child || child.exitCode !== null) return;
		await new Promise<void>((resolve) => {
			const timer = setTimeout(() => {
				child.kill("SIGKILL");
				resolve();
			}, 3000);
			child.once("exit", () => {
				clearTimeout(timer);
				resolve();
			});
			child.stdin?.end();
		});
	}
}
