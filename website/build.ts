/**
 * Builds the project website into website/dist: one static page per language (English at the root, the others under
 * /zh, /fr, /ru), with the subset fonts (website/fonts.py) and the screenshots (website/shots.sh).
 * No dependencies. Run: node website/build.ts
 */
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { LANGS, type Lang, type Strings, render } from "./src/page.ts";
import { sprite } from "./src/sprites.ts";

const here = dirname(fileURLToPath(import.meta.url));
const dist = join(here, "dist");
const load = (lang: Lang): Strings => JSON.parse(readFileSync(join(here, "locales", `${lang}.json`), "utf8"));

/** Every language has exactly English's keys, and lists of the same length. */
function sameShape(a: unknown, b: unknown, path: string): string[] {
	if (Array.isArray(a)) {
		if (!Array.isArray(b) || a.length !== b.length) return [`${path}: list length differs`];
		return a.flatMap((x, i) => sameShape(x, b[i], `${path}[${i}]`));
	}
	if (a && typeof a === "object") {
		if (!b || typeof b !== "object" || Array.isArray(b)) return [`${path}: not an object`];
		const [ka, kb] = [Object.keys(a), Object.keys(b)];
		return [
			...ka.filter((k) => !kb.includes(k)).map((k) => `${path}.${k}: missing`),
			...kb.filter((k) => !ka.includes(k)).map((k) => `${path}.${k}: not in en`),
			...ka.filter((k) => kb.includes(k)).flatMap((k) => sameShape((a as Record<string, unknown>)[k], (b as Record<string, unknown>)[k], `${path}.${k}`)),
		];
	}
	return typeof a === typeof b && (typeof b !== "string" || b.trim()) ? [] : [`${path}: empty or wrong type`];
}

const en = load("en");
const problems = LANGS.filter((l) => l !== "en").flatMap((l) => sameShape(en, load(l), l));

/** The pixel fonts hold only the characters the site uses: a Chinese character missing from the subset would fall back to another font. */
const fonts = join(here, "assets", "fonts");
const zhChars = existsSync(join(fonts, "zh-chars.txt")) ? new Set(readFileSync(join(fonts, "zh-chars.txt"), "utf8")) : new Set<string>();
const pages = LANGS.map((lang) => [lang, render(load(lang), lang)] as const);
for (const [lang, html] of pages) {
	const missing = [...new Set(html.replace(/<[^>]*>/g, ""))].filter((c) => c.codePointAt(0)! > 0x2000 && !"–—‘’“”„«»…·•→←™€".includes(c) && !zhChars.has(c));
	if (missing.length) problems.push(`${lang}: not in the font subset: ${missing.join("")} (run website/fonts.py)`);
	for (const shot of ["morning", "chat", "chat-window", "ask"])
		if (!existsSync(join(here, "assets", "shots", lang, `${shot}.png`))) problems.push(`${lang}: no screenshot ${shot}.png (run website/shots.sh ${lang})`);
}
if (problems.length) {
	console.error(problems.join("\n"));
	process.exit(1);
}

rmSync(dist, { recursive: true, force: true });
mkdirSync(join(dist, "fonts"), { recursive: true });
for (const f of ["body-latin.woff2", "body-zh.woff2", "head-15.woff2", "head-20.woff2", "LICENSE-fusion-pixel.txt", "LICENSE-jersey.txt"]) cpSync(join(fonts, f), join(dist, "fonts", f));
mkdirSync(join(dist, "assets"), { recursive: true });
cpSync(join(here, "assets", "shots"), join(dist, "assets", "shots"), { recursive: true });
cpSync(join(here, "assets", "og.png"), join(dist, "assets", "og.png"));
writeFileSync(join(dist, "assets", "icon.svg"), sprite("bot", 4).replace(' class="px"', "").replace(' aria-hidden="true"', ""));
cpSync(join(here, "src", "site.css"), join(dist, "site.css"));
for (const [lang, html] of pages) {
	const dir = lang === "en" ? dist : join(dist, lang);
	mkdirSync(dir, { recursive: true });
	writeFileSync(join(dir, "index.html"), html);
}
console.log(`website/dist: ${pages.length} pages`);
