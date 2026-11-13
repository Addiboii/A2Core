#!/usr/bin/env python3
"""Skill audit: for every skill, compare what its DESCRIPTION says it does against what the
datasheet (skill-guide.json) + the class/behaviour tables represent, and report the gaps grouped
by cause. This drives the "make all skills work as described" rework (USER 2026-11-08).

Run on the server (reads tools/data/*.json). Prints a per-cause summary + examples, and writes
tools/data/skill-audit.json with the full per-skill findings.
"""
import json, os, re, sys

sys.stdout.reconfigure(encoding="utf-8")
BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data")
if not os.path.isdir(BASE):
    BASE = r"G:\A2Core-project\aion\A2Core\tools\data"

guide = json.load(open(os.path.join(BASE, "skill-guide.json"), encoding="utf-8"))
guide = guide.get("skills", guide)
cls = json.load(open(os.path.join(BASE, "class-skills.json"), encoding="utf-8")).get("skills", {})
beh = json.load(open(os.path.join(BASE, "skill-behaviour.json"), encoding="utf-8"))
beh = beh.get("skills", beh) if isinstance(beh, dict) else {}


def has_table_damage(sid):
    d = cls.get(sid) or beh.get(sid) or {}
    return bool(d.get("damage"))


def intents(v):
    """What the description says the skill should do."""
    desc = v.get("desc", "")
    low = desc.lower()
    it = set()
    if re.search(r"\bdeals?\b.*\bdamage\b|\d+-\d+\s+\w*\s*damage", low):
        it.add("damage")
    if re.search(r"up to \d+ enemies|enemies within|within \d+m of|enemies in", low) or (v.get("targets") or 0) and (v.get("targets") or 0) > 1:
        it.add("aoe")
    if re.search(r"\b(water|fire|earth|wind)\s+damage", low):
        it.add("element")
    if re.search(r"inflicts?\s+(root|slow|stun|bind|snare|sleep|fear|silence|paralysis|knockdown|airborne|frostbite|frost|embers|slow|taunt|insignia|poison|bleed)", low) or "over time" in low:
        it.add("debuff")
    if re.search(r"reduc\w+|lower\w+|decreas\w+", low) and re.search(r"defense|attack|speed|resist|tolerance|accuracy|evasion", low):
        it.add("weakness")
    if re.search(r"over time every|damage over time", low):
        it.add("dot")
    if re.search(r"increas\w+ (the caster|caster's|your|own|ally|party|allies|spirit|summoned)|grants?\s+\w+ (status|buff)|applies?.*(to the caster|to allies|to the party)", low):
        it.add("buff")
    if "summon" in low:
        it.add("summon")
    if re.search(r"restores?\s+[\d\-]+\s*hp|\bheals?\b", low):
        it.add("heal")
    if re.search(r"restores?\s+[\d\-]+\s*mp", low):
        it.add("mp")
    if re.search(r"absorb\w*\s+[\d\.]+%?\s*(of\s+)?hp|lifesteal", low):
        it.add("lifesteal")
    if "Charge Skill" in (v.get("traits") or []) or "charge level" in low:
        it.add("charge")
    return it


def represented(sid, v):
    """What the datasheet currently represents for this skill."""
    r = set()
    if has_table_damage(sid) or ((v.get("damage") or {}).get("min_by_level")):
        r.add("damage")
    if v.get("area") and (v.get("targets") or 0) > 1:
        r.add("aoe")
    if v.get("element"):
        r.add("element")
    if v.get("effects"):
        r.add("debuff")
    if v.get("debuff"):  # stat reductions
        r.add("weakness")
    if v.get("dot"):
        r.add("dot")
    # buff / summon: no datasheet field exists yet
    if v.get("mp_restore"):
        r.add("mp")
    if v.get("lifesteal_percent"):
        r.add("lifesteal")
    return r


gaps = {}       # cause -> list of (sid, class, name)
per_skill = {}
for sid, v in guide.items():
    want = intents(v)
    have = represented(sid, v)
    missing = want - have
    # a charge skill whose aoe is 'represented' in data still needs the charge path (flag separately)
    if "charge" in want and "aoe" in want:
        missing.add("charge-aoe")
    if missing:
        per_skill[sid] = {"class": v.get("class"), "name": v.get("name"), "want": sorted(want), "missing": sorted(missing)}
        for m in missing:
            gaps.setdefault(m, []).append((sid, v.get("class"), v.get("name")))

print(f"audited {len(guide)} skills; {len(per_skill)} have at least one gap\n")
print("GAPS BY CAUSE (skills where the description implies it but the datasheet/handler does not):")
for cause in sorted(gaps, key=lambda c: -len(gaps[c])):
    rows = gaps[cause]
    ex = ", ".join(f"{n}" for _, _, n in rows[:4])
    print(f"  {cause:12} {len(rows):4}   e.g. {ex}")

out = os.path.join(BASE, "skill-audit.json")
json.dump({"gaps_by_cause": {k: [s for s, _, _ in v] for k, v in gaps.items()}, "per_skill": per_skill},
          open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(f"\nfull report -> {out}")
