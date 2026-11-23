/** The website's one page, as a function of a language's strings (website/locales/<lang>.json). Works without JavaScript. */
import en from "../locales/en.json" with { type: "json" };
import { sprite } from "./sprites.ts";

export type Strings = typeof en;
export const LANGS = ["en", "zh", "fr", "ru"] as const;
export type Lang = (typeof LANGS)[number];

export const LANG_NAMES: Record<Lang, string> = { en: "English", zh: "简体中文", fr: "Français", ru: "Русский" };
const HTML_LANG: Record<Lang, string> = { en: "en", zh: "zh-Hans", fr: "fr", ru: "ru" };
const REPO = "https://github.com/4444zyf/StardewPilot";
/** Where the site is served (Cloudflare Pages); social previews need absolute links. */
export const SITE = "https://getstardewpilot.pages.dev";

/** One sprite per feature note, in the order of jobs.notes. */
const NOTE_ICONS = ["bubble", "can", "moon", "book", "clock", "heart"];
/** The screenshots (website/shots.sh), in the order of shots.items. */
const SHOTS = ["morning", "chat", "ask"];
/** The calendar: Spring starts on a Monday. Today is the 12th, a Friday. */
const TODAY = 12;
const RAIN = [3, 9, 10, 20];
const BIRTHDAY = 20;

const e = (s: string | number) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);

function calendar(t: Strings): string {
	const cells: string[] = t.calendar.days.map((d) => `<div class="h">${e(d)}</div>`);
	for (let d = 1; d <= 28; d++) {
		const friday = d % 7 === 5;
		const icons: string[] = [];
		if (d >= TODAY) icons.push(sprite("clock", 2));
		if (d >= TODAY && friday) icons.push(sprite("can", 2));
		if (d === BIRTHDAY) icons.push(sprite("heart", 2));
		const cls = [RAIN.includes(d) ? "rain" : "", d === TODAY ? "today" : ""].filter(Boolean).join(" ");
		cells.push(`<div${cls ? ` class="${cls}"` : ""}${d === TODAY ? ` title="${e(t.calendar.today)}"` : ""}><span>${d}</span><i>${icons.join("")}</i></div>`);
	}
	return `<div class="grid">${cells.join("")}</div>`;
}

