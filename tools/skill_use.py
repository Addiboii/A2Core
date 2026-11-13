"""Player skill use limits from the client's own Skill table (tools/dump_tables.ps1 export, Table_Skill.json).

Writes tools/data/skill-use.json for the server's skill checks (GameSession.SkillChecks.cs):
  {"skills": {"<id>": [use range cm, use height range cm, group cooldown id, target kind]}}
target kind: 0 Self, 1 MainTarget, 2 MainSelf. Only player skills (ids 10,000,000-19,999,999).
All player skills have CastingTime 0 in this client (timed casts are SkillCharge rows), so no cast time is exported.

usage: python tools/skill_use.py <tables folder>   (e.g. C:/A2Core/research/local/tables-20261109-023531)
"""
import json
import os
import sys

KINDS = {"ESkillTargetProcessType::Self": 0, "ESkillTargetProcessType::MainTarget": 1, "ESkillTargetProcessType::MainSelf": 2}


def main(folder: str) -> None:
    with open(os.path.join(folder, "Table_Skill.json"), encoding="utf-8") as f:
        rows = json.load(f)["Properties"]["Data"]
    skills = {}
    for r in rows:
        sid = r["ID"]["Value"]
        if not 10_000_000 <= sid < 20_000_000:
            continue
        skills[str(sid)] = [int(r["NeedSkillUseRange"]), int(r["NeedSkillUseHeightRange"]), int(r["GroupCoolTimeId"] or 0),
                            KINDS.get(r["TargetProcessType"], 0)]
    out = {
        "source": f"client Table_Skill.json ({os.path.basename(os.path.normpath(folder))}): NeedSkillUseRange, NeedSkillUseHeightRange, "
                  "GroupCoolTimeId, TargetProcessType of the player skills; tools/skill_use.py",
        "skills": dict(sorted(skills.items(), key=lambda kv: int(kv[0]))),
    }
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "skill-use.json")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("{\n \"source\": " + json.dumps(out["source"]) + ",\n \"skills\": {\n")
        f.write(",\n".join(f"  \"{k}\": {json.dumps(v)}" for k, v in out["skills"].items()))
        f.write("\n }\n}\n")
    print(f"{len(skills)} player skills -> {path}")


if __name__ == "__main__":
    main(sys.argv[1])
