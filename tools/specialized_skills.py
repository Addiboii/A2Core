"""Skill specialisation (parts) -> tools/data/specialized-skills.json.

Read from the client tables of build 26262663 (WorldProbe exports under research/local; a worktree points there with
AION_RESEARCH_LOCAL):
- SpecializedSkillParts.dat (specialized-skill-tables/table-001): per part its parent skill, the parent level it needs and
  InitEquipSlotType (None for the parts a player chooses; Slot_1..4 for a stigma's parts, equipped by the stigma's level).
- SpecializedSkillSlot.dat (table-002): per SkillAcquireType the slots and the parent level each opens at, and whether the
  player edits it (Mastery: Slot_1-3 at 8, 12, 20, editable; Stigma: Slot_1-5, not editable).
- SpecializedSkill.dat (table-000): every child skill of a parent (5 single parts, 10 pairs and 10 triples of a mastery
  skill; 5 of a stigma).
- Skill.dat via the catalogue (game-catalog-v2): the children's cast rows (tools/class_skills.cast_row).

Labels: every number is OBSERVED (read from the named table). OBSERVED in the community recordings of 2026-11-04 to 06:
the child the client casts is the parent + the equipped parts' digits ascending (parts 30 and 40 of 11020000 -> 11020340),
and SpecializedSkill.dat lists exactly those ids. Only mastery parents are written here: stigma parts are the stigma
variants of tools/data/stigmas.json (the same +10..+40 at stigma levels 5/10/15/20, checked in the tests).
Rebuild with `python tools/specialized_skills.py`.
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/specialized-skills.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
TABLES = LOCAL / 'specialized-skill-tables'
CATALOG = LOCAL / 'game-catalog-v2/entries.jsonl'
BUILD = '26262663'

if __package__:
    from . import class_skills
else:
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import class_skills


def _short(value):
    return str(value).split('::')[-1]


def _value(v):
    return v.get('Value') if isinstance(v, dict) else v


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def slot_levels(rows, acquire_type):
    """[parent level that opens Slot_1, Slot_2, ...] of one SkillAcquireType."""
    found = sorted((int(_short(r['SlotType']).split('_')[1]), int(r['UnlockSkillLv'])) for r in rows
                   if _short(r['SkillAcquireType']) == acquire_type)
    if [n for n, _ in found] != list(range(1, len(found) + 1)):
        raise ValueError(f'{acquire_type} slots are not Slot_1..N: {found}')
    return [level for _, level in found]


def child_id(parent, parts):
    """The skill the client casts with these parts equipped (parent + digits ascending, OBSERVED rule)."""
    digits = sorted((p - parent) // 10 for p in parts)
    return parent + sum(d * 10 ** (len(digits) - i) for i, d in enumerate(digits))


def build(tables=TABLES, catalog=CATALOG):
    specialized = _rows(Path(tables) / 'table-000.json')
    parts_rows = _rows(Path(tables) / 'table-001.json')
    slots = _rows(Path(tables) / 'table-002.json')
    mastery = {}
    for r in parts_rows:
        if _short(r['InitEquipSlotType']) != 'None':
            continue
        mastery.setdefault(str(_value(r['ParentSkillId'])), []).append([_value(r['ID']), int(r['ParentSkillLv'])])
    for entries in mastery.values():
        entries.sort()
    children = {}
    for r in specialized:
        parent = str(_value(r['ParentSkillId']))
        if parent in mastery:
            children.setdefault(parent, []).append(_value(r['ChildSkillId']))
    entries = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'skills':
                entries[entry['id']] = entry
    wanted = sorted({c for kids in children.values() for c in kids})
    cast = {str(c): class_skills.cast_row(entries[c]) for c in wanted if c in entries}
    # A chain step's children follow the child's own chain (Skill.dat ChainSkillPrevSkillId: 11030340 after 11020340), not
    # SpecializedSkill.dat: added with their chain links, each under its step (the id with the parts digits cleared).
    class_skills.merge_chain_skills(cast, entries, wanted)
    chain_children = {}
    for key in cast:
        kid = int(key)
        if kid not in set(wanted):
            chain_children.setdefault(str(kid // 10000 * 10000), []).append(kid)
    for parent, kids in list(children.items()) + list(chain_children.items()):
        for kid in kids:
            cast[str(kid)]['parent'] = int(parent)
    return {
        'source': f'SpecializedSkill/Parts/Slot.dat and Skill.dat (catalogue) of client build {BUILD} (tools/specialized_skills.py)',
        'build': BUILD,
        'slot_levels': slot_levels(slots, 'Mastery'),
        'stigma_slots': len(slot_levels(slots, 'Stigma')),
        'parts': dict(sorted(mastery.items())),
        'children': {p: sorted(k) for p, k in sorted(children.items())},
        'chain_children': {p: sorted(k) for p, k in sorted(chain_children.items())},
        'skills': cast,
        'missing_skills': sorted(set(wanted) - set(entries)),
        'evidence': {
            'slot_levels': 'SpecializedSkillSlot.dat Mastery rows: UnlockSkillLv of Slot_1..3 (OBSERVED)',
            'stigma_slots': 'SpecializedSkillSlot.dat Stigma rows (OBSERVED; the stigma lists of 0x3900 carry five)',
            'parts': 'SpecializedSkillParts.dat rows with InitEquipSlotType None: [part id, ParentSkillLv] per parent (OBSERVED)',
            'children': 'SpecializedSkill.dat ChildSkillId per mastery parent (OBSERVED)',
            'chain_children': 'Skill.dat chain steps of the children (ChainSkillPrevSkillId), per step id; OBSERVED in 0x5100 #643 '
                              '(11030340, 11040340 with 11020000 parts 30+40)',
            'skills': 'Skill.dat cast data of every child (catalogue, tools/class_skills.cast_row), with its parent',
        },
    }


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':'), sort_keys=True) + '\n', encoding='utf-8')
    print(f"{len(data['parts'])} specialisable skills, slots open at {data['slot_levels']}, {len(data['skills'])} child cast rows, "
          f"missing {len(data['missing_skills'])}")


if __name__ == '__main__':
    main()
