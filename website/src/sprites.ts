/** The site's pixel sprites, drawn for StardewPilot (original art). Rows of palette letters; "." is empty. */
const BASE: Record<string, string> = { K: "#3b2313", W: "#fff6dc", S: "#e3d2a8" };
type Sprite = { w?: number; p: Record<string, string>; r: string[] };
export const SPRITES: Record<string, Sprite> = {
	"bot": {
		"p": {
			"Y": "#f2c94c",
			"D": "#c98a1b",
			"R": "#d9452b",
			"L": "#cfd9e2",
			"G": "#8fa1b3",
			"C": "#3fd0e0",
			"B": "#3f6fc4",
			"T": "#8a5a2b"
		},
		"r": [
			"....YYYYYYYY",
			"...YYYYYYYYYY",
			"..YRRRRRRRRRRY",
			".DDDDDDDDDDDDDD",
			"...KKKKKKKKKK",
			"...KLLLLLLLLK",
			"...KLCCLLCCLK",
			"...KLCCLLCCLK",
			"...KLLLLLLLLK",
			"...KGLKKKKLGK",
			"...KKKKKKKKKK",
			"TT..BBBBBBBB..TT",
			".TTTBBBYYBBBTTT",
			"....BBBBBBBB",
			"....KBBBBBBK",
			".......TT",
			".......TT",
			"......KTTK"
		]
	},
	"bubble": {
		"p": {},
		"r": [
			"",
			"..KKKKKKKKKKKK",
			".KWWWWWWWWWWWWK",
			".KWWWWWWWWWWWWK",
			".KWWKKWKKWKKWWK",
			".KWWKKWKKWKKWWK",
			".KWWWWWWWWWWWWK",
			".KWWWWWWWWWWWWK",
			"..KKKKWWWKKKKK",
			".....KWWK",
			".....KWK",
			".....KK"
		]
	},
	"can": {
		"p": {
			"B": "#6aa7d8",
			"L": "#bfe1f7",
			"C": "#3fd0e0"
		},
		"r": [
			"",
			"",
			".......KKKKKK",
			"......K......K",
			"KK....KKKKKKKK",
			"KBK...KBBBBBBKK",
			".KBK..KBLBBBBKBK",
			"..KBKKKBLBBBBKBK",
			"...KBBBBLBBBBKBK",
			"....KKKBBBBBBKK",
			"......KBBBBBBK",
			"......KBBBBBBK",
			"......KKKKKKKK",
			"",
			"C..C",
			".C..C"
		]
	},
	"moon": {
		"p": {
			"Y": "#ffe27a"
		},
		"r": [
			"",
			".....KKKKK",
			"...KKYYYYYK",
			"..KYYYYKKKK",
			".KYYYYK",
			".KYYYK......Y",
			"KYYYYK.....YYY",
			"KYYYYK......Y",
			"KYYYYK",
			"KYYYYYK",
			".KYYYYYK....K",
			".KYYYYYYKKKKYK",
			"..KYYYYYYYYYYK",
			"...KKYYYYYYKK",
			".....KKKKKK"
		]
	},
	"book": {
		"p": {
			"R": "#b5452c",
			"D": "#7d2c1b"
		},
		"r": [
			"",
			"..KKKKKKKKKKKK",
			".KRRRRRRRRRRRRK",
			".KRWWWWWWWWWWRK",
			".KRWKKKKKKKKWRK",
			".KRWWWWWWWWWWRK",
			".KRWKKKKKKWWWRK",
			".KRWWWWWWWWWWRK",
			".KRWKKKKKKKKWRK",
			".KRWWWWWWWWWWRK",
			".KRWKKKKKWWWWRK",
			".KRWWWWWWWWWWRK",
			".KRRRRRRRRRRRRK",
			".KDDDDDDDDDDDDK",
			"..KKKKKKKKKKKK"
		]
	},
	"clock": {
		"p": {
			"R": "#d9452b"
		},
		"r": [
			"",
			"....KKKKKKKK",
			"..KKWWWWWWWWKK",
			".KWWWWWKWWWWWWK",
			".KWWWWWKWWWWWWK",
			"KWWWWWWKWWWWWWWK",
			"KWWWWWWKWWWWWWWK",
			"KWWWWWWRRRRRWWWK",
			"KWWWWWWWWWWWWWWK",
			"KWWWWWWWWWWWWWWK",
			"KWWWWWWWWWWWWWWK",
			".KWWWWWWWWWWWWK",
			".KWWWWWWWWWWWWK",
			"..KKWWWWWWWWKK",
			"....KKKKKKKK"
		]
	},
	"heart": {
		"p": {
			"R": "#e2504c",
			"H": "#ffb3a8"
		},
		"r": [
			"",
			"",
			"..KKK....KKK",
			".KRRRK..KRRRK",
			"KRHRRRKKRRRRRK",
			"KRHRRRRRRRRRRK",
			"KRRRRRRRRRRRRK",
			"KRRRRRRRRRRRRK",
			".KRRRRRRRRRRK",
			"..KRRRRRRRRK",
			"...KRRRRRRK",
			"....KRRRRK",
			".....KRRK",
			"......KK"
		]
	},
	"star": {
		"w": 7,
		"p": {
			"Y": "#ffe27a"
		},
		"r": [
			"...Y",
			"...Y",
			"..YYY",
			"YYYYYYY",
			"..YYY",
			"...Y",
			"...Y"
		]
	},
	"sprout": {
		"p": {
			"G": "#2f7d32",
			"L": "#7ed957",
			"M": "#6b4423",
			"D": "#4e2f16"
		},
		"r": [
			"",
			"",
			"",
			"",
			"",
			"",
			"....GG....GG",
			"...GLLG..GLLG",
			"...GLLLGGLLLG",
			"....GLLLLLLG",
			".....GGLLGG",
			".......LL",
			".......LL",
			"..MMMMMMMMMMMM",
			".MMDMMMMMMDMMMM",
			"..MMMMMDMMMMM"
		]
	},
	"parsnip": {
		"p": {
			"G": "#2f7d32",
			"L": "#7ed957",
			"P": "#f6e3a1",
			"D": "#d9b96a",
			"M": "#6b4423"
		},
		"r": [
			"....G......G",
			"...GLG....GLG",
			"...GLLG..GLLG",
			"....GLLGGLLG",
			".....GLLLLG",
			"......KPPK",
			".....KPPPPK",
			".....KPPDPK",
			".....KPPPPK",
			"......KPPK",
			"......KPDK",
			".......KPK",
			".......KK"
		]
	},
	"chicken": {
		"p": {
			"R": "#d9452b",
			"O": "#f29a2e"
		},
		"r": [
			"",
			".....RR",
			"....KRRK",
			"...KWWWWK",
			"...KWKWWK",
			"..OOWWWWK....KK",
			"...KWWWWKK..KWK",
			"...KWWWWWWKKWWK",
			"...KWWSWWWWWWWK",
			"...KWWWSSSWWWWK",
			"....KWWWWWWWWK",
			".....KKKKKKKK",
			"......O...O",
			"......O...O",
			".....OO..OO"
		]
	}
};

export type SpriteName = keyof typeof SPRITES;

/** An inline SVG of a sprite, z screen pixels per sprite pixel. Decorative: hidden from screen readers. */
export function sprite(name: string, z = 4, cls = "px"): string {
	const s = SPRITES[name];
	const pal = { ...BASE, ...s.p };
	const w = s.w ?? 16;
	let out = "";
	s.r.forEach((row, y) => {
		for (let x = 0; x < row.length; ) {
			const c = row[x];
			let n = 1;
			while (row[x + n] === c) n++;
			if (c !== ".") out += `<rect x="${x}" y="${y}" width="${n}" height="1" fill="${pal[c]}"/>`;
			x += n;
		}
	});
	return `<svg class="${cls}" xmlns="http://www.w3.org/2000/svg" width="${w * z}" height="${s.r.length * z}" viewBox="0 0 ${w} ${s.r.length}" shape-rendering="crispEdges" aria-hidden="true">${out}</svg>`;
}