export function render(t: Strings, lang: Lang): string {
	const base = lang === "en" ? "./" : "../";
	const href = (l: Lang) => (l === "en" ? base : `${base}${l}/`);
	const legendIcons = [sprite("clock", 3), sprite("can", 3), `<span class="raindot"></span>`, sprite("heart", 3)];

	return `<!doctype html>
<html lang="${HTML_LANG[lang]}" class="l-${lang}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${e(t.meta.title)}</title>
<meta name="description" content="${e(t.meta.description)}">
<meta property="og:title" content="${e(t.meta.title)}">
<meta property="og:description" content="${e(t.meta.description)}">
<meta property="og:type" content="website">
<meta property="og:image" content="${SITE}/assets/og.png">
<meta property="og:url" content="${SITE}/${lang === "en" ? "" : `${lang}/`}">
<meta name="twitter:card" content="summary_large_image">
<link rel="canonical" href="${SITE}/${lang === "en" ? "" : `${lang}/`}">
<meta name="theme-color" content="#8a5326">
<link rel="icon" href="${base}assets/icon.svg" type="image/svg+xml">
${LANGS.filter((l) => l !== lang).map((l) => `<link rel="alternate" hreflang="${HTML_LANG[l]}" href="${href(l)}">`).join("\n")}
<link rel="preload" href="${base}fonts/body-latin.woff2" as="font" type="font/woff2" crossorigin>
<link rel="stylesheet" href="${base}site.css">
</head>
<body>
<header class="top">
	<nav class="langs" aria-label="${e(t.nav.language)}">${LANGS.map((l) => (l === lang ? `<span class="tag on" lang="${HTML_LANG[l]}">${LANG_NAMES[l]}</span>` : `<a class="tag" lang="${HTML_LANG[l]}" hreflang="${HTML_LANG[l]}" href="${href(l)}">${LANG_NAMES[l]}</a>`)).join("")}</nav>
	<a class="tag gh" href="${REPO}">GitHub</a>
</header>

<div class="sign">
	<div class="board"><h1>StardewPilot</h1><p>${e(t.sign.tagline)}</p></div>
	<div class="posts"><i></i><i></i></div>
</div>
<nav class="jump">
	<a class="frame thin" href="#jobs">${e(t.nav.jobs)}</a><a class="frame thin" href="#shots">${e(t.nav.shots)}</a><a class="frame thin" href="#calendar">${e(t.nav.calendar)}</a><a class="frame thin" href="#start">${e(t.nav.start)}</a>
</nav>
<div class="lead"><div class="frame">
	<p>${e(t.lead.text)}</p>
	<p class="btns"><a class="btn green" href="${REPO}/releases/latest">${e(t.lead.download)}</a><a class="btn" href="${REPO}">${e(t.lead.source)}</a></p>
</div></div>

<main>
<section class="board2" id="jobs">
	<h2 class="frame thin">${e(t.jobs.title)}</h2>
	<div class="notes">
${t.jobs.notes.map((n, i) => `		<article class="note">
			<div class="kind">${e(n.tag)}</div>${sprite(NOTE_ICONS[i], 4)}
			<h3>${e(n.title)}</h3>
			<p>${e(n.text)}</p>
			<p class="say">${e(t.jobs.try)} <b>${e(n.say)}</b></p>
		</article>`).join("\n")}
	</div>
</section>

<section class="board2" id="shots">
	<h2 class="frame thin">${e(t.shots.title)}</h2>
	<p class="sub">${e(t.shots.lead)}</p>
	<div class="photos">
${[1, 0, 2].map((i) => {
		const wide = SHOTS[i] === "chat";
		const img = wide ? "chat-window" : SHOTS[i];
		const [w, h] = wide ? [1020, 650] : [640, 400];
		return `		<figure class="photo${wide ? " wide" : ""}"><a href="${base}assets/shots/${lang}/${SHOTS[i]}.png"><img src="${base}assets/shots/${lang}/${img}.png" width="${w}" height="${h}" loading="lazy" alt="${e(t.shots.items[i].alt)}"></a><figcaption>${e(t.shots.items[i].caption)}</figcaption></figure>`;
	}).join("\n")}
	</div>
</section>

<section class="board2" id="calendar">
	<h2 class="frame thin">${e(t.calendar.title)}</h2>
	<div class="cal">
		${calendar(t)}
		<div class="side">
			<ul class="frame thin legend">${t.calendar.legend.map((l, i) => `<li>${legendIcons[i]}<span>${e(l)}</span></li>`).join("")}</ul>
			<div class="frame thin rules">
				<h3>${e(t.calendar.rulesTitle)}</h3>
				<ul>${t.calendar.rules.map((r) => `<li>${e(r)}</li>`).join("")}</ul>
			</div>
		</div>
	</div>
</section>

<section class="board2" id="start">
	<h2 class="frame thin">${e(t.start.title)}</h2>
	<ol class="notes four">
${t.start.steps.map((s, i) => `		<li class="note"><h3><span>${i + 1}</span> ${e(s.title)}</h3><p>${e(s.text)}</p></li>`).join("\n")}
	</ol>
	<p class="more"><a class="frame thin" href="${REPO}#readme">${e(t.start.more)} ${sprite("star", 2)}</a></p>
</section>
</main>

<footer>
	<p class="links"><a href="${REPO}/releases">${e(t.footer.releases)}</a> · <a href="${REPO}/tree/main/docs">${e(t.footer.docs)}</a> · <a href="${REPO}/issues">${e(t.footer.issues)}</a> · <a href="${REPO}/blob/main/LICENSE">${e(t.footer.license)}</a></p>
	<p>${e(t.footer.fan)}</p>
	<p class="small">${e(t.footer.fonts)}</p>
	<p class="mascot">${sprite("bot", 4)}</p>
</footer>
</body>
</html>
`;
}
