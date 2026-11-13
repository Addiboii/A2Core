"""Monster skills that move the caster (Skill effect Teleport_Caster: leaps, charges) -> tools/data/npc-teleports.json.

From the client skill table (game-catalog-v2 entries.jsonl, SkillEffect rows): EffectValueList [anchor_direction, distance cm,
angle degrees, time ms]. anchor_direction: CasterLocation_CasterToTargetDirection (from the caster, along caster->target
turned by angle: 180 = a leap backwards) or TargetLocation_CasterToTargetDirection (from the target: 200 past the player). MapLocation_MapDirection ["x, y, z", direction,
time]: a leap to a fixed spot (the Lake Guardian's 1603852 to its arena centre), kept as [anchor, 0, direction, time, x, y, z].
Only skills of tools/data/world-npcs.json monsters and the phase skills of gamedata/provisional/boss-ai.xml are kept.

    python3 tools/npc_teleports.py [entries.jsonl]
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'


def main(argv):
    catalog = Path(argv[0]) if argv else CATALOG
    used = {s for t in json.loads((ROOT / 'tools/data/world-npcs.json').read_text())['templates'].values() for s in t.get('skills', [])}
    import xml.etree.ElementTree as ET
    for phase in ET.parse(ROOT / 'gamedata/provisional/boss-ai.xml').getroot().iter('phase'):
        used |= {int(x) for x in (phase.get('skills') or '').split()}
    out = {}
    for line in open(catalog, encoding='utf-8'):
        e = json.loads(line)
        skill_id = e.get('id') if 'raw' not in e else e['raw']['ID']['Value']
        if e.get('kind') != 'skills' or skill_id not in used:
            continue
        for g in e.get('relationships', {}).get('effects', []):
            for r in g.get('rows', []):
                if str(r.get('EffectType', '')).endswith('Teleport_Caster'):
                    v = r.get('EffectValueList', [])
                    try:
                        if len(v) >= 3 and str(v[0]).startswith('MapLocation'):
                            x, y, z = (float(c) for c in str(v[1]).split(','))
                            out[str(skill_id)] = [str(v[0]), 0.0, float(v[2]), float(v[3]) if len(v) > 3 else 0.0, x, y, z]
                        elif len(v) >= 3 and str(v[0]).endswith('Direction'):
                            out[str(skill_id)] = [str(v[0]), float(v[1]), float(v[2]), float(v[3]) if len(v) > 3 else 0.0]
                    except ValueError:
                        continue   # other forms (fixed coordinates) are not handled
    (ROOT / 'tools/data/npc-teleports.json').write_text(json.dumps({'source': 'Skill.dat Teleport_Caster rows (tools/npc_teleports.py)', 'skills': out}, indent=1) + '\n')
    print(len(out), 'monster skills move their caster')


if __name__ == '__main__':
    main(sys.argv[1:])
