"""Class skill guide -> tools/data/skill-guide.json (the server's per-skill rules: areas, targets, chance effects, debuffs, DoT...).

Source: the shugo.gg class skill books (https://shugo.gg/data/skills/<class>.json, saved by the user into research/shugo-skills/,
generated 2026-11-04 from the game's own skill text). The client's skill tables (Skill.dat/SkillEffect.dat) would be the exact
source but need the archive key; until then this is the reference. Every value parsed from the description text is marked
by where it came from; numbers per skill level come from the book's own arrays.

Usage: python tools/skill_guide.py            (reads research/shugo-skills/*.json, writes tools/data/skill-guide.json)
"""
import glob
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "research", "shugo-skills")
OUT = os.path.join(ROOT, "tools", "data", "skill-guide.json")

# Crowd control and status names the descriptions use (the abnormal kind the server applies).
CC = ["Stun", "Knockdown", "Airborne", "Root", "Seal", "Fear", "Sleep", "Silence", "Bind", "Frost", "Stagger", "Grab",
      "Blind", "Snare", "Slow", "Stone", "Paralysis", "Poison", "Bleed", "Burn", "Shock", "Wounded", "Charm", "Confusion"]
CC_RE = "|".join(CC)


def num(s):
    return float(s) if "." in s else int(s)


