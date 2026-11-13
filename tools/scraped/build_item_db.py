#!/usr/bin/env python3
"""Merge scraped aion2hub item data + shugo stat-meta + icon map into one file the
staff dashboard serves: web/public/item-db.json

Each item: id, name, grade, category, slot, type, level, requiresLevel, bind,
  params:   [{label, key, value, pct}]            # base parameters (stat-meta normalised)
  soul:     {choose, subs:[{label,key,range,pct}]} # Soul Imprint random sub-stats
  sockets:  {manastone, godstone}                  # theostone/godstone sockets
  enchant:  {max, atMax}                            # enhancement at max
  exceed:   {max, atMax}                            # breakthrough at max
  sell, obtain, icon
"""
import json, os, re

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "data")
ITEMS = os.path.join(DATA, "aion2hub-items.json")
META = os.path.join(DATA, "stat-meta.json")
ICONS = os.path.join(HERE, "..", "web", "public", "item-icons-map.json")
OUT = os.path.join(HERE, "..", "web", "public", "item-db.json")


def load(p, default):
    try:
        return json.load(open(p, encoding="utf-8"))
    except Exception:
        return default


def main():
    items = load(ITEMS, {})
    meta = load(META, {"byLabel": {}})
    byLabel = meta.get("byLabel", {})
    icons = load(ICONS, {})

    def norm(label):
        m = byLabel.get(label)
        return (m["key"] if m else None, bool(m and m["pct"]))

    out = {}
    for sid, it in items.items():
        rec = {"id": it["id"], "name": it.get("name", f"Item {sid}")}
        for k in ("grade", "category", "slot", "type", "bind"):
            if it.get(k):
                rec[k] = it[k]
        if "itemLevel" in it:
            rec["level"] = it["itemLevel"]
        if "requiresLevel" in it:
            rec["req"] = it["requiresLevel"]
        if it.get("stats"):
            params = []
            for s in it["stats"]:
                key, pct = norm(s["stat"])
                params.append({"label": s["stat"], "key": key, "value": s["value"], "pct": pct})
            rec["params"] = params
        if it.get("soulImprint"):
            subs = []
            for s in it["soulImprint"]:
                key, pct = norm(s["stat"])
                subs.append({"label": s["stat"], "key": key, "range": s["range"], "pct": pct})
            rec["soul"] = {"choose": it.get("soulImprintChoose"), "subs": subs}
        ms, gs = it.get("manastoneSockets"), it.get("godstoneSockets")
        if ms or gs:
            rec["sockets"] = {"manastone": ms or 0, "godstone": gs or 0}
        if it.get("enchant"):
            rec["enchant"] = it["enchant"]
        if it.get("exceed"):
            rec["exceed"] = it["exceed"]
        if it.get("sellPrice"):
            rec["sell"] = it["sellPrice"]
        if it.get("obtain"):
            rec["obtain"] = it["obtain"]
        if icons.get(sid):
            rec["icon"] = icons[sid]
        out[sid] = rec

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    json.dump(out, open(OUT, "w", encoding="utf-8"), separators=(",", ":"), ensure_ascii=False)
    size = os.path.getsize(OUT)
    withparams = sum(1 for r in out.values() if r.get("params"))
    print(f"items {len(out)}  with-params {withparams}  ->  {OUT}  ({size//1024} KB)")


if __name__ == "__main__":
    main()
