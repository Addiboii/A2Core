#!/usr/bin/env python3
"""Scrape AION2 instance (dungeon) drop tables from aion2hub.com.

Each instance page is a drop table: difficulty tiers, entry cost, pity ceiling, boss
list with levels, and the item-drop % per clear from the clear-reward cube.
Output: data/aion2hub/instances.json
"""
import json, os, re, time, html, urllib.request, urllib.error

UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}
HERE = os.path.dirname(os.path.abspath(__file__))
OUTDIR = os.path.join(HERE, "data", "aion2hub")
URLS = os.path.join(OUTDIR, "instance-urls.json")
OUT = os.path.join(OUTDIR, "instances.json")


def fetch(url, tries=4):
    for t in range(tries):
        try:
            r = urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=30)
            return r.status, r.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return 404, ""
            time.sleep(1.5 * (t + 1))
        except Exception:
            time.sleep(1.5 * (t + 1))
    return 0, ""


def txt(s):
    return html.unescape(re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", s))).strip()


# Drop row: <a href="/database/items/ID"> ...<img>... <span class="truncate" style="color:#HEX">NAME</span></a>
#           <span class="shrink-0 tabular-nums ...">PCT%</span>
DROP = re.compile(
    r'href="/database/items/(\d+)".*?'
    r'<span class="truncate"[^>]*(?:color:(#[0-9a-fA-F]{3,8}))?[^>]*>([^<]+)</span>\s*</a>\s*'
    r'<span class="shrink-0 tabular-nums[^"]*">\s*([\d.]+)%',
    re.S,
)
NOCOMMENT = re.compile(r"<!--.*?-->")


GRADE_HEX = {  # aion2hub grade colors -> name
    "#9ca3af": "Common", "#b6b6b6": "Common", "#ffffff": "Common",
    "#4ade80": "Rare", "#22c55e": "Rare",
    "#4a9bf2": "Heroic", "#3b82f6": "Heroic", "#60a5fa": "Heroic",
    "#c084fc": "Unique", "#a855f7": "Unique",
    "#f59e0b": "Epic", "#fbbf24": "Epic", "#f0a020": "Epic",
    "#ef4444": "Legendary", "#fb7185": "Legendary",
}


def parse(url, body):
    slug = url.rstrip("/").split("/")[-1]
    body = NOCOMMENT.sub("", body)  # drop React comment nodes that split numbers
    rec = {"slug": slug, "url": url}
    m = re.search(r"<h1[^>]*>(.*?)</h1>", body, re.S)
    if m:
        rec["name"] = txt(m.group(1))
    flat = txt(body)
    # metadata
    m = re.search(r"Entry cost:\s*([\d,]+)\s*tickets?", flat)
    if m:
        rec["entryTickets"] = int(m.group(1).replace(",", ""))
    m = re.search(r"Pity ceiling:\s*([\d,]+)\s*clears?", flat)
    if m:
        rec["pityClears"] = int(m.group(1).replace(",", ""))
    m = re.search(r"(Easy(?:\s+Normal)?(?:\s+Hard)?(?:\s+Extreme)?)\s*(?:·|Korea|$)", flat)
    if m:
        rec["difficulties"] = m.group(1).split()
    # bosses with level ranges, e.g. "Kromede's Desire Lv 37 –63"
    bosses = []
    for bm in re.finditer(r"([A-Z][A-Za-z'’ ]{2,40}?)\s*Lv\s*(\d+)\s*[–-]\s*(\d+)", flat):
        nm = re.sub(r"^(?:Bosses?|Final Boss|Mobs?)\s+", "", bm.group(1).strip())
        if nm and nm not in ("Boss", "Final Boss") and len(nm) > 2:
            bosses.append({"name": nm, "levelMin": int(bm.group(2)), "levelMax": int(bm.group(3))})
    if bosses:
        # dedupe
        seen = {}
        for b in bosses:
            seen[b["name"]] = b
        rec["bosses"] = list(seen.values())
    # drop table: item id -> name + grade + % chance (take max % seen per item)
    drops = {}
    i = body.find("Drop Table")
    region = body[i:] if i >= 0 else body
    for iid, hexcol, name, pct in DROP.findall(region):
        iid = int(iid)
        p = float(pct)
        if iid not in drops or p > drops[iid]["pct"]:
            d = {"id": iid, "name": txt(name), "pct": p}
            g = GRADE_HEX.get((hexcol or "").lower())
            if g:
                d["grade"] = g
            drops[iid] = d
    if drops:
        rec["drops"] = sorted(drops.values(), key=lambda d: -d["pct"])
    else:
        # no % drop table (e.g. Bakron Island) — fall back to the JSON-LD gear list (no chances)
        items = []
        for m in re.finditer(r'"name":"([^"]+)","url":"https://aion2hub\.com/database/items/(\d+)"', body):
            items.append({"id": int(m.group(2)), "name": txt(m.group(1))})
        if items:
            # dedupe by id
            seen = {}
            for it in items:
                seen[it["id"]] = it
            rec["gearList"] = list(seen.values())
    return rec


def main():
    urls = json.load(open(URLS, encoding="utf-8"))
    out = []
    for i, u in enumerate(urls):
        st, body = fetch(u)
        if st == 200 and body:
            rec = parse(u, body)
            out.append(rec)
            print(f"{i+1}/{len(urls)} {rec.get('name','?')}: {len(rec.get('drops',[]))} drops, {len(rec.get('bosses',[]))} bosses", flush=True)
        else:
            print(f"{i+1}/{len(urls)} {u} -> {st}", flush=True)
        time.sleep(0.3)
    os.makedirs(OUTDIR, exist_ok=True)
    json.dump(out, open(OUT, "w", encoding="utf-8"), separators=(",", ":"), ensure_ascii=False)
    print(f"\nDONE {len(out)} instances -> {OUT}")


if __name__ == "__main__":
    main()