def parse(desc):
    """Rules from one skill description (the first paragraph is the skill; later ones are notes like stagger)."""
    r = {}
    text = desc.replace("\n", " ")
    m = re.search(r"up to (\d+) enem\w+", text)
    if m: r["targets"] = int(m.group(1))
    m = re.search(r"within ([\d.]+)m of the caster", text)
    if m: r["area"] = {"shape": "caster", "radius_m": num(m.group(1))}
    m = re.search(r"within ([\d.]+)m of the target", text)
    if m: r["area"] = {"shape": "target", "radius_m": num(m.group(1))}
    m = re.search(r"[Rr]ushes forward ([\d.]+)m", text)
    if m:
        r["area"] = {"shape": "path", "length_m": num(m.group(1))}
        r["move"] = {"kind": "dash", "distance_m": num(m.group(1))}
    m = re.search(r"(?:Rushes|Moves|Leaps|Charges|Teleports) to a target within ([\d.]+)m", text)
    if m: r["move"] = {"kind": "to_target", "range_m": num(m.group(1))}
    if "area" not in r and "targets" in r:
        m = re.search(r"up to \d+ enem\w+ within ([\d.]+)m", text)
        if m: r["area"] = {"shape": "target" if "Selects a target" in text or "of the target" in text else "front", "radius_m": num(m.group(1))}
        elif "nearby" in text: r["area"] = {"shape": "target", "radius_m": 4}
    m = re.search(r"(?:a target|enemies|target) within ([\d.]+)m", text)
    if m: r["range_m"] = num(m.group(1))
    # chance effects: "with a 50% chance to inflict Root for 3s on Block", "inflicts Knockdown for 3s"
    effects = []
    for m in re.finditer(r"(?:(\d+)% chance to )?inflicts? (" + CC_RE + r")(?: for ([\d.]+)s)?(?: (on Block|on hit|on Parry))?", text):
        e = {"effect": m.group(2), "chance": int(m.group(1)) if m.group(1) else 100}
        if m.group(3): e["duration_ms"] = int(float(m.group(3)) * 1000)
        if m.group(4): e["when"] = m.group(4).replace("on ", "").lower()
        effects.append(e)
    # one entry per status: the one that names a chance or duration wins over a bare mention of the same status
    best = {}
    for e in effects:
        if e["effect"] not in best or len(e) > len(best[e["effect"]]): best[e["effect"]] = e
    if best: r["effects"] = list(best.values())
    # debuffs on the target: "reduces Defense by 30% and Incoming Heal by 50% for 10s", "inflicts Slow, which reduces Move Speed
    # by 40% for 5s", "Inflicts Wounded for 10s on hit, reducing the enemy's Attack by 10%", "Reduces enemy PvE Damage Boost by 5%"
    stats, dur = {}, 0
    for sent in re.split(r"(?<=[.])\s+(?=[A-Z])", text):
        if re.search(r"\b(caster|party members|cooldowns?|Bow range)\b", sent) and not re.search(r"enemy|target", sent): continue
        found = False
        for m in re.finditer(r"(?i)(?:reduc|decreas|lower)\w*,?\s+(?:which reduces\s+)?(?:enemy |their |the enemy's |its |the target's )?(.+?)(?= for \d|,? and increases|[.](?!\d)|$)", sent):
            for k, v in re.findall(r"([A-Za-z][A-Za-z ]*?) by (\d+(?:\.\d+)?)%", m.group(1)):
                k = re.sub(r"^(and |enemy |their |s )", "", k.strip())
                if "cooldown" in k.lower() or "range" in k.lower(): continue
                stats[k] = -float(v) if "." in v else -int(v); found = True
        if found:
            d = re.search(r"for ([\d.]+)s", sent)
            if d: dur = max(dur, int(float(d.group(1)) * 1000))
    if stats:
        if not dur:
            d = re.search(r"(?:inflicts?|Inflicts?) \w+(?: \w+)? for ([\d.]+)s", text)
            dur = int(float(d.group(1)) * 1000) if d else 5000
        r["debuff"] = {"stats": stats, "duration_ms": dur}
    # damage over time: "Damage over Time of 233-233 every 1s for 6s", "43-43 Fire damage every 1s for 6s",
    # "Takes 578-578 Fire Damage over Time every 1s" (status lasting "for 10s"), "Deals 207-207 Damage over Time ... for 10s"
    if re.search(r"Damage over Time|damage every [\d.]+s", text, re.I):
        dot = {"interval_ms": 1000}
        m = re.search(r"every ([\d.]+)s", text)
        if m: dot["interval_ms"] = int(float(m.group(1)) * 1000)
        m = re.search(r"(?:Damage over Time|damage every [\d.]+s)[^.]*? for ([\d.]+)s", text, re.I) or re.search(r"for ([\d.]+)s", text)
        dot["duration_ms"] = int(float(m.group(1)) * 1000) if m else 10000
        m = re.search(r"(\d+)-(\d+) (?:\w+ )?(?:Damage over Time|damage every)", text, re.I) or re.search(r"Damage over Time of (\d+)-(\d+)", text)
        if m: dot["amount"] = [int(m.group(1)), int(m.group(2))]
        r["dot"] = dot
    m = re.search(r"\b(Fire|Water|Earth|Wind)(?: [Dd]amage| Damage over Time)", text)
    if m: r["element"] = m.group(1)
    m = re.search(r"absorbs (\d+)% HP", text)
    if m: r["lifesteal_percent"] = int(m.group(1))
    m = re.search(r"Restores (\d+) MP", text)
    if m: r["mp_restore"] = int(m.group(1))
    m = re.search(r"target afflicted with (" + CC_RE + ")", text)
    if m: r["requires_target"] = m.group(1)
    m = re.search(r"Stagger Gauge Damage", desc)
    m2 = re.search(r"(\d+) Stagger Gauge Damage", desc)
    if m2: r["stagger"] = int(m2.group(1))
    if "increases Enmity" in text or "generating Enmity" in text: r["enmity"] = True
    m = re.search(r"Deals damage (twice|\d+ times)", text) or re.search(r"(\d+) times, each dealing damage", text)
    if m: r["repeat"] = 2 if m.group(1) == "twice" else int(re.sub(r"\D", "", m.group(1)))
    return r


