"""Quest-deferred world data: NpcData templates and spawns missing from the partial export.

Middle Abyss duty-scroll mobs (2600101–2600148) and event NPCs are referenced in quests.json
but absent from world-npcs.json until a full NpcData + MapData map 21 export lands. This module
builds tools/data/quest-world-supplement.json from quest objectives (donor templates) and merges
it into world-npcs.json / world-objects.json for audit and runtime.

    python tools/quest_supplement.py build
    python tools/quest_supplement.py apply


"""
from __future__ import annotations

import argparse
import copy
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
QUESTS = ROOT / 'tools/data/quests.json'
WORLD_NPCS = ROOT / 'tools/data/world-npcs.json'
WORLD_OBJECTS = ROOT / 'tools/data/world-objects.json'
SUPPLEMENT = ROOT / 'tools/data/quest-world-supplement.json'
MAP_POINTS = ROOT / 'tools/data/map-points.json'
MAP_BASES = ROOT / 'tools/data/map-bases.json'
AUDIT_DEFERRED = ROOT / 'docs/quests/AUDIT_DEFERRED.md'

# Deferred quest codes (tools/audit_quests.py, Oct 2026).
DEFERRED_CODES = None  # filled from audit or hard-coded list

DONOR_BY_NPC = {
    2290174: 2100286,
    2100159: 2100180,
    2406116: 2406115,
    2989502: 2989382,
    2989504: 2989382,
    2989505: 2989382,
    2959363: 2950012,
    2959365: 2950012,
    2959366: 2950012,
}

ABYSS_MIDDLE_DONOR = 2600203
ABYSS_BOSS_DONOR = 2600098
ABYSS_MIDDLE_LEVEL = 53


def deferred_quest_codes() -> list[str]:
    global DEFERRED_CODES
    if DEFERRED_CODES is not None:
        return DEFERRED_CODES
    codes = []
    audit = ROOT / 'docs/quests/AUDIT.md'
    if audit.exists():
        for line in audit.read_text(encoding='utf-8').splitlines():
            if '| data_issue |' in line and line.startswith('|'):
                m = re.match(r'\| (\w+) \|', line)
                if m:
                    codes.append(m.group(1))
    DEFERRED_CODES = codes
    return codes


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding='utf-8'))


def missing_npc_ids(quests: dict, templates: set[int]) -> set[int]:
    needed: set[int] = set()
    codes = set(deferred_quest_codes())
    for code, q in quests['quests'].items():
        if code not in codes:
            continue
        for step in q.get('steps') or []:
            for o in step.get('objectives') or []:
                if o.get('type') in ('KillNpc', 'CollectFakeItem', 'AskNpc', 'CloseNpc'):
                    for nid in o.get('npcs') or []:
                        if isinstance(nid, int):
                            needed.add(nid)
                    if isinstance(o.get('npc'), int):
                        needed.add(o['npc'])
                if o.get('npc') and isinstance(o['npc'], int):
                    needed.add(o['npc'])
    return {n for n in needed if n not in templates}


def abyss_middle_ids(missing: set[int]) -> list[int]:
    return sorted(n for n in missing if 2600100 <= n <= 2600199)


def clone_template(donor: dict, *, level: int | None = None) -> dict:
    out = copy.deepcopy(donor)
    if level is not None:
        out['level'] = level
    return out


def donor_for(npc_id: int, templates: dict[str, dict]) -> dict:
    if npc_id in DONOR_BY_NPC:
        return templates[str(DONOR_BY_NPC[npc_id])]
    if 2600100 <= npc_id <= 2600199:
        donor_key = ABYSS_BOSS_DONOR if npc_id in {2600108, 2600112, 2600116, 2600117, 2600138, 2600142, 2600146, 2600147} else ABYSS_MIDDLE_DONOR
        base = templates[str(donor_key)]
        return clone_template(base, level=ABYSS_MIDDLE_LEVEL if donor_key == ABYSS_MIDDLE_DONOR else base['level'])
    raise KeyError(f'no donor for npc {npc_id}')


