"""Skill summaries of every class from the gamers4.life skill list (one public page, server-rendered) ->
research/g4l-skills.json: id, name, class, type, damage type (magic/physic), categories (attack/debuff/buff...), targeting,
cooldown, range, stigma. The second, thinner layer of tools/skill_guide.py (all 9 classes; shugo.gg has the details)."""
import json, os, re, sys, urllib.request
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
URL = "https://gamers4.life/aion-2/database/en/skill/"
src = sys.argv[1] if len(sys.argv) > 1 else None
html = open(src, encoding="utf-8").read() if src else urllib.request.urlopen(urllib.request.Request(URL, headers={"User-Agent": "Mozilla/5.0"}), timeout=60).read().decode("utf-8")
text = html.replace('\\"', '"')
skills = {}
for m in re.finditer(r'\{"id":"(\d{8})","name":"(.*?)","icon":"[^"]*","grade":\d+,"mainCategory":"(\w+)","subCategory":"(\w+)",'
                     r'"type":"(\w+)","damageType":"(\w*)","maxSkillLevel":(\d+),"requiredWeapons":\[(.*?)\],"targetProcessType":"(\w*)",'
                     r'"isStigmaSkill":(true|false),"skillCategories":\[(.*?)\],"cooldown":(\d+),"cooldownMin":[^,]*,"range":(\d+)', text):
    sid, name, cls, sub, typ, dmg, lvmax, weapons, target, stigma, cats, cd, rng = m.groups()
    skills[sid] = {"name": name, "class": cls, "sub": sub, "type": typ, "damage_type": dmg, "lv_max": int(lvmax),
                   "weapons": re.findall(r'"(\w+)"', weapons), "target": target, "stigma": stigma == "true",
                   "categories": re.findall(r'"(\w+)"', cats), "cooldown_ms": int(cd), "range_cm": int(rng)}
out = os.path.join(ROOT, "research", "g4l-skills.json")
json.dump({"source": URL, "skills": skills}, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=0)
from collections import Counter
print(len(skills), "skills ->", out)
print(Counter(s["class"] for s in skills.values()))
print(Counter(s["damage_type"] for s in skills.values()), Counter(c for s in skills.values() for c in s["categories"]))
