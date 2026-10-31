"""Recovered creation/class mapping and level-one skill initialization.

Source tables are offline WorldProbe exports under research/local. The small
derived catalog can be rebuilt with `python tools/starter_skills.py`.
Wire opcodes below come from the official capture, not usmap ordinal guesses.
"""
import json
import struct
from functools import lru_cache
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "tools/data/starter-skills.json"
PC_CONTEXT_STACKS = ROOT / "tools/data/pc-context-stacks.json"
PC_BAR_CONTEXT_STACKS = ROOT / "tools/data/pc-bar-context-stacks.json"


def _short(value):
    return str(value).split("::")[-1]


def _skill_id(value):
    if isinstance(value, dict) and "Value" in value:
        return int(value["Value"])
    return int(value)


def context_skill_ids_from_row(row):
    """Skill ids for QuickSlotInfo._context_skill_ids from a PCContextSkillSlot row."""
    # usmap PCContextSkillSlotTableItem: ID, QuickSlotId, Class, DefaultSkillId, ContextChangeSkillList, bEditable. The other
    # names are guesses from before the struct was read and match nothing in this build.
    for key in (
        "ContextChangeSkillList",
        "ContextSkillIdList",
        "ContextSkillIds",
        "DefaultContextSkillIdList",
        "SkillOrderList",
        "ContextSkillDataList",
    ):
        if key not in row:
            continue
        raw = row[key]
        if not isinstance(raw, list):
            continue
        ids = []
        for entry in raw:
            if isinstance(entry, dict):
                if "SkillId" in entry:
                    ids.append(_skill_id(entry["SkillId"]))
                elif "Value" in entry:
                    ids.append(_skill_id(entry))
                else:
                    for v in entry.values():
                        if isinstance(v, (int, dict)):
                            ids.append(_skill_id(v))
                            break
            elif isinstance(entry, int):
                ids.append(entry)
        ids = [i for i in ids if i > 0]
        if ids:
            return ids
    for key, raw in row.items():
        if "Context" not in key or "Skill" not in key or not isinstance(raw, list):
            continue
        ids = [_skill_id(x) for x in raw if x]
        ids = [i for i in ids if i > 0]
        if ids:
            return ids
    return []


def build_e_slot_context_stacks(source):
    """Per-class E-key (QuickSlotId 8) priority stacks from PCContextSkillSlot table-005."""
    rows = json.loads((source / "table-005.json").read_text(encoding="utf-8"))["Properties"]["Data"]
    stacks = {}
    for row in rows:
        if int(row.get("QuickSlotId") or 0) != 8:
            continue
        cls = _short(row["Class"])
        ids = context_skill_ids_from_row(row)
        if ids:
            stacks[cls] = ids
    return stacks


def _context_list_for_row(row):
    """Up to four skill ids for QuickSlotInfo._context_skill_ids (DefaultSkillId first)."""
    default = _skill_id(row["DefaultSkillId"])
    ids = context_skill_ids_from_row(row)
    if not ids:
        return [default]
    ordered = [default]
    for skill_id in ids:
        if skill_id != default and skill_id not in ordered:
            ordered.append(skill_id)
    return ordered[:4]


def build_bar_context_by_default_skill(source):
    """PCContextSkillSlot DefaultSkillId -> ContextChangeSkillList for every quickslot row."""
    rows = json.loads((source / "table-005.json").read_text(encoding="utf-8"))["Properties"]["Data"]
    by_default = {}
    for row in rows:
        default = _skill_id(row["DefaultSkillId"])
        if default <= 0:
            continue
        ctx = _context_list_for_row(row)
        if len(ctx) >= 2 or ctx != [default]:
            by_default[str(default)] = ctx
    return by_default


def wire3_bar_context_fallback(starter=None):
    """OURS: wire hotbar slot 3 stacks DefaultSkillId with the slot 4 default (Assassin Ambush + Flash Slice)."""
    starter = starter or json.loads(CATALOG.read_text(encoding="utf-8"))
    by_default = {}
    for profile in starter.values():
        bar = {slot: skill for slot, skill in profile.get("bar") or []}
        primary, alternate = bar.get(3), bar.get(4)
        if not primary or not alternate or primary == alternate:
            continue
        by_default[str(primary)] = [primary, alternate]
    return by_default


