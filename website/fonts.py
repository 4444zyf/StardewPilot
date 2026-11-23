"""Makes the website's font files: small subsets of the open-licence pixel fonts, holding only the characters the
site uses. Run it again after adding Chinese text (build.ts says so when a character is missing).

    python3 -m venv /tmp/fontenv && /tmp/fontenv/bin/pip install fonttools brotli
    /tmp/fontenv/bin/python website/fonts.py

Sources (SIL Open Font License, versions pinned): Fusion Pixel 12px proportional (latin and zh_hans) from its GitHub
release, Jersey 15 and 20 from fontsource on npm. Downloads are cached in ~/.cache/stardewpilot-site-fonts.
Writes website/assets/fonts/{body-latin,body-zh,head-15,head-20}.woff2, zh-chars.txt and the licences.
"""
import io, json, pathlib, re, tarfile, urllib.request, zipfile
from fontTools import subset

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE / "assets" / "fonts"
CACHE = pathlib.Path.home() / ".cache" / "stardewpilot-site-fonts"
FUSION = "https://github.com/TakWolf/fusion-pixel-font/releases/download/2026.11.10/fusion-pixel-font-12px-proportional-otf.woff2-v2026.11.10.zip"
JERSEY = "https://registry.npmjs.org/@fontsource/jersey-{n}/-/jersey-{n}-5.3.0.tgz"

# Everything the Latin and Cyrillic pages can show, whatever the text: ASCII, Latin-1, Cyrillic, common punctuation.
LATIN = "".join(map(chr, range(0x20, 0x7F))) + "".join(map(chr, range(0xA0, 0x180))) + "".join(map(chr, range(0x400, 0x460))) + "–—‘’“”„«»…·•→←™€"


def fetch(url: str) -> bytes:
    CACHE.mkdir(parents=True, exist_ok=True)
    f = CACHE / url.rsplit("/", 1)[1]
    if not f.exists():
        f.write_bytes(urllib.request.urlopen(url, timeout=120).read())
    return f.read_bytes()


def site_text() -> str:
    text = "".join(p.read_text("utf8") for p in (HERE / "locales").glob("*.json"))
    return text + "".join(p.read_text("utf8") for p in (HERE / "src").glob("*.ts"))


def wide(text: str) -> str:
    """The characters outside the Latin set: Chinese, and full-width punctuation."""
    return "".join(sorted({c for c in text if c not in LATIN and not c.isspace() and ord(c) > 0x2000}))


def make(src: bytes, chars: str, out: str) -> None:
    opts = subset.Options()
    opts.flavor = "woff2"
    opts.layout_features = ["*"]
    opts.name_IDs = ["*"]
    opts.notdef_outline = True
    font = subset.load_font(io.BytesIO(src), opts)
    sub = subset.Subsetter(opts)
    sub.populate(text=chars)
    sub.subset(font)
    subset.save_font(font, str(OUT / out), opts)
    print(f"{out}: {(OUT / out).stat().st_size // 1024} KB")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    z = zipfile.ZipFile(io.BytesIO(fetch(FUSION)))
    zh = wide(site_text())
    make(z.read("fusion-pixel-12px-proportional-latin.otf.woff2"), LATIN, "body-latin.woff2")
    make(z.read("fusion-pixel-12px-proportional-zh_hans.otf.woff2"), zh + LATIN, "body-zh.woff2")
    (OUT / "zh-chars.txt").write_text(zh + "\n", "utf8")
    (OUT / "LICENSE-fusion-pixel.txt").write_bytes(z.read("OFL.txt"))
    for n in (15, 20):
        t = tarfile.open(fileobj=io.BytesIO(fetch(JERSEY.format(n=n))))
        woff = t.extractfile(f"package/files/jersey-{n}-latin-400-normal.woff2").read()
        make(woff, LATIN, f"head-{n}.woff2")
        if n == 15:
            (OUT / "LICENSE-jersey.txt").write_bytes(t.extractfile("package/LICENSE").read())
    print(f"zh-chars.txt: {len(zh)} characters")


if __name__ == "__main__":
    main()