def convert(path):
    book = json.load(open(path, encoding="utf-8"))
    cls = book.get("class") or os.path.splitext(os.path.basename(path))[0]
    out = {}
    for sid, s in book["skills"].items():
        e = {
            "class": cls, "name": s.get("name"), "type": s.get("type"), "lv_max": s.get("lvMax"),
            "cooldown_ms": int(round(float(s.get("cooldown") or 0) * 1000)), "cast_ms": int(round(float(s.get("cast") or 0) * 1000)),
            "range_m": s.get("range"), "cost": s.get("cost") or {}, "weapons": s.get("weapons") or [], "traits": s.get("traits") or [],
            "desc": s.get("desc"),
        }
        if s.get("subType"): e["sub_type"] = s["subType"]
        if s.get("byLevel"): e["cooldown_by_level_ms"] = {str(x["lv"]): int(round(float(x["cooldown"]) * 1000)) for x in s["byLevel"] if "cooldown" in x}
        if s.get("dmg"):
            dmg = s["dmg"]
            e["damage"] = {"kind": dmg.get("kind"), "hits": dmg.get("hits"), "ratio_percent": dmg.get("ratio"),
                           "min_by_level": dmg.get("min"), "max_by_level": dmg.get("max")}
        if s.get("parts"): e["specialties"] = [{"id": p["id"], "unlock_level": p.get("lv"), "desc": p.get("desc")} for p in s["parts"]]
        if s.get("learn"): e["learn"] = s["learn"]
        if s.get("learnLevels"): e["learn_levels"] = s["learnLevels"]
        e.update(parse(s.get("desc") or ""))
        if "dot" in e and e["dot"].get("amount") and s.get("tpl") and s.get("vals"):
            first = str(e["dot"]["amount"][0])
            # which placeholder pair of the template holds the DoT amount at level 1
            for i, pair in enumerate(zip(*[iter(range(len(s["vals"][0])))] * 2)):
                if str(s["vals"][0][pair[0]]) == first:
                    e["dot"]["amount_by_level"] = [int(float(v[pair[0]])) for v in s["vals"] if len(v) > pair[0]]
        if "dot" in e and e["dot"].get("amount") and "amount_by_level" not in e["dot"]:
            e["dot"]["amount_by_level"] = [e["dot"]["amount"][0]]   # no per-level table (a passive): the stated amount
        out[sid] = e
    return cls, out


def main():
    files = sorted(glob.glob(os.path.join(SRC, "*.json")))
    skills, classes = {}, []
    for f in files:
        cls, out = convert(f)
        classes.append(cls)
        skills.update(out)
    # The thinner layer for every class (gamers4.life skill list, tools/g4l_skills.py): damage type, categories, targeting.
    g4l = os.path.join(ROOT, "research", "g4l-skills.json")
    if os.path.exists(g4l):
        for sid, g in json.load(open(g4l, encoding="utf-8"))["skills"].items():
            e = skills.setdefault(sid, {"class": g["class"], "name": g["name"], "type": g["type"], "lv_max": g["lv_max"],
                                        "cooldown_ms": g["cooldown_ms"], "range_m": g["range_cm"] / 100, "weapons": g["weapons"]})
            e["damage_type"] = g["damage_type"]
            e["categories"] = g["categories"]
            e["target_process"] = g["target"]
            e["stigma"] = g["stigma"]
            if g["class"] not in classes: classes.append(g["class"])
    doc = {"source": "shugo.gg class skill books (research/shugo-skills, generated 2026-11-04 from the game's skill text); "
                     "areas, targets, chance effects and debuffs parsed from the descriptions (tools/skill_guide.py)",
           "classes": classes, "skills": skills}
    json.dump(doc, open(OUT, "w", encoding="utf-8"), ensure_ascii=False, indent=None, separators=(",", ":"))
    n = len(skills)
    def count(k): return sum(1 for s in skills.values() if k in s)
    print(f"{n} skills from {', '.join(classes)} -> {OUT}")
    for k in ("damage_type", "categories", "damage", "targets", "area", "move", "effects", "debuff", "dot", "element", "lifesteal_percent", "mp_restore", "requires_target", "specialties"):
        print(f"  {k:18} {count(k)}")


if __name__ == "__main__":
    main()
