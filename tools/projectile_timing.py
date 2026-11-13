"""Projectile timings of the player's own skills from the official recordings → tools/data/projectile-timings.json.

SkillFireProjectile_NT 0x3803 {key, mask u8 0, target varint, skill i32, action uid u8, projectile varint, position Vec3,
flight ms varint, seq i32, seq u8, 0}: per skill and projectile, the median time from the caster's SkillStart_NT 0x3802 to the
fire (fire_ms), the flight time it names (flight_ms) and the projectile's seq (1.. for multi-shot skills). Skills that fire
several projectiles (Rapid Fire 3, Firestorm 5) list each. Duplicate recordings count once.

    python3 tools/projectile_timing.py [captures dir]
"""
import collections
import glob
import json
import statistics
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from skill_traffic import own_key, varint  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/data/projectile-timings.json'


def main(argv):
    root = Path(argv[0] if argv else Path.home() / 'captures')
    fire = collections.defaultdict(list)
    flight = collections.defaultdict(list)
    seqs = {}
    seen = set()
    for path in sorted(glob.glob(str(root / 'capture-*/timeline-13328/timeline.jsonl'))):
        rows = [json.loads(line) for line in open(path)]
        digest = hash(''.join(r.get('body') or '' for r in rows if r['dir'] == 's2c')[:3_000_000])
        if digest in seen:
            continue
        seen.add(digest)
        me = own_key(rows)
        starts = {}
        for r in rows:
            if r['dir'] != 's2c' or not r.get('body'):
                continue
            b = bytes.fromhex(r['body'])
            try:
                if r['op'] == '0x3802':
                    key, i = varint(b, 2)
                    if key == me:
                        starts[(struct.unpack_from('<i', b, i + 1)[0], b[i + 5])] = r['ms']
                elif r['op'] == '0x3803':
                    key, i = varint(b, 2)
                    if key != me:
                        continue
                    _, i = varint(b, i + 1)
                    skill = struct.unpack_from('<i', b, i)[0]
                    uid = b[i + 4]
                    projectile, i = varint(b, i + 5)
                    ms, i = varint(b, i + 12)
                    seq = struct.unpack_from('<i', b, i)[0]
                    k = (skill, projectile)
                    seqs[k] = seq
                    flight[k].append(ms)
                    if (skill, uid) in starts:
                        fire[k].append(r['ms'] - starts[(skill, uid)])
            except (IndexError, struct.error):
                continue
    skills = collections.defaultdict(list)
    for (skill, projectile), flights in sorted(flight.items()):
        skills[str(skill)].append({'projectile': projectile, 'seq': seqs[(skill, projectile)],
                                   'fire_ms': int(statistics.median(fire[(skill, projectile)])) if fire[(skill, projectile)] else None,
                                   'flight_ms': int(statistics.median(flights)), 'samples': len(flights)})
    for shots in skills.values():
        shots.sort(key=lambda s: s['seq'])
    fires = [s['fire_ms'] for shots in skills.values() for s in shots if s['fire_ms'] is not None]
    flights = [s['flight_ms'] for shots in skills.values() for s in shots]
    out = {'source': 'official recordings 2026-11-06/07, SkillFireProjectile_NT 0x3803 of the player (tools/projectile_timing.py)',
           'default_fire_ms': int(statistics.median(fires)), 'default_flight_ms': int(statistics.median(flights)), 'skills': skills}
    OUT.write_text(json.dumps(out, indent=1) + '\n')
    print(f"{len(skills)} skills, default fire {out['default_fire_ms']} ms, flight {out['default_flight_ms']} ms")


if __name__ == '__main__':
    main(sys.argv[1:])
