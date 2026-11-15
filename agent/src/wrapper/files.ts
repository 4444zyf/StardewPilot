/**
 * Where the wrapper keeps its files, and the wrapper settings (no secrets).
 */
import { chmodSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";

/** Same folder the mod uses for bridge.json. */
export function configDir(): string {
	const base = process.platform === "win32"
		? (process.env.APPDATA ?? join(homedir(), "AppData", "Roaming"))
		: (process.env.XDG_CONFIG_HOME ?? join(homedir(), ".config"));
	return join(base, "StardewPilot");
}

export const settingsPath = () => join(configDir(), "agent.json");
export const statusPath = () => join(configDir(), "agent-status.json");
/** pi's config folder when the copilot keeps its own (the default). */
export const ownPiDir = () => join(configDir(), "pi");
/** pi's normal config folder, honouring its own override variable. */
export const sharedPiDir = () => process.env.PI_CODING_AGENT_DIR ?? join(homedir(), ".pi", "agent");

export type PiFolder = "own" | "shared";

export interface WrapperSettings {
	piFolder: PiFolder;
	/** Chosen model as provider/id, empty when none is chosen. */
	model: string;
	thinking: string;
}

export const THINKING_LEVELS = ["off", "minimal", "low", "medium", "high", "xhigh", "max"] as const;

export const DEFAULT_SETTINGS: WrapperSettings = { piFolder: "own", model: "", thinking: "off" };

export function piDirFor(settings: WrapperSettings): string {
	return settings.piFolder === "shared" ? sharedPiDir() : ownPiDir();
}

/** Write a file only its owner can read. Written to a temp file first so readers never see half a file. */
export function writePrivateFile(path: string, content: string): void {
	mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
	const tmp = `${path}.${process.pid}.tmp`;
	writeFileSync(tmp, content, { mode: 0o600 });
	try {
		chmodSync(tmp, 0o600);
	} catch {
		// Windows: modes are not supported; the profile folder is private already.
	}
	renameSync(tmp, path);
}

export function ensurePrivateDir(path: string): void {
	mkdirSync(path, { recursive: true, mode: 0o700 });
	try {
		chmodSync(path, 0o700);
	} catch {
		// Windows
	}
}

export function loadSettings(path = settingsPath()): WrapperSettings {
	if (!existsSync(path)) return { ...DEFAULT_SETTINGS };
	try {
		const raw = JSON.parse(readFileSync(path, "utf8")) as Partial<WrapperSettings>;
		return {
			piFolder: raw.piFolder === "shared" ? "shared" : "own",
			model: typeof raw.model === "string" ? raw.model : "",
			thinking: THINKING_LEVELS.includes(raw.thinking as (typeof THINKING_LEVELS)[number]) ? raw.thinking! : "off",
		};
	} catch {
		return { ...DEFAULT_SETTINGS };
	}
}

export function saveSettings(settings: WrapperSettings, path = settingsPath()): void {
	writePrivateFile(path, JSON.stringify(settings, null, 2) + "\n");
}

/** Split "provider/model-id". Model IDs may themselves contain slashes (OpenRouter). */
export function splitModel(ref: string): { provider: string; id: string } | undefined {
	const i = ref.indexOf("/");
	if (i <= 0 || i === ref.length - 1) return undefined;
	return { provider: ref.slice(0, i), id: ref.slice(i + 1) };
}
