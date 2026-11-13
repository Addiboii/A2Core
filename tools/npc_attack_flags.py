"""Which NPCs players cannot hurt and which die by a number of hits, from the client's NpcData (bCanBeAttacked,
NpcTargetAble, bHitCountBased).

Source: the client table export (tools/dump_tables.ps1 on the gaming PC, research/local/tables-20261109-023531/
Table_NpcData.json, 13,493 rows).

- untouchable: bCanBeAttacked false, or NpcTargetAble Unable (the client never lets them be targeted: invisible boss
  helpers, lasers, tornadoes, floor bombs, camera shakes). No player damage path (skills, area hits, delayed casts, damage
  over time, summons, player traps, gear procs, companions) hurts them. A map event's ChangeRelationshipEntity to a monster
  relationship makes one attackable (GameSession.MapEvents.cs SetRelationship).
  NpcTargetAble Onlypicking (5 monster rows) stays attackable: OBSERVED dying to the player (2400031 shadow spiders, 2400030 crab).
  HITTABLE_UNTARGETABLE: NpcTargetAble Unable but OBSERVED taking the player's hits (official recording), so left attackable.
- hit_count: bHitCountBased true. Each hit takes one point, whatever its damage. Their HP is the official appear's HP:
  7/7 for every one recorded (OBSERVED, NpcAppear_NT 0x3641 of 2290017, 2400121, 2400122, 2400144, 2400152, 2400340,
  levels 4-27, Normal Grade 2/3; HP fell 7 -> 0 one per hit in 0x8d00). HITS = 7 for the rest is CANDIDATE.

    python tools/npc_attack_flags.py [--table research/local/tables-20261109-023531/Table_NpcData.json]
writes tools/data/npc-attack-flags.json (embedded in Aion.World).
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLE = ROOT / 'research/local/tables-20261109-023531/Table_NpcData.json'
OUT = ROOT / 'tools/data/npc-attack-flags.json'
HITS = 7
OBSERVED_HITS = {2290017: 7, 2400121: 7, 2400122: 7, 2400144: 7, 2400152: 7, 2400340: 7}
# NpcTargetAble Unable, bCanBeAttacked true, yet the player's hits landed officially.
HITTABLE_UNTARGETABLE = {
    2400082: 'M_D1_UC_DranaSwarmEgg_01: capture-13700-20261106-115817 #92032-#92082, the player hit it (119700 -> 102600 -> 0)',
}


def _find(node, key):
    if isinstance(node, dict):
        for name, value in node.items():
            if name == key:
                return value
            found = _find(value, key)
            if found is not None:
                return found
    return None


def build(rows):
    untouchable, hit_count = [], {}
    for row in rows:
        npc = row['ID']['Value']
        targetable = row.get('NpcTargetAble', 'ENpcTargetAble::Able').split('::')[-1]
        if (not row.get('bCanBeAttacked', True) or targetable == 'Unable') and npc not in HITTABLE_UNTARGETABLE:
            untouchable.append(npc)
        if row.get('bHitCountBased'):
            hit_count[str(npc)] = OBSERVED_HITS.get(npc, HITS)
    return {'source': 'NpcData.dat bCanBeAttacked / NpcTargetAble / bHitCountBased (tools/npc_attack_flags.py)',
            'hits_observed': sorted(OBSERVED_HITS), 'hits_default': HITS,
            'hittable_untargetable': {str(k): v for k, v in HITTABLE_UNTARGETABLE.items()},
            'untouchable': sorted(untouchable), 'hit_count': dict(sorted(hit_count.items(), key=lambda kv: int(kv[0])))}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--table', type=Path, default=TABLE)
    args = parser.parse_args()
    out = build(_find(json.loads(args.table.read_text(encoding='utf-8')), 'Data'))
    OUT.write_text(json.dumps(out, separators=(',', ':')) + '\n', encoding='utf-8')
    print(len(out['untouchable']), 'untouchable,', len(out['hit_count']), 'hit-count ->', OUT.relative_to(ROOT))


if __name__ == '__main__':
    main()
