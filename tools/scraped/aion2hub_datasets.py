#!/usr/bin/env python3
"""Scrape AION2 single-page datasets from aion2hub.com (set bonuses, titles, etc.).

Each dataset is one page with all rows server-rendered in the HTML. Output: one JSON
per dataset under data/aion2hub/. Run: python aion2hub_datasets.py [name ...]  (default: all tractable)
"""
import json, os, re, sys, time, html, urllib.request

UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}
HERE = os.path.dirname(os.path.abspath(__file__))
OUTDIR = os.path.join(HERE, "data", "aion2hub")


def fetch(url):
    for t in range(4):
        try:
            r = urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=60)
            return r.read().decode("utf-8", "replace")
        except Exception:
            time.sleep(1.5 * (t + 1))
    return ""


def clean(s):
    return html.unescape(re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", s))).strip()


def nocomment(b):
    return re.sub(r"<!--.*?-->", "", b)


# ---- set bonuses -------------------------------------------------------------
def parse_sets(body):
    body = nocomment(body)
    out = []
    blocks = re.split(r"<h3", body)
    for blk in blocks[1:]:
        m = re.match(r'[^>]*>(.*?)</h3>', blk, re.S)
        if not m:
            continue
        name = clean(m.group(1))
        if not name or len(name) > 60:
            continue
        rec = {"name": name}
        pm = re.search(r">(\d+)\s*pieces<", blk)
        if pm:
            rec["pieceCount"] = int(pm.group(1))
        cm = re.search(r'font-semibold uppercase[^>]*>([^<]+Set)<', blk)
        if cm:
            rec["category"] = clean(cm.group(1))
        # bonus tiers: "N-pc" -> effect <p>
        bonuses = []
        for bm in re.finditer(r'>(\d+)-pc</span>.*?<p class="leading-snug[^"]*">([^<]+)</p>', blk, re.S):
            bonuses.append({"pieces": int(bm.group(1)), "effect": clean(bm.group(2))})
        if bonuses:
            rec["bonuses"] = bonuses
        pieces = sorted({int(x) for x in re.findall(r'/database/items/(\d+)', blk)})
        if pieces:
            rec["pieceItemIds"] = pieces
        # stop a block at the next section header to avoid bleed
        if bonuses or pieces:
            out.append(rec)
    return out


# ---- generic section + table extractor (for system/positional datasets) ------
def parse_generic(body):
    body = nocomment(body)
    # cut nav/footer: keep from first content <h1/h2> to <footer>
    fi = body.find("<footer")
    if fi > 0:
        body = body[:fi]
    SKIP = {"Tools", "Database", "Maps", "Builds", "Guides"}
    out = []
    parts = re.split(r"(<h[23][^>]*>)", body)
    # walk header/content pairs
    i = 1
    cur = None
    chunks = re.split(r"(<h[23][^>]*>.*?</h[23]>)", body, flags=re.S)
    for ch in chunks:
        hm = re.match(r"<h[23][^>]*>(.*?)</h[23]>", ch, re.S)
        if hm:
            title = clean(hm.group(1))
            cur = None
            if title and title not in SKIP and len(title) < 60:
                cur = {"section": title}
                out.append(cur)
        elif cur is not None:
            text = clean(ch)
            if text:
                cur["text"] = text[:600]
            ids = sorted({int(x) for x in re.findall(r"/database/items/(\d+)", ch)})
            if ids:
                cur["itemIds"] = ids
            # tables -> rows of cells
            tables = []
            for tm in re.finditer(r"<table[^>]*>(.*?)</table>", ch, re.S):
                rows = []
                for rm in re.finditer(r"<tr[^>]*>(.*?)</tr>", tm.group(1), re.S):
                    cells = [clean(c) for c in re.findall(r"<t[dh][^>]*>(.*?)</t[dh]>", rm.group(1), re.S)]
                    if any(cells):
                        rows.append(cells)
                if rows:
                    tables.append(rows)
            if tables:
                cur["tables"] = tables
            # map markers (name + map + marker id/coords)
            markers = []
            for mm in re.finditer(r'href="/maps\?map=([^&"]+)&marker=([^"]+)"[^>]*>.*?truncate[^>]*>([^<]+)<', ch, re.S):
                markers.append({"map": mm.group(1), "marker": mm.group(2), "name": clean(mm.group(3))})
            if markers:
                cur["markers"] = markers
    return [s for s in out if len(s) > 1]


# ---- item-card grid (drop-rates, cosmetics) ---------------------------------
CARD = re.compile(
    r'href="/database/items/(\d+)"[^>]*>.*?'
    r'<span[^>]*truncate[^>]*(?:color:(#[0-9a-fA-F]{3,8}))?[^>]*>([^<]+)</span>'
    r'(?:\s*<span[^>]*>([^<]*)</span>)?',
    re.S,
)


def parse_cards(body):
    body = nocomment(body)
    fi = body.find("<footer")
    if fi > 0:
        body = body[:fi]
    seen = {}
    for iid, hexcol, name, label in CARD.findall(body):
        iid = int(iid)
        if iid in seen:
            continue
        d = {"id": iid, "name": clean(name)}
        g = {
            "#9ca3af": "Common", "#b6b6b6": "Common", "#b9b3a4": "Common",
            "#57c98a": "Rare", "#4ade80": "Rare",
            "#4a9bf2": "Heroic", "#e3bd6e": "Epic", "#c084fc": "Unique",
            "#e0708a": "Legendary", "#ef4444": "Legendary",
        }.get((hexcol or "").lower())
        if g:
            d["grade"] = g
        lab = clean(label or "")
        if lab and len(lab) < 40:
            d["label"] = lab
        seen[iid] = d
    return list(seen.values())


# ---- achievements (2,550 cards) ---------------------------------------------
ACH_SPLIT = re.compile(r'<h3 class="min-w-0 font-cinzel[^"]*"[^>]*>')


def parse_achievements(body):
    body = nocomment(body)
    out = []
    parts = ACH_SPLIT.split(body)
    for p in parts[1:]:
        # name is at the very start up to </h3>
        nm = re.match(r"(.*?)</h3>", p, re.S)
        if not nm:
            continue
        name = clean(nm.group(1))
        if not name:
            continue
        blk = p[:4000]
        rec = {"name": name}
        gm = re.search(r'>\s*(Common|Rare|Heroic|Unique|Epic|Legendary|Mythic)\s*</span>', blk)
        if gm:
            rec["grade"] = gm.group(1)
        fm = re.search(r'>(Elyos|Asmodian|Both factions)</span>', blk)
        if fm:
            rec["faction"] = fm.group(1)
        # the three meta spans: Achievement | faction | category
        cm = re.search(r'text-gray-600">([^<]+)</span>', blk)
        if cm:
            rec["category"] = clean(cm.group(1))
        ps = re.findall(r'<p[^>]*>([^<]+)</p>', blk)
        if ps:
            rec["objective"] = clean(ps[0])
        pm = re.search(r'([\d,]+)\s*(?:Achievement )?Points?', blk)
        if pm:
            rec["points"] = int(pm.group(1).replace(",", ""))
        ids = sorted({int(x) for x in re.findall(r"/database/items/(\d+)", blk)})
        if ids:
            rec["rewardItemIds"] = ids
        out.append(rec)
    return out


DATASETS = {
    "sets": ("https://aion2hub.com/database/sets", parse_sets),
    "achievements": ("https://aion2hub.com/database/achievements", parse_achievements),
    "drop-rates": ("https://aion2hub.com/database/drop-rates", parse_cards),
    "cosmetics": ("https://aion2hub.com/database/cosmetics", parse_cards),
    "world-objects": ("https://aion2hub.com/database/world-objects", parse_generic),
    "monoliths": ("https://aion2hub.com/database/monoliths", parse_generic),
    "awakening": ("https://aion2hub.com/database/awakening", parse_generic),
    "world-bosses": ("https://aion2hub.com/tools/world-bosses", parse_generic),
    "titles": ("https://aion2hub.com/database/titles", parse_generic),
}


def run(name):
    url, fn = DATASETS[name]
    body = fetch(url)
    if not body:
        print(name, "FETCH FAILED")
        return
    data = fn(body)
    os.makedirs(OUTDIR, exist_ok=True)
    p = os.path.join(OUTDIR, name + ".json")
    json.dump(data, open(p, "w", encoding="utf-8"), separators=(",", ":"), ensure_ascii=False)
    print(f"{name}: {len(data)} rows -> {p}")


if __name__ == "__main__":
    names = sys.argv[1:] or list(DATASETS)
    for n in names:
        run(n)
