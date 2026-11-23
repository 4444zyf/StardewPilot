# The StardewPilot website

One static page that introduces StardewPilot, in the mod's four languages (English at the root, `/zh`, `/fr`, `/ru`): a notice board on a wooden wall. Type and frames follow the game's own menus; the pixel art is drawn for the site, and the fonts are open-licence. It loads nothing from another site and needs no JavaScript.

```sh
node website/build.ts                              # writes website/dist
python3 -m http.server -d website/dist 8080        # look at it: http://localhost:8080
```

`website/dist` is plain files: any static host can serve it. It is served from Cloudflare Pages at https://getstardewpilot.pages.dev:

```sh
node website/build.ts && wrangler pages deploy website/dist --project-name getstardewpilot --branch main
```

| Path | What |
| --- | --- |
| `build.ts` | Checks that every language has English's keys, that the font subset holds every Chinese character used, and that each language has its screenshots; then writes the pages and copies fonts and images. |
| `src/page.ts` | The page, as a function of one language's strings. |
| `src/site.css` | Styles. Font sizes are whole multiples of each font's pixel grid, so the text stays crisp. |
| `src/sprites.ts` | The pixel sprites (mascot, icons), as inline SVG. |
| `locales/<lang>.json` | Every string on the page. A new string goes into all four. |
| `assets/shots/<lang>/` | Screenshots of the real game running the mod, in each language. The README uses the English ones. |
| `shots.sh` | Retakes one language's screenshots; its header says what it needs (the Linux dev setup). |
| `fonts.py` | Makes `assets/fonts/`: subsets of Fusion Pixel (body, with Chinese) and Jersey (titles), with their licences. Run it after adding Chinese text. |
| `art.ts` | Draws the social preview (`assets/og.png`) and the README's banner and icons (`assets/readme/`). |

The game's own font is not used: it is part of the game's art. Fusion Pixel is the closest open-licence match.
