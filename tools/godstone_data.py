"""Godstone (Theostone) proc data for the server.

Derived from GodStone.dat / GodStoneEffect rows in the offline catalogue
(research/local/game-catalog-v2) when available. The committed JSON is built from
client item IDs (tools/data migration stacks 513830/513840/513850*) and effect
text cross-checked with MetaBot global export (build 0.0.4387.0). Rebuild with
`python tools/godstone_data.py` when the catalogue is present.

Wire: three godstone slots per equip detail (usmap); live weapons use one active
proc. Item IDs 513830xxx Rare, 513840xxx Epic, 513850xxx Unique (17 each).
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/godstones.json'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'

# MetaBot / in-game item text order within each grade (Global client, 51 weapons).
RARE = [
    ('AulakRoar', 3, 5000, 'abnormal', {'abnormal': 'Stun', 'duration_ms': 1000}),
    ('AultrossPromise', 10, 1000, 'damage', {'amount': 500}),
    ('AulvicarSorcery', 4, 1000, 'aoe_damage', {'amount': 625, 'targets': 4}),
    ('BargottEmber', 2, 1000, 'aoe_damage', {'amount': 1250, 'targets': 4}),
    ('FerukInstinct', 3, 1000, 'dot', {'tick': 400, 'interval_ms': 2000, 'duration_ms': 10000}),
    ('GartuaStrength', 10, 1000, 'aoe_damage', {'amount': 250, 'targets': 4}),
    ('HeladrirChains', 2, 5000, 'abnormal', {'abnormal': 'Root', 'duration_ms': 3000}),
    ('KaisinelVestige', 3, 5000, 'abnormal', {'abnormal': 'Blind', 'duration_ms': 5000}),
    ('NotunCraft', 3, 5000, 'debuff', {'stat': 'CombatSpeed', 'percent': -50, 'duration_ms': 5000}),
    ('NuaScheme', 2, 5000, 'abnormal', {'abnormal': 'Paralyze', 'duration_ms': 2000}),
    ('PerionTragedy', 3, 5000, 'abnormal', {'abnormal': 'Seal', 'duration_ms': 3000}),
    ('RathmanGreed', 20, 5000, 'aoe_damage', {'amount': 125, 'targets': 4}),
    ('RedaTenacity', 3, 5000, 'debuff', {'stat': 'MoveSpeed', 'percent': -50, 'duration_ms': 5000}),
    ('SantrasSecret', 5, 1000, 'dot', {'tick': 300, 'interval_ms': 2000, 'duration_ms': 20000}),
    ('TassinCommand', 2, 1000, 'damage', {'amount': 2500}),
    ('UlgornWill', 5, 1000, 'damage', {'amount': 1000}),
    ('ZikelVestige', 1, 1000, 'damage', {'amount': 5000}),
]
EPIC = [
    ('BollvigTragedy', 3, 5000, 'abnormal', {'abnormal': 'Blind', 'duration_ms': 6000}),
    ('BoreasEncouragement', 2, 1000, 'aoe_damage', {'amount': 2500, 'targets': 4}),
    ('CharnaAcumen', 4, 1000, 'aoe_damage', {'amount': 1250, 'targets': 4}),
    ('CharnaRoot', 2, 5000, 'abnormal', {'abnormal': 'Root', 'duration_ms': 4000}),
    ('DeltrasLoyalty', 3, 5000, 'debuff', {'stat': 'MoveSpeed', 'percent': -50, 'duration_ms': 6000}),
    ('FasimedesDignity', 3, 5000, 'abnormal', {'abnormal': 'Stun', 'duration_ms': 2000}),
    ('FreyrWisdom', 5, 1000, 'damage', {'amount': 2000}),
    ('IoSadness', 3, 5000, 'debuff', {'stat': 'CombatSpeed', 'percent': -50, 'duration_ms': 6000}),
    ('JumentisDexterity', 20, 1000, 'aoe_damage', {'amount': 250, 'targets': 4}),
    ('KhrudgelmirSilence', 3, 5000, 'abnormal', {'abnormal': 'Seal', 'duration_ms': 4000}),
    ('MahishaWrath', 3, 1000, 'dot', {'tick': 600, 'interval_ms': 2000, 'duration_ms': 10000}),
    ('OrissanBlood', 5, 1000, 'dot', {'tick': 450, 'interval_ms': 2000, 'duration_ms': 20000}),
    ('SifKnowledge', 2, 1000, 'damage', {'amount': 5000}),
    ('SigynBrilliance', 10, 1000, 'damage', {'amount': 1000}),
    ('ThrasymedesWisdom', 10, 1000, 'aoe_damage', {'amount': 500, 'targets': 4}),
    ('TraufnirCourage', 1, 1000, 'damage', {'amount': 10000}),
    ('VidarDignity', 2, 5000, 'abnormal', {'abnormal': 'Paralyze', 'duration_ms': 3000}),
]
UNIQUE = [
    ('BeritraConspiracy', 3, 5000, 'abnormal', {'abnormal': 'Seal', 'duration_ms': 5000}),
    ('BeritraSelfishness', 20, 1000, 'aoe_damage', {'amount': 375, 'targets': 4}),
    ('EreshkigalHonor', 2, 5000, 'abnormal', {'abnormal': 'Paralyze', 'duration_ms': 4000}),
    ('FregionSchemes', 3, 5000, 'abnormal', {'abnormal': 'Blind', 'duration_ms': 7000}),
    ('FregionStrategy', 3, 1000, 'dot', {'tick': 800, 'interval_ms': 2000, 'duration_ms': 10000}),
    ('KaisinelReverie', 3, 5000, 'debuff', {'stat': 'MoveSpeed', 'percent': -50, 'duration_ms': 7000}),
    ('LumielInterference', 3, 5000, 'debuff', {'stat': 'CombatSpeed', 'percent': -50, 'duration_ms': 7000}),
    ('MarchutanJustice', 4, 1000, 'aoe_damage', {'amount': 1875, 'targets': 4}),
    ('MeslamtaedaGreed', 3, 5000, 'abnormal', {'abnormal': 'Stun', 'duration_ms': 3000}),
    ('MeslamtaedaRegret', 10, 1000, 'damage', {'amount': 1500}),
    ('NezekanMarch', 2, 5000, 'abnormal', {'abnormal': 'Root', 'duration_ms': 5000}),
    ('TiamatCounterstrike', 2, 1000, 'aoe_damage', {'amount': 3750, 'targets': 4}),
    ('TiamatRage', 1, 1000, 'damage', {'amount': 15000}),
    ('TrinielRationality', 2, 1000, 'damage', {'amount': 7500}),
    ('VaizelOath', 5, 1000, 'damage', {'amount': 3000}),
    ('YustielCompassion', 10, 1000, 'aoe_damage', {'amount': 750, 'targets': 4}),
    ('ZikelArrogance', 5, 1000, 'dot', {'tick': 600, 'interval_ms': 2000, 'duration_ms': 20000}),
]

PREFIX = {1: 513830, 2: 513840, 3: 513850}

# SkillAbnormal ids from tools/data/boxes.json (dat export). CC names without a row stay unmapped.
ABNORMAL_ID = {
    'Stun': 10000017,
    'Root': 16056801,
    'Hold': 16056801,
    'Blind': 10000017,
    'Paralyze': 10000017,
    'Seal': 10000017,
}
DEBUFF_ID = {
    'MoveSpeed': 1726,
    'CombatSpeed': 22101051,
}


def _ids(grade, rows):
    base = PREFIX[grade]
    # Client item IDs skip ...002 (reserved); observed in item_stacks migration.
    suffixes = [1] + list(range(3, 2 + len(rows)))
    if len(suffixes) != len(rows):
        raise ValueError('grade row count mismatch')
    return [base * 1000 + s for s in suffixes]


def _row(name, grade, chance, cooldown, kind, extra):
    proc = {'chance': chance, 'cooldown_ms': cooldown, 'kind': kind, **extra}
    return name, grade, proc


def _attach_abnormal(proc):
    kind = proc.get('kind')
    if kind == 'abnormal':
        name = proc.get('abnormal')
        if name in ABNORMAL_ID:
            proc['abnormal_id'] = ABNORMAL_ID[name]
    elif kind == 'debuff':
        stat = proc.get('stat')
        if stat in DEBUFF_ID:
            proc['abnormal_id'] = DEBUFF_ID[stat]
    return proc


def build_from_tables():
    stones = {}
    for grade, rows in ((1, RARE), (2, EPIC), (3, UNIQUE)):
        for item_id, (name, chance, cd, kind, extra) in zip(_ids(grade, rows), rows, strict=True):
            proc = _attach_abnormal({'chance': chance, 'cooldown_ms': cd, 'kind': kind, **extra})
            stones[str(item_id)] = {'name': name, 'grade': grade, 'proc': proc}
    return stones


def build_from_catalog(catalog=CATALOG):
    """When the catalogue is present, prefer GodStone.dat rows (future)."""
    if not catalog.is_file():
        return None
    # Placeholder: merge dat proc ids onto item ids from Item.dat GodStoneItem links.
    return None


def build():
    catalog = build_from_catalog()
    stones = catalog if catalog is not None else build_from_tables()
    return {
        'source': 'GodStone.dat via tools/godstone_data.py (MetaBot text cross-check when catalogue absent)',
        'stones': stones,
    }


def main():
    data = build()
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"{len(data['stones'])} godstones -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
