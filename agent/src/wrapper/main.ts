#!/usr/bin/env node
/**
 * stardewpilot-agent: runs pi for the game and serves the copilot settings page.
 *
 *   node agent/src/wrapper/main.ts run     start pi when a model is set up; serve the page (the mod runs this)
 *   node agent/src/wrapper/main.ts setup   only serve the page and open it in the browser
 *   node agent/src/wrapper/main.ts status  print the running wrapper's state
 *
 * Options: --pi <command>  --model <provider/id>  --port <n>  --extension <path>  --no-open
 */
import { randomBytes } from "node:crypto";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { loadSettings, piDirFor, saveSettings, statusPath, type WrapperSettings, writePrivateFile } from "./files.ts";
import { LoginFlows } from "./login-flows.ts";
import { locatePi, type PiInstall } from "./pi-locate.ts";
import { loadPiModule, PiRuntime } from "./runtime.ts";
import { PageServer, type StateView } from "./server.ts";
import { Supervisor } from "./supervisor.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
/** One port above the bridge's default. */
const DEFAULT_PORT = 45782;

interface Args {
	command: "run" | "setup" | "status";
	pi: string;
	model: string;
	port: number;
	extension: string;
	open: boolean;
}

function parseArgs(argv: string[]): Args {
	const args: Args = { command: "run", pi: "pi", model: "", port: DEFAULT_PORT, extension: resolve(HERE, "..", "index.ts"), open: true };
	for (let i = 0; i < argv.length; i++) {
		const a = argv[i];
		const value = () => {
			const v = argv[++i];
			if (v === undefined) throw new Error(`${a} needs a value`);
			return v;
		};
		if (a === "run" || a === "setup" || a === "status") args.command = a;
		else if (a === "--pi") args.pi = value();
		else if (a === "--model") args.model = value();
		else if (a === "--port") args.port = Number(value());
		else if (a === "--extension") args.extension = resolve(value());
		else if (a === "--no-open") args.open = false;
		else throw new Error(`Unknown argument ${a}`);
	}
	return args;
}

export function openBrowser(url: string) {
	const [cmd, ...cmdArgs] = process.platform === "darwin" ? ["open", url]
		: process.platform === "win32" ? ["cmd", "/c", "start", "", url]
		: ["xdg-open", url];
	try {
		spawn(cmd, cmdArgs, { detached: true, stdio: "ignore" }).on("error", () => {}).unref();
	} catch {
		// no browser opener; the URL is printed
	}
}

class Wrapper {
	private settings: WrapperSettings;
	private runtime!: PiRuntime;
	private readonly flows: LoginFlows;
	private readonly supervisor: Supervisor;
	private server!: PageServer;
	private readonly token = randomBytes(32).toString("base64url");
	private readonly args: Args;
	private readonly pi: PiInstall;
	private readonly piModule: Awaited<ReturnType<typeof loadPiModule>>;
	private applying: Promise<void> = Promise.resolve();

	private constructor(args: Args, pi: PiInstall, piModule: Awaited<ReturnType<typeof loadPiModule>>) {
		this.args = args;
		this.pi = pi;
		this.piModule = piModule;
		this.settings = loadSettings();
		if (!this.settings.model && args.model) this.settings.model = args.model; // seed from the mod's config
		this.supervisor = new Supervisor(() => this.writeStatus());
		this.flows = new LoginFlows(() => this.runtime, () => {
			// A new sign-in may be what was missing.
			if (this.args.command === "run" && this.supervisor.state === "needs_setup") void this.apply();
		});
	}

	static async create(args: Args): Promise<Wrapper> {
		const pi = locatePi(args.pi);
		const w = new Wrapper(args, pi, await loadPiModule(pi.packageDir));
		w.runtime = await PiRuntime.create(w.piModule, piDirFor(w.settings));
		w.server = new PageServer({
			token: w.token,
			pageDir: join(HERE, "page"),
			runtime: () => w.runtime,
			flows: w.flows,
			state: () => w.state(),
			saveSettings: (next) => w.save(next),
			restart: () => w.apply(),
		});
		await w.server.listen(args.port);
		return w;
	}

