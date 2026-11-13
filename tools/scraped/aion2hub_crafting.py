#!/usr/bin/env python3
"""Scrape AION2 crafting recipes from aion2hub.com/tools/crafting-calculator/<slug>-<id>.

Each page: result item (from slug), profession, and the material list with quantities.
Output: data/aion2hub/crafting.json  { "<resultId>": {name, profession, materials:[{id,name,qty,grade}]} }
"""
import json, os, re, time, html, urllib.request, urllib.error

UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}
HERE = os.path.dirname(os.path.abspath(__file__))
OUTDIR = os.path.join(HERE, "data", "aion2hub")
URLS = os.path.join(OUTDIR, "crafting-urls.json")
OUT = os.path.join(OUTDIR, "crafting.json")

GRADE_HEX = {
    "#9ca3af": "Common", "#b6b6b6": "Common", "#ffffff": "Common",
    "#4ade80": "Rare", "#22c55e": "Rare",
    "#4a9bf2": "Heroic", "#3b82f6": "Heroic", "#60a5fa": "Heroic",
    "#c084fc": "Unique", "#a855f7": "Unique",
    "#e3bd6e": "Epic", "#f59e0b": "Epic", "#fbbf24": "Epic", "#f0a020": "Epic",
    "#ef4444": "Legendary", "#fb7185": "Legendary",
}
MAT = re.compile(
    r'href="/database/items/(\d+)">\s*<span[^>]*?(?:color:(#[0-9a-fA-F]{3,8}))?[^>]*>([^<]+)</span>\s*</a>\s*</span>\s*'
    r'<span class="tabular-nums[^"]*">\s*[×xX]\s*([\d,]+)',
    re.S,
)
PROF = re.compile(r"with\s+(Blacksmithing|Tailoring|Alchemy|Cooking|Handicraft\w*|Weaponsmith\w*|Armorsmith\w*|Woodworking|Leatherworking|Jewel\w*)", re.I)


def fetch(url, tries=4):
    for t in range(tries):
        try:
            r = urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=30)
            return r.status, r.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return 404, ""
            time.sleep(1.2 * (t + 1))
        except Exception:
            time.sleep(1.2 * (t + 1))
    return 0, ""


def txt(s):
    return html.unescape(re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", s))).strip()


def parse(url, body):
    body = re.sub(r"<!--.*?-->", "", body)
    m = re.search(r"-(\d+)$", url.rstrip("/"))
    rid = m.group(1) if m else None
    rec = {"url": url}
    m = re.search(r"<h1[^>]*>(.*?)</h1>", body, re.S)
    if m:
        rec["name"] = re.split(r"\s+[—–-]\s+Crafting", txt(m.group(1)))[0].strip()
    m = PROF.search(txt(body))
    if m:
        rec["profession"] = m.group(1)
    mats = {}
    for iid, hexcol, name, qty in MAT.findall(body):
        iid = int(iid)
        q = int(qty.replace(",", ""))
        if iid not in mats:
            d = {"id": iid, "name": txt(name), "qty": q}
            g = GRADE_HEX.get((hexcol or "").lower())
            if g:
                d["grade"] = g
            mats[iid] = d
    if mats:
        rec["materials"] = list(mats.values())
    return rid, rec


def main():
    urls = json.load(open(URLS, encoding="utf-8"))
    out = {}
    if os.path.exists(OUT):
        out = json.load(open(OUT, encoding="utf-8"))
    for i, u in enumerate(urls):
        rid = re.search(r"-(\d+)$", u.rstrip("/"))
        if rid and rid.group(1) in out:
            continue
        st, body = fetch(u)
        if st == 200 and body:
            rid, rec = parse(u, body)
            if rid:
                out[rid] = rec
        if i % 50 == 0:
            json.dump(out, open(OUT, "w", encoding="utf-8"), separators=(",", ":"), ensure_ascii=False)
            print(f"{i}/{len(urls)} recipes={len(out)}", flush=True)
        time.sleep(0.25)
    json.dump(out, open(OUT, "w", encoding="utf-8"), separators=(",", ":"), ensure_ascii=False)
    print(f"DONE {len(out)} recipes -> {OUT}")


if __name__ == "__main__":
    main()
