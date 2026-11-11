"""Stigma skills -> tools/data/stigmas.json.

Read from the client tables of build 26262663 (WorldProbe exports under research/local; a worktree points there with
AION_RESEARCH_LOCAL):
- SkillAcquireData.dat (starter-skills-tables/table-002): the AcquireType Stigma rows, one per class, skill and skill level:
  CostStigmaPoint (shards for that level), NeedCharacterLevel, NeedAscensionGrade, bSkillListUI.
- Exp.dat (exp-tables-20261016/table-000): BonusStigmaPoint, BonusStigmaSuperiorPoint and StigmaSkillContextSlotMax by
  character level.
- QuickSlotData.dat (starter-skills-tables/table-006): the quick slots whose InputAction is QuickSlot_Stigma_N.
- Skill.dat via the item/skill catalogue (game-catalog-v2): cast data and Damage rows of each stigma skill, read with the
  same functions as the class skills (tools/class_skills.py cast_row), so cooldowns and SkillEffect rows are this build's.

Labels: every number in the file is OBSERVED (read from the named table). The readings below are CANDIDATE:
- CostStigmaPoint is the price of reaching that SkillLevel (level 1 = learning the skill).
- BonusStigmaPoint is the running total of shards a character of that level has (it only grows: 1 at 23 ... 29 at 45).
- StigmaSkillContextSlotMax is how many stigma quick slots are open; slot N opens at the first level where it reaches N.
What no table says is listed under "unknown" in the output instead of guessed.
Rebuild with `python tools/stigmas.py`.
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/stigmas.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))
TABLES = LOCAL / 'starter-skills-tables'
EXP = LOCAL / 'exp-tables-20261016'
CATALOG = LOCAL / 'game-catalog-v2/entries.jsonl'
BUILD = '26262663'

if __package__:
    from . import class_skills
else:
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import class_skills

UNKNOWN = [
    'the shard price of lowering or resetting a stigma (no table names one)',
    'whether a stigma must sit in an open stigma quick slot to be cast (no table says)',
    'the wire form of learning a stigma (no recording; the server reads SkillLevelUp_RQ on an unlearned stigma as learning it)',
    'what BonusStigmaSuperiorPoint (superior shards, from level 46) buys: every Stigma row has CostStigmaSuperiorPoint 0',
    'what bSkillListUI false means for the two stigma skills that carry it (kept as list_ui, unused by the server)',
]


def _short(value):
    return str(value).split('::')[-1]


def _value(v):
    return v.get('Value') if isinstance(v, dict) else v


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _grade(value):
    """EAscensionGrade name -> number (usmap: AscensionGrade = 1, AscensionGrade_N = N, None = 0)."""
    name = _short(value)
    if name == 'None':
        return 0
    if name == 'AscensionGrade':
        return 1
    return int(name.rsplit('_', 1)[1])


def stigma_rows(acquire):
    """SkillAcquireData Stigma rows -> {class: {skill: {level: [cost, need level, ascension grade, list_ui]}}}."""
    out = {}
    for row in acquire:
        if _short(row.get('AcquireType')) != 'Stigma':
            continue
        level = int(row['SkillLevel'])
        out.setdefault(_short(row['ClassType']), {}).setdefault(int(_value(row['SkillId'])), {})[level] = [
            int(row.get('CostStigmaPoint') or 0), int(row.get('NeedCharacterLevel') or 0), _grade(row.get('NeedAscensionGrade', 'None')),
            bool(row.get('bSkillListUI', True)), int(row.get('CostStigmaSuperiorPoint') or 0)]
    return out


def class_entry(skills):
    """One class: its skills and the ladder they share. Raises when a class's skills disagree (the file would then need a ladder per skill)."""
    ladders = {tuple(tuple(v[:3]) for _, v in sorted(levels.items())) for levels in skills.values()}
    if len(ladders) != 1:
        raise ValueError(f'stigma skills of one class have different ladders: {len(ladders)}')
    ladder = next(iter(ladders))
    levels = sorted(next(iter(skills.values())))
    if levels != list(range(1, len(levels) + 1)):
        raise ValueError(f'stigma levels are not 1..N: {levels}')
    if any(v[4] for levels_ in skills.values() for v in levels_.values()):
        raise ValueError('a stigma row costs superior shards: the file has no place for that yet')
    return {'skills': sorted(skills),
            'ladder': [[level, cost, need, grade] for level, (cost, need, grade) in zip(levels, ladder)],
            'need_level': min(need for _, need, _ in ladder),
            'max_level': len(levels),
            'hidden': sorted(s for s, levels_ in skills.items() if not all(v[3] for v in levels_.values()))}


