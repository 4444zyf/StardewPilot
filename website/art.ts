/**
 * Draws the site's social preview (assets/og.png) and the README's banner and icons (assets/readme/) in the site's
 * style, with a headless Chrome. Run after changing the look: node website/art.ts
 * Chrome: $CHROME, else Playwright's chrome-headless-shell in ~/.cache/ms-playwright.
 */
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, writeFileSync } from "node:fs";
import { homedir, tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { sprite } from "./src/sprites.ts";

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, "assets");
mkdirSync(join(out, "readme"), { recursive: true });

function chrome(): string {
	if (process.env.CHROME) return process.env.CHROME;
	const root = join(homedir(), ".cache", "ms-playwright");
	const dir = readdirSync(root).filter((d) => d.startsWith("chromium_headless_shell")).sort().pop();
	const bin = dir && join(root, dir, "chrome-headless-shell-linux64", "chrome-headless-shell");
	if (!bin || !existsSync(bin)) throw new Error("No Chrome: set CHROME to a Chrome or chrome-headless-shell binary");
	return bin;
}

const css = `<link rel="stylesheet" href="file://${join(here, "src", "site.css")}">
<style>@font-face{font-family:"Fusion Pixel";src:url(file://${join(out, "fonts", "body-latin.woff2")})}
@font-face{font-family:"Jersey 20";src:url(file://${join(out, "fonts", "head-20.woff2")})}
@font-face{font-family:"Jersey 15";src:url(file://${join(out, "fonts", "head-15.woff2")})}
body{margin:0;overflow:hidden;position:relative}
.c{position:absolute;inset:0;display:grid;place-items:center}
.pin{position:absolute;background:#fff7dc;padding:16px 18px;box-shadow:5px 5px 0 #0006;font-size:24px;line-height:1.35;width:230px}
.pin::before{content:"";position:absolute;top:-8px;left:50%;width:18px;height:18px;margin-left:-9px;background:#d9452b;border-radius:50%;box-shadow:inset -4px -4px 0 #8a2a1a}
.bot{position:absolute}</style>`;

function shoot(name: string, w: number, h: number, body: string): void {
	const file = join(tmpdir(), `stardewpilot-art-${name.replace(/\W/g, "-")}.html`);
	writeFileSync(file, `<!doctype html><html><head><meta charset="utf-8">${css}</head><body style="width:${w}px;height:${h}px">${body}</body></html>`);
	execFileSync(chrome(), ["--no-sandbox", "--hide-scrollbars", `--window-size=${w},${h}`, "--virtual-time-budget=2000", `--screenshot=${join(out, name)}`, `file://${file}`], { stdio: "ignore" });
	console.log(name);
}

const sign = (size: number) => `<div class="sign" style="margin:0"><div class="board"><h1 style="font-size:${size}px">StardewPilot</h1><p>a copilot for your farm</p></div></div>`;
const pins = [
	`<div class="pin" style="left:44px;top:56px;transform:rotate(-3deg)">${sprite("clock", 3)}<br>Show it once. It does it every morning.</div>`,
	`<div class="pin" style="right:44px;top:70px;transform:rotate(2.5deg)">${sprite("can", 3)}<br>Hand over the chores.</div>`,
];

shoot("readme/banner.png", 1280, 320, `${pins.join("")}<div class="c">${sign(110)}</div>`);
shoot("og.png", 1200, 630, `${pins.join("")}<div class="pin" style="left:60px;bottom:70px;transform:rotate(1.5deg)">${sprite("bubble", 3)}<br>Ask anything, from your own save.</div>
<div class="pin" style="right:60px;bottom:60px;transform:rotate(-2deg)">${sprite("heart", 3)}<br>It asks before anything big.</div>
<div class="c">${sign(120)}</div><div class="bot" style="left:552px;bottom:40px">${sprite("bot", 6)}</div>`);
for (const name of ["bot", "bubble", "can", "moon", "book", "clock", "heart", "star", "sprout", "parsnip", "chicken"]) writeFileSync(join(out, "readme", `${name}.svg`), sprite(name, 4).replace(' class="px"', ""));
console.log("readme icons");