def build_catalog(source):
    def rows(index):
        return json.loads((source / f"table-{index:03}.json").read_text(encoding="utf-8"))["Properties"]["Data"]
    creations, pcs, acquire, slots = rows(0), rows(1), rows(2), rows(5)
    result = {}
    for creation in creations:
        cls, race = creation["Class"], creation["Race"]
        skills = sorted({r["SkillId"]["Value"] for r in acquire
                         if r["ClassType"] in (cls, "ECharacterClass::None")
                         and r["bAutoLearn"] and r["SkillLevel"] == 1
                         and r["NeedCharacterLevel"] <= 1
                         and r["NeedAscensionGrade"] == "EAscensionGrade::None"
                         and not r["NeedPrevSkillDataList"]
                         and not r["CostItemDataList"]
                         and not r["CostSkillPoint"] and not r["CostStigmaPoint"]
                         and not r["CostStigmaSuperiorPoint"]})
        for pc in pcs:
            if (pc["Class"], pc["Race"]) != (cls, race):
                continue
            gender = {"EGender::Male": 1, "EGender::Female": 2}[pc["Gender"]]
            result[f'{creation["ID"]["Value"]}:{gender}'] = {
                "pc_id": pc["ID"]["Value"], "class": cls.split("::")[-1],
                "race": 1 if race == "ERace::Light" else 2,
                "resource": pc["HudSkillCostType"].split("::")[-1],
                "skills": skills,
                "slots": [[r["QuickSlotId"] - 1, r["DefaultSkillId"]["Value"]]
                          for r in slots if r["Class"] == cls
                          and r["DefaultSkillId"]["Value"] in skills],
                # PCContextSkillSlot: the fixed skill bar (QuickSlotId 1-8) of the class; a slot shows
                # its DefaultSkillId once that skill is learned (the level-one subset is "slots").
                "bar": [[r["QuickSlotId"] - 1, r["DefaultSkillId"]["Value"]]
                        for r in slots if r["Class"] == cls and r["DefaultSkillId"]["Value"]
                        and 1 <= r["QuickSlotId"] <= 8],
            }
    return result


@lru_cache(maxsize=1)
def _catalog():
    return json.loads(CATALOG.read_text(encoding="utf-8"))


def starter_profile(creation_id, gender):
    return _catalog().get(f"{creation_id}:{gender}")


def build_skill_list(skills):
    """0x5100: count + ready entries (observed flag 1 layout).

    Each entry is flag, i32 ID, level, original level, nine zero bytes.
    `skills` holds skill IDs (level 1) or (skill ID, skill level) pairs.
    The flag-5 autoload variant seen in the sample has an additional byte;
    this initializer only publishes ready entries, without autoload state.
    """
    entries = [(s, 1) if isinstance(s, int) else tuple(s) for s in skills]
    ids = [skill_id for skill_id, _ in entries]
    if len(entries) > 127 or len(set(ids)) != len(ids):
        raise ValueError("Expected at most 127 unique skills")
    out = bytearray(b"\x00\x51" + bytes([len(entries)]))
    for skill_id, level in entries:
        if not 0 < skill_id <= 0x7fffffff or not 1 <= level <= 255:
            raise ValueError("Invalid skill ID or level")
        out += b"\x01" + struct.pack("<iBB", skill_id, level, level) + bytes(9)
    return bytes(out)


def build_skill_slots(slots):
    """0x5600 observed 42-byte skill-slot records, with four context IDs.

    Sample slots 0/1 contain tutorial skills 9865/9868. Only owned starter
    skills are installed; unlearned higher-level defaults are not granted.
    """
    if len(slots) > 127:
        raise ValueError("Too many slots")
    out = bytearray(b"\x00\x56" + bytes([len(slots)]))
    for slot, skill in slots:
        out += bytes([1, slot, 0])  # record marker, zero-based slot, Skill
        out += struct.pack("<qii", 0, 0, skill)
        out += b"\x04" + struct.pack("<4i", skill, 0, 0, 0)
        out += bytes(6)  # macro, social action, toggle
    return bytes(out)


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--tables",
        type=Path,
        default=ROOT / "research/local/starter-skills-tables",
        help="WorldProbe export with table-000..007",
    )
    args = parser.parse_args()
    CATALOG.parent.mkdir(exist_ok=True)
    CATALOG.write_text(json.dumps(build_catalog(args.tables), indent=2) + "\n", encoding="utf-8")
    if (args.tables / "table-005.json").is_file():
        stacks = build_e_slot_context_stacks(args.tables)
        bar_ctx = build_bar_context_by_default_skill(args.tables)
        if stacks:
            PC_CONTEXT_STACKS.write_text(
                json.dumps(
                    {
                        "source": f"PCContextSkillSlot table-005.json under {args.tables}",
                        "quick_slot_id": 8,
                        "wire_slot": 7,
                        "real_export": True,
                        "e_slot_by_class": stacks,
                    },
                    indent=2,
                )
                + "\n",
                encoding="utf-8",
            )
        if bar_ctx:
            PC_BAR_CONTEXT_STACKS.write_text(
                json.dumps(
                    {
                        "source": f"PCContextSkillSlot ContextChangeSkillList from {args.tables}/table-005.json",
                        "real_export": True,
                        "by_default_skill": bar_ctx,
                    },
                    indent=2,
                )
                + "\n",
                encoding="utf-8",
            )