def stigma_slots(quick_slots):
    """QuickSlotData slots whose InputAction is QuickSlot_Stigma_N, in N order."""
    found = []
    for row in quick_slots:
        action = _short(row.get('InputAction', ''))
        if action.startswith('QuickSlot_Stigma_'):
            found.append((int(action.rsplit('_', 1)[1]), int(_value(row['SlotId']))))
    return [slot for _, slot in sorted(found)]


def exp_columns(exp):
    shards = {str(r['Level']): int(r.get('BonusStigmaPoint') or 0) for r in exp}
    superior = {str(r['Level']): int(r.get('BonusStigmaSuperiorPoint') or 0) for r in exp}
    slot_max = {str(r['Level']): int(r.get('StigmaSkillContextSlotMax') or 0) for r in exp}
    opens = []
    for level in sorted(int(k) for k in slot_max):
        while slot_max[str(level)] > len(opens):
            opens.append(level)
    return shards, superior, slot_max, opens


def build(tables=TABLES, exp_tables=EXP, catalog=CATALOG):
    by_class = stigma_rows(_rows(Path(tables) / 'table-002.json'))
    slots = stigma_slots(_rows(Path(tables) / 'table-006.json'))
    shards, superior, slot_max, opens = exp_columns(_rows(Path(exp_tables) / 'table-000.json'))
    if len(opens) > len(slots):
        raise ValueError(f'Exp.dat opens {len(opens)} stigma slots, QuickSlotData has {len(slots)}')
    wanted = {s for skills in by_class.values() for s in skills}
    entries = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'skills':
                entries[entry['id']] = entry
    missing = sorted(wanted - set(entries))
    cast = {str(s): class_skills.cast_row(entries[s]) for s in sorted(wanted) if s in entries}
    class_skills.merge_chain_skills(cast, entries, sorted(wanted & set(entries)))
    owner = {s: cls for cls, skills in by_class.items() for s in skills}
    for key, row in cast.items():
        if int(key) in owner:
            row['class'] = owner[int(key)]
    speeds = class_skills.projectile_speeds(cast, class_skills.find_table('SkillProjectile.dat', LOCAL))
    return {
        'source': f'SkillAcquireData Stigma rows, Exp.dat, QuickSlotData, Skill.dat (catalogue) of client build {BUILD} (tools/stigmas.py)',
        'build': BUILD,
        'classes': {cls: class_entry(skills) for cls, skills in sorted(by_class.items())},
        'shards': shards,
        'superior_shards': superior,
        'slot_max': slot_max,
        'slot_levels': opens,
        'slots': slots,
        'skills': cast,
        'projectile_speeds': speeds,
        'missing_skills': missing,
        'unknown': UNKNOWN,
        'evidence': {
            'ladder': 'SkillAcquireData Stigma rows: [SkillLevel, CostStigmaPoint, NeedCharacterLevel, NeedAscensionGrade number] (OBSERVED); '
                      'cost read as the price of reaching that level (CANDIDATE)',
            'shards': 'Exp.dat BonusStigmaPoint by level (OBSERVED); read as the running total a character has (CANDIDATE)',
            'superior_shards': 'Exp.dat BonusStigmaSuperiorPoint by level (OBSERVED); unused',
            'slot_max': 'Exp.dat StigmaSkillContextSlotMax by level (OBSERVED)',
            'slot_levels': 'first level where StigmaSkillContextSlotMax reaches 1, 2, ... (CANDIDATE: slot N opens there)',
            'slots': 'QuickSlotData SlotId of InputAction QuickSlot_Stigma_1..N (OBSERVED)',
            'skills': 'Skill.dat cast data and SkillEffect Damage rows (catalogue, tools/class_skills.cast_row), chain steps added',
            'hidden': 'skills whose Stigma rows have bSkillListUI false (OBSERVED; meaning unknown)',
        },
    }


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':'), sort_keys=True) + '\n', encoding='utf-8')
    for name, cls in data['classes'].items():
        print(f"{name}: {len(cls['skills'])} stigmas, need level {cls['need_level']}, max level {cls['max_level']}")
    print(f"{len(data['skills'])} cast rows, slots {data['slots']} open at {data['slot_levels']}, "
          f"shards at 45: {data['shards'].get('45')}, missing {data['missing_skills']}")


if __name__ == '__main__':
    main()
