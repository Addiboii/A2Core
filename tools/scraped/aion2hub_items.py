#!/usr/bin/env python3
"""Scrape AION2 item data from aion2hub.com (not Cloudflare-blocked).

Pulls each item's base parameters, Soul Imprint random sub-stats, manastone/godstone
sockets, enchant (+15) and exceed (+5) bonuses, sell price, and how-to-obtain, into
tools/data/aion2hub-items.json for the staff dashboard item panel.

Usage:
  python aion2hub_items.py ids              # crawl list pages -> data/aion2hub-ids.json
  python aion2hub_items.py one 110120003    # parse one item, print JSON (test)
  python aion2hub_items.py crawl [start]    # fetch all detail pages -> aion2hub-items.json
"""
import json, os, re, sys, time, html, urllib.request, urllib.error

BASE = "https://aion2hub.com"
UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}
HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "data")
IDS_PATH = os.path.join(DATA, "aion2hub-ids.json")
OUT_PATH = os.path.join(DATA, "aion2hub-items.json")


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


ROW = re.compile(
    r'<li class="flex items-center justify-between[^"]*">'
    r'<span class="([^"]*)">(.*?)</span>'
    r'<span class="([^"]*)">(.*?)</span></li>',
    re.S,
)


def parse(itemid, body):
    it = {"id": int(itemid)}
    # name
    m = re.search(r"<h1[^>]*>(.*?)</h1>", body, re.S)
    if m:
        it["name"] = txt(m.group(1))
    # header pills (Item Lv / Requires Lv / slot / type / category)
    m = re.search(r"Item Lv\s*(\d+)", txt(body))
    if m:
        it["itemLevel"] = int(m.group(1))
    m = re.search(r"Requires Lv\s*(\d+)", txt(body))
    if m:
        it["requiresLevel"] = int(m.group(1))
    # rarity / grade appears as an uppercase pill near the top
    m = re.search(r"\b(Common|Rare|Heroic|Unique|Epic|Legendary|Mythic|Ancient)\b", txt(body[:4000]))
    if m:
        it["grade"] = m.group(1)
    head = txt(body)
    m = re.search(r"Requires Lv\s*\d+\s+([A-Za-z]+)\s+([A-Za-z ]+?)\s+(Weapons|Armor|Accessor\w+|Consumables?|Materials?|Skins?|Misc|Gathering|Quest|Cube|[A-Z][a-z]+)\b", head)
    if m:
        it["slot"] = m.group(1)
        it["type"] = m.group(2).strip()
        it["category"] = m.group(3)
    if "Bind on Pickup" in head:
        it["bind"] = "pickup"
    elif "Bind on Equip" in head:
        it["bind"] = "equip"

    # structured rows: base stats (gray) vs soul imprint (emerald)
    stats, soul = [], []
    for lab_cls, lab, val_cls, val in ROW.findall(body):
        label, value = txt(lab), txt(val)
        if not label:
            continue
        if "emerald" in lab_cls:
            soul.append({"stat": label, "range": value})
        elif "gray-400" in lab_cls:
            stats.append({"stat": label, "value": value})
    if stats:
        it["stats"] = stats
    if soul:
        it["soulImprint"] = soul
    m = re.search(r"Soul Imprint\s*[—-]\s*(\d+)\s+of these", head)
    if m:
        it["soulImprintChoose"] = int(m.group(1))

    # sockets + sell price
    m = re.search(r"Manastone sockets\s*(\d+)", head)
    if m:
        it["manastoneSockets"] = int(m.group(1))
    m = re.search(r"Godstone sockets\s*(\d+)", head)
    if m:
        it["godstoneSockets"] = int(m.group(1))
    m = re.search(r"Sell price\s*([\d,]+)\s*Gold", head)
    if m:
        it["sellPrice"] = int(m.group(1).replace(",", ""))

    # upgrades: enchant + exceed
    STOP = r"(?:Exceed|Choose|\d+ manastone|How to Obtain|Item #|Tools Rift|$)"
    m = re.search(r"Enchantable to \+\s*(\d+)\s*At \+\s*\d+\s*:\s*([^<]*?)" + STOP, head)
    if m:
        it["enchant"] = {"max": int(m.group(1)), "atMax": m.group(2).strip().rstrip(",")}
    m = re.search(r"Exceed \(breakthrough\) to \+\s*(\d+)\s*At \+\s*\d+\s*:\s*([^<]*?)(?:Choose|\d+ manastone|How to Obtain|Item #|Tools Rift|$)", head)
    if m:
        it["exceed"] = {"max": int(m.group(1)), "atMax": m.group(2).strip().rstrip(",")}

    # how to obtain (short)
    m = re.search(r"How to Obtain(.*?)(?:Item #|Tools Rift|Database Items|Maps World|Builds Community|Guides Release)", head)
    if m:
        ob = m.group(1).strip()
        if ob:
            it["obtain"] = ob[:400]
    return it


def crawl_ids():
    ids, page, prev = [], 1, None
    while True:
        st, body = fetch(f"{BASE}/database?page={page}")
        if st != 200:
            break
        found = re.findall(r"/database/items/(\d+)", body)
        found = list(dict.fromkeys(found))
        if not found or found == prev:
            break
        ids.extend(int(x) for x in found)
        prev = found
        print(f"page {page}: +{len(found)} (total {len(set(ids))})", flush=True)
        page += 1
        time.sleep(0.4)
    ids = sorted(set(ids))
    os.makedirs(DATA, exist_ok=True)
    json.dump(ids, open(IDS_PATH, "w"), separators=(",", ":"))
    print("TOTAL IDS", len(ids), "->", IDS_PATH)
    return ids


def crawl_all(start=0):
    ids = json.load(open(IDS_PATH)) if os.path.exists(IDS_PATH) else crawl_ids()
    out = {}
    if os.path.exists(OUT_PATH):
        out = json.load(open(OUT_PATH, encoding="utf-8"))
    done = set(out.keys())
    for i, iid in enumerate(ids):
        if i < start or str(iid) in done:
            continue
        st, body = fetch(f"{BASE}/database/items/{iid}")
        if st == 200 and body:
            out[str(iid)] = parse(iid, body)
        if i % 50 == 0:
            json.dump(out, open(OUT_PATH, "w", encoding="utf-8"), separators=(",", ":"), ensure_ascii=False)
            print(f"{i}/{len(ids)} parsed={len(out)}", flush=True)
        time.sleep(0.25)
    json.dump(out, open(OUT_PATH, "w", encoding="utf-8"), separators=(",", ":"), ensure_ascii=False)
    print("DONE", len(out), "->", OUT_PATH)


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "one"
    if cmd == "ids":
        crawl_ids()
    elif cmd == "one":
        iid = sys.argv[2] if len(sys.argv) > 2 else "110120003"
        st, body = fetch(f"{BASE}/database/items/{iid}")
        print(json.dumps(parse(iid, body), indent=2, ensure_ascii=False))
    elif cmd == "crawl":
        crawl_all(int(sys.argv[2]) if len(sys.argv) > 2 else 0)
