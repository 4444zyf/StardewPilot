/**
 * Find the installed pi: its command and its package folder (to import ModelRuntime from it).
 * We use the player's pi rather than shipping a second copy.
 */
import { existsSync, readFileSync, realpathSync } from "node:fs";
import { delimiter, dirname, isAbsolute, join } from "node:path";

export interface PiInstall {
	/** The pi command that was found (on Windows a .cmd shim, which cannot be started directly). */
	command: string;
	/** pi's JavaScript entry file, started with node. */
	entry: string;
	/** Folder of the @earendil-works/pi-coding-agent package. */
	packageDir: string;
	version: string;
}

const PACKAGE_NAME = "@earendil-works/pi-coding-agent";

function which(command: string): string | undefined {
	if (isAbsolute(command) || command.includes("/") || command.includes("\\")) return existsSync(command) ? command : undefined;
	const exts = process.platform === "win32" ? (process.env.PATHEXT ?? ".EXE;.CMD").split(";") : [""];
	for (const dir of (process.env.PATH ?? "").split(delimiter)) {
		for (const ext of exts) {
			const candidate = join(dir, command + ext);
			if (dir && existsSync(candidate)) return candidate;
		}
	}
	return undefined;
}

/** Walk up from a file to the folder whose package.json is pi's. */
function packageDirFrom(start: string): string | undefined {
	let dir = dirname(start);
	for (let i = 0; i < 8; i++) {
		const pkg = join(dir, "package.json");
		if (existsSync(pkg)) {
			try {
				if (JSON.parse(readFileSync(pkg, "utf8")).name === PACKAGE_NAME) return dir;
			} catch {
				// keep looking
			}
		}
		// npm on Windows installs a .cmd shim next to node_modules
		const shimmed = join(dir, "node_modules", ...PACKAGE_NAME.split("/"));
		if (existsSync(join(shimmed, "package.json"))) return shimmed;
		const parent = dirname(dir);
		if (parent === dir) break;
		dir = parent;
	}
	return undefined;
}

export function locatePi(command = "pi"): PiInstall {
	const found = which(command);
	if (!found) throw new Error(`pi was not found ('${command}'). Install it with: npm install -g ${PACKAGE_NAME}`);
	const packageDir = packageDirFrom(realpathSync(found));
	if (!packageDir) throw new Error(`Found '${found}', but could not find the ${PACKAGE_NAME} package next to it.`);
	const pkg = JSON.parse(readFileSync(join(packageDir, "package.json"), "utf8")) as { version: string; bin?: string | Record<string, string> };
	const bin = typeof pkg.bin === "string" ? pkg.bin : (pkg.bin?.pi ?? Object.values(pkg.bin ?? {})[0]);
	if (!bin) throw new Error(`The ${PACKAGE_NAME} package at ${packageDir} names no program to start.`);
	const entry = join(packageDir, bin);
	if (!existsSync(entry)) throw new Error(`pi's entry file is missing: ${entry}`);
	return { command: found, packageDir, entry, version: pkg.version };
}