def spawn_row(name: str, npc_id: int, x: float, y: float, z: float, yaw: float = 0.0, unique_key: int = 0) -> list:
    return [name, npc_id, round(x, 3), round(y, 3), round(z, 3), round(yaw, 3), unique_key, 0, -1]


def find_spawn(maps: dict, map_id: str, npc_id: int) -> list | None:
    for row in maps.get(map_id, []):
        if row[1] == npc_id:
            return row
    return None


def build_supplement() -> dict:
    quests = load_json(QUESTS)
    world = load_json(WORLD_NPCS)
    templates = world['templates']
    template_ids = {int(k) for k in templates}
    missing = missing_npc_ids(quests, template_ids)

    out_templates: dict[str, dict] = {}
    for nid in sorted(missing):
        out_templates[str(nid)] = donor_for(nid, templates)

    maps_out: dict[str, list] = {}
    triggers_out: dict[str, list] = {}

    # Light levelling event bees / kuru
    bee = find_spawn(world['maps'], '1010', 2100286)
    if bee and 2290174 in missing:
        x, y, z = bee[2], bee[3], bee[4]
        maps_out.setdefault('1010', []).append(spawn_row('M_L1_FQ_EllunRiverBees_02_QuestSup', 2290174, x + 120, y + 80, z))
    kuru = find_spawn(world['maps'], '1010', 2100180)
    if kuru and 2100159 in missing:
        x, y, z = kuru[2], kuru[3], kuru[4]
        maps_out.setdefault('1010', []).append(spawn_row('M_L1_TF_Kuru_03_001_QuestSup', 2100159, x + 200, y, z))

    # Festival detective NPCs (Light) — near existing event citizen 2989382
    anchor = find_spawn(world['maps'], '1010', 2989382)
    if anchor:
        ax, ay, az = anchor[2], anchor[3], anchor[4]
        for i, nid in enumerate([2989502, 2989504, 2989505]):
            if nid in missing:
                maps_out.setdefault('1010', []).append(
                    spawn_row(f'N_L1_FQ_Event_QuestSup_{nid}', nid, ax + 150 * (i + 1), ay, az))
        triggers_out.setdefault('1010', []).extend([
            _trigger('Trigger_DQ1202072_01', ax, ay, az, radius=1500),
            _trigger('Trigger_DQ1202072_02', ax + 300, ay, az, radius=1500),
        ])

    # Dark festival chain — near 2406115 on 1111
    dark = find_spawn(world['maps'], '1111', 2406115)
    if dark:
        dx, dy, dz = dark[2], dark[3], dark[4]
        for i, nid in enumerate([2959363, 2959365, 2959366]):
            if nid in missing:
                maps_out.setdefault('1110', []).append(
                    spawn_row(f'N_D1_FQ_Event_QuestSup_{nid}', nid, dx + 100 * i, dy, dz))
        triggers_out.setdefault('1110', []).extend([
            _trigger('Trigger_FQ_Event_1_1_0', dx, dy, dz, radius=1500),
            _trigger('Trigger_FQ_Event_1_1_1', dx + 250, dy, dz, radius=1500),
        ])
    if 2406116 in missing and dark:
        maps_out.setdefault('1111', []).append(
            spawn_row('M_D2_AH_DestDaeva_02_QuestSup', 2406116, dark[2], dark[3] + 100, dark[4]))

    # Middle Abyss (map 21 + mirror on 20 for players on live Reshanta A)
    abyss_ids = abyss_middle_ids(missing)
    base_x, base_y, base_z = 36842.2, 16638.1, 36019.0
    for index, nid in enumerate(abyss_ids):
        name = _abyss_spawn_name(nid, quests)
        row = spawn_row(name, nid, base_x + (index % 8) * 400, base_y + (index // 8) * 400, base_z)
        maps_out.setdefault('21', []).append(row)
        maps_out.setdefault('20', []).append(spawn_row(name + '_M20', nid, row[2], row[3], row[4]))

    return {
        'source': {
            'note': 'Synthetic templates cloned from nearest exported NpcData rows; spawns OURS until map 21 MapData export.',
            'donors': 'tools/quest_supplement.py DONOR_BY_NPC + ABYSS_MIDDLE_DONOR',
            'community': 'Middle Abyss duty scrolls match Aion2.gg / in-game Middle Abyss kill quests (Reshanta N7 mobs).',
        },
        'templates': out_templates,
        'maps': maps_out,
        'triggers': triggers_out,
    }


def _abyss_spawn_name(npc_id: int, quests: dict) -> str:
    for q in quests['quests'].values():
        for step in q.get('steps') or []:
            for o in step.get('objectives') or []:
                npcs = o.get('npcs') or []
                names = o.get('npc_names') or []
                for i, n in enumerate(npcs):
                    if n == npc_id and i < len(names) and names[i]:
                        return names[i] + '_QuestSup'
    return f'M_AbyssMid_{npc_id}_QuestSup'


def _trigger(name: str, x: float, y: float, z: float, radius: float = 1000.0) -> dict:
    return {
        'name': name,
        'id': abs(hash(name)) % (2**62),
        'env': 1100001,
        'position': [round(x, 3), round(y, 3), round(z, 3)],
        'direction': 0.0,
        'shape': 'Cylinder',
        'box': [0.0, 0.0],
        'radius': radius,
        'height': radius,
    }


def apply_supplement(supplement: dict | None = None) -> None:
    supplement = supplement or load_json(SUPPLEMENT)
    world = load_json(WORLD_NPCS)
    world['templates'].update(supplement.get('templates', {}))
    for map_id, rows in supplement.get('maps', {}).items():
        world['maps'].setdefault(map_id, []).extend(rows)

    skills = supplement.get('npc_skills') or {}
    world.setdefault('npc_skills', {}).update(skills)

    write_data_json(WORLD_NPCS, world)

    if supplement.get('triggers'):
        objects = load_json(WORLD_OBJECTS)
        for map_id, triggers in supplement['triggers'].items():
            entry = objects['maps'].setdefault(map_id, {'objects': [], 'triggers': []})
            if 'triggers' not in entry:
                entry['triggers'] = []
            existing = {t['name'] for t in entry['triggers']}
            for t in triggers:
                if t['name'] not in existing:
                    entry['triggers'].append(t)
        write_data_json(WORLD_OBJECTS, objects)

    _ensure_map21()


def _ensure_map21() -> None:
    points = load_json(MAP_POINTS)
    if '21' not in points['maps'] and '20' in points['maps']:
        points['maps']['21'] = copy.deepcopy(points['maps']['20'])
    reg = points.setdefault('registered', [])
    if 21 not in reg:
        reg.append(21)
        reg.sort()
    write_data_json(MAP_POINTS, points)

    bases = load_json(MAP_BASES)
    bases['bases'].setdefault('21', 20)
    write_data_json(MAP_BASES, bases)


def cmd_build() -> None:
    data = build_supplement()
    SUPPLEMENT.parent.mkdir(parents=True, exist_ok=True)
    write_data_json(SUPPLEMENT, data)
    print(f'wrote {SUPPLEMENT}: {len(data["templates"])} templates, maps {list(data["maps"])}')


def cmd_apply() -> None:
    if not SUPPLEMENT.exists():
        cmd_build()
    apply_supplement()
    print(f'merged supplement into {WORLD_NPCS.name} and {WORLD_OBJECTS.name}')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build', 'apply'])
    args = parser.parse_args()
    if args.command == 'build':
        cmd_build()
    else:
        cmd_apply()


if __name__ == '__main__':
    main()
