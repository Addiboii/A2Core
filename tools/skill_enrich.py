#!/usr/bin/env python3
"""Enrich skill-guide.json from each skill's own description, filling structured fields that the
generator missed (USER 2026-11-08: make all skills work as described). Idempotent: only fills a
field when it is missing/empty, never overwrites a good value. The server already consumes all of
these (GuideMpRestore, GuideLifesteal, GuideTargets, GuideCrowdControl, GuideDebuff).

Fills: mp_restore, lifesteal_percent, area{shape,radius_m}+targets, effects[{effect,chance,duration_ms}],
       debuff{duration_ms,stats{Stat:pct}} (enemy stat reductions / weaknesses).
Run on the server (reads/writes tools/data/skill-guide.json). Prints a per-field fill count.
"""
import json, os, re, sys

sys.stdout.reconfigure(encoding="utf-8")
BASE = r"G:\A2Core-project\aion\A2Core\tools\data"
if not os.path.isdir(BASE):
    BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data")
P = os.path.join(BASE, "skill-guide.json")
root = json.load(open(P, encoding="utf-8"))
sk = root.get("skills", root)

CC = ["Stun", "Root", "Bind", "Snare", "Slow", "Sleep", "Fear", "Silence", "Paralysis",
      "Knockdown", "Airborne", "Taunt", "Frost", "Stone", "Grab", "Seal"]
STAT = {  # description stat words -> internal stat name used by GuideDebuff/MonsterStat
    "defense": "Defense", "attack": "Attack", "move speed": "MoveSpeed", "movement speed": "MoveSpeed",
    "speed": "MoveSpeed", "tolerance": "Tolerance", "accuracy": "Accuracy", "evasion": "Evasion",
}
fills = {k: 0 for k in ("mp", "lifesteal", "area", "targets", "effects", "debuff")}

for sid, v in sk.items():
    desc = v.get("desc", "")
    low = desc.lower()

    # MP restore
    if not v.get("mp_restore"):
        m = re.search(r"restores?\s+(\d+)\s*mp", low)
        if m:
            v["mp_restore"] = int(m.group(1)); fills["mp"] += 1

    # lifesteal: "Absorbs N% ... HP"
    if not v.get("lifesteal_percent"):
        m = re.search(r"absorb\w*\s+([\d.]+)%\s*(?:of\s+)?hp", low)
        if m:
            v["lifesteal_percent"] = int(round(float(m.group(1)))); fills["lifesteal"] += 1

    # AoE: "up to N enemies [within Xm] [of the target]"
    m = re.search(r"up to (\d+)\s+enemies(?:\s+within\s+([\d.]+)\s*m)?(\s+of\s+(?:the\s+)?target)?", low)
    if m:
        n = int(m.group(1))
        if not v.get("targets") or v.get("targets", 0) < 2:
            v["targets"] = n; fills["targets"] += 1
        if not (isinstance(v.get("area"), dict) and v["area"].get("shape")):
            # radius: stated, else the skill's range if small (melee), else a 5 m default
            radius = float(m.group(2)) if m.group(2) else (v.get("range_m") if (v.get("range_m") or 0) and v["range_m"] <= 6 else 5)
            shape = "target" if m.group(3) else "self"
            v["area"] = {"shape": shape, "radius_m": radius}; fills["area"] += 1

    # crowd-control effects: "N% chance to inflict X for Ys" OR "Inflicts X for Ys"
    effects = v.get("effects") or []
    have = {e.get("effect") for e in effects if isinstance(e, dict)}
    for em in re.finditer(r"(?:(\d+)%\s*chance\s*to\s*)?inflict(?:s|ing)?\s+([A-Za-z]+)(?:\s+for\s+([\d.]+)\s*s)?", desc, re.I):
        eff = em.group(2).capitalize()
        if eff in CC and eff not in have:
            chance = int(em.group(1)) if em.group(1) else 100
            dur = int(float(em.group(3)) * 1000) if em.group(3) else 0
            effects.append({"effect": eff, "chance": chance, "duration_ms": dur})
            have.add(eff); fills["effects"] += 1
    if effects:
        v["effects"] = effects

    # weaknesses: "Reduces (enemy) (PvE/PvP) STAT by N% [for Ys]"
    if not v.get("debuff"):
        stats = {}
        dur = 0
        for dm in re.finditer(r"(?:reduc\w+|lower\w+|decreas\w+)\s+(?:enemy\s+|the\s+target'?s?\s+|target\s+)?(?:pve\s+|pvp\s+)?([a-z ]+?)\s+by\s+([\d.]+)%(?:\s+for\s+([\d.]+)\s*s)?", low):
            word = dm.group(1).strip()
            key = next((STAT[k] for k in STAT if word.endswith(k) or word == k), None)
            if key:
                stats[key] = -abs(float(dm.group(2)))
                if dm.group(3):
                    dur = max(dur, int(float(dm.group(3)) * 1000))
        if stats:
            v["debuff"] = {"duration_ms": dur or 10000, "stats": stats}; fills["debuff"] += 1

json.dump(root, open(P, "w", encoding="utf-8"), ensure_ascii=False, indent=0)
print("enriched skill-guide.json. fields filled:")
for k, n in fills.items():
    print(f"  {k:10} +{n}")
print("->", P)