	get url() {
		return this.server.url;
	}

	private state(): StateView {
		return {
			state: this.args.command === "setup" ? "stopped" : this.supervisor.state,
			message: this.args.command === "setup"
				? "Setup only. The copilot starts with the game."
				: this.supervisor.message,
			settings: this.settings,
			piDir: this.runtime.piDir,
			piVersion: this.pi.version,
			recentErrors: this.supervisor.recentErrors.slice(-10),
		};
	}

	private async save(next: WrapperSettings) {
		const folderChanged = next.piFolder !== this.settings.piFolder;
		this.settings = next;
		saveSettings(next);
		if (folderChanged) {
			this.flows.cancelAll();
			this.runtime = await PiRuntime.create(this.piModule, piDirFor(next));
		}
		await this.apply();
	}

	/** Start pi if the settings allow it, otherwise say what is missing. Serialised. */
	apply(): Promise<void> {
		this.applying = this.applying.then(async () => {
			if (this.args.command !== "run") return;
			const problem = await this.runtime.problemWith(this.settings.model);
			if (problem) {
				await this.supervisor.needsSetup(problem);
				return;
			}
			if (!existsSync(this.args.extension)) {
				await this.supervisor.needsSetup(`The StardewPilot pi extension is missing at ${this.args.extension}.`);
				return;
			}
			await this.supervisor.start({
				entry: this.pi.entry,
				extension: this.args.extension,
				piDir: this.runtime.piDir,
				model: this.settings.model,
				thinking: this.settings.thinking,
				cwd: dirname(this.args.extension),
			});
		}).catch((err: unknown) => {
			console.error(`apply failed: ${err instanceof Error ? err.message : String(err)}`);
		});
		return this.applying;
	}

	writeStatus(stopped = false) {
		if (this.args.command !== "run") return;
		const status = {
			state: stopped ? "stopped" : this.supervisor.state,
			message: stopped ? "" : this.supervisor.message,
			model: this.settings.model,
			settingsUrl: stopped ? "" : this.url,
			pid: process.pid,
			piPid: stopped ? undefined : this.supervisor.pid,
			updatedAt: new Date().toISOString(),
		};
		try {
			writePrivateFile(statusPath(), JSON.stringify(status, null, 2) + "\n");
		} catch (err) {
			console.error(`Could not write ${statusPath()}: ${err instanceof Error ? err.message : String(err)}`);
		}
	}

	async shutdown() {
		this.flows.cancelAll();
		await this.supervisor.stop();
		this.writeStatus(true);
		await this.runtime.settle();
		await this.server.close();
	}
}

async function main() {
	const args = parseArgs(process.argv.slice(2));

	if (args.command === "status") {
		const path = statusPath();
		if (!existsSync(path)) {
			console.log("No wrapper has run yet.");
			return;
		}
		const s = JSON.parse(readFileSync(path, "utf8"));
		console.log(`${s.state}: ${s.message}\nmodel: ${s.model || "(none)"}\nsettings page: ${s.settingsUrl || "(not running)"}`);
		return;
	}

	const wrapper = await Wrapper.create(args);
	// In run mode our stderr ends up in the SMAPI log, which players share; the link with its key stays in the private status file.
	console.error(args.command === "setup" ? `StardewPilot settings page: ${wrapper.url}` : `Settings page ready; link in ${statusPath()}`);
	let stopping = false;
	const stop = async () => {
		if (stopping) return;
		stopping = true;
		await wrapper.shutdown();
		process.exit(0);
	};
	process.on("SIGINT", stop);
	process.on("SIGTERM", stop);

	if (args.command === "setup") {
		if (args.open) openBrowser(wrapper.url);
		console.error("Press Ctrl+C when you are done.");
		return;
	}

	// run: the game holds our stdin open and closes it when it exits.
	wrapper.writeStatus();
	process.stdin.on("end", stop);
	process.stdin.on("error", stop);
	process.stdin.resume();
	await wrapper.apply();
}

main().catch((err: unknown) => {
	console.error(err instanceof Error ? err.message : String(err));
	process.exit(1);
});
