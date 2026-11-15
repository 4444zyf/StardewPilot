import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, it } from "node:test";
import { Supervisor } from "../src/wrapper/supervisor.ts";

/** A fake pi: records its arguments and environment, then either exits at once or waits for stdin to close. */
function fakePi(dir: string, mode: "crash" | "stay"): string {
	const script = join(dir, `fake-pi-${mode}.mjs`);
	writeFileSync(script, `
import { appendFileSync } from "node:fs";
appendFileSync(${JSON.stringify(join(dir, "runs.log"))}, JSON.stringify({ args: process.argv.slice(2), dir: process.env.PI_CODING_AGENT_DIR }) + "\\n");
${mode === "crash"
		? `console.error("fatal: no credentials"); process.exit(3);`
		: `process.stdin.resume(); process.stdin.on("end", () => process.exit(0));`}
`);
	const wrapper = join(dir, `fake-pi-${mode}.sh`);
	writeFileSync(wrapper, `#!/bin/sh\nexec ${JSON.stringify(process.execPath)} ${JSON.stringify(script)} "$@"\n`, { mode: 0o755 });
	return wrapper;
}

const until = async (cond: () => boolean, ms = 3000) => {
	const end = Date.now() + ms;
	while (!cond()) {
		if (Date.now() > end) throw new Error("timed out");
		await new Promise((r) => setTimeout(r, 10));
	}
};

describe("pi supervisor", { skip: process.platform === "win32" }, () => {
	it("starts pi with the extension, model and folder, and stops it by closing stdin", async () => {
		const dir = mkdtempSync(join(tmpdir(), "sp-sup-"));
		const states: string[] = [];
		const sup = new Supervisor(() => states.push(sup.state), { startupMs: 50 });
		await sup.start({ command: fakePi(dir, "stay"), extension: "/ext/index.ts", piDir: "/pi-dir", model: "keyco/m1", thinking: "low", cwd: dir });
		await until(() => sup.state === "running" && existsSync(join(dir, "runs.log")));
		const run = JSON.parse(readFileSync(join(dir, "runs.log"), "utf8").trim());
		assert.deepEqual(run.args, ["--mode", "rpc", "--no-builtin-tools", "-e", "/ext/index.ts", "--model", "keyco/m1", "--thinking", "low"]);
		assert.equal(run.dir, "/pi-dir");

		const started = Date.now();
		await sup.stop();
		assert.ok(Date.now() - started < 2500, "pi should exit when stdin closes, before the kill timeout");
		assert.equal(sup.pid, undefined);
	});

	it("restarts pi that keeps failing, then gives up with its last error", async () => {
		const dir = mkdtempSync(join(tmpdir(), "sp-sup-"));
		const sup = new Supervisor(() => {}, { backoff: [10, 20], startupMs: 1000 });
		await sup.start({ command: fakePi(dir, "crash"), extension: "/ext/index.ts", piDir: "/pi", model: "keyco/m1", thinking: "off", cwd: dir });
		await until(() => sup.state === "error", 5000);
		const runs = readFileSync(join(dir, "runs.log"), "utf8").trim().split("\n");
		assert.equal(runs.length, 5);
		assert.match(sup.message, /keeps stopping/);
		assert.match(sup.message, /fatal: no credentials/);
		assert.ok(sup.recentErrors.includes("fatal: no credentials"));
		await sup.stop();
	});

	it("reports needs_setup without starting pi", async () => {
		const sup = new Supervisor(() => {});
		await sup.needsSetup("No model chosen yet.");
		assert.equal(sup.state, "needs_setup");
		assert.equal(sup.message, "No model chosen yet.");
	});
});
