"""The player's own skill traffic in the official recordings (~/captures/*/timeline-13328/timeline.jsonl, s2c only).

Per skill: the SkillStart_NT 0x3802 forms (mask, tail), SkillCastingStart_NT 0x3809 casting times, SkillFireProjectile_NT 0x3803
projectile ids and flight times, SkillEnd 0x3806 times and tails; chain skills: ChainSkillActiveList_NT 0x3818 {prev skill,
follow-ups} and ChainSkillDeactiveList_NT 0x3819 {skills} with the time between them and which follow-up the player cast;
SkillAutoLoadUpdate_NT 0x3822. The opcodes are the catalogue's skill block at the live offset (+0x156 up to 0x380e, +0x157 after).
Duplicate recordings (same s2c bytes) count once.

    python3 tools/skill_traffic.py [captures dir] > report.json
"""
import collections
import glob
import hashlib
import json
import statistics
import struct
import sys
from pathlib import Path


def varint(b, i):
    v = s = 0
    while True:
        c = b[i]
        i += 1
        v |= (c & 0x7f) << s
        s += 7
        if c < 0x80:
            return v, i


def i32(b, i):
    return struct.unpack_from('<i', b, i)[0], i + 4


def own_key(rows):
    """The player's key: the 0x3802 caster seen right after (<= 60 ms) a 0x3801 start result (only the caster gets one)."""
    keys = collections.Counter()
    last_result = -10_000
    for r in rows:
        if r['dir'] != 's2c' or not r.get('body'):
            continue
        if r['op'] == '0x3801':
            last_result = r['ms']
        elif r['op'] == '0x3802' and abs(r['ms'] - last_result) <= 60:
            keys[varint(bytes.fromhex(r['body']), 2)[0]] += 1
    return keys.most_common(1)[0][0] if keys else None


def analyse(paths):
    seen = set()
    skills = collections.defaultdict(lambda: {'starts': 0, 'masks': collections.Counter(), 'end_ms': [], 'end_tails': collections.Counter(),
                                              'casting_ms': [], 'projectiles': collections.Counter(), 'flight_ms': [], 'chain_from': collections.Counter()})
    chains = collections.defaultdict(lambda: {'offers': 0, 'follow_ups': collections.Counter(), 'taken': collections.Counter(), 'window_ms': [], 'taken_after_ms': []})
    autoload = collections.Counter()
    recordings = []
    for path in paths:
        rows = [json.loads(line) for line in open(path)]
        digest = hashlib.sha1(b''.join(bytes.fromhex(r['body']) for r in rows if r['dir'] == 's2c' and r.get('body'))[:2_000_000]).hexdigest()
        if digest in seen:
            continue
        seen.add(digest)
        me = own_key(rows)
        if me is None:
            continue
        recordings.append(Path(path).parts[-3])
        started = {}          # action uid -> (skill, ms)
        offer = None          # (prev skill, follow-ups, ms)
        for r in rows:
            if r['dir'] != 's2c' or not r.get('body'):
                continue
            b = bytes.fromhex(r['body'])
            op, ms = r['op'], r['ms']
            try:
                if op == '0x3802':
                    key, i = varint(b, 2)
                    if key != me:
                        continue
                    mask = b[i]
                    skill, i = i32(b, i + 1)
                    uid = b[i]
                    s = skills[skill]
                    s['starts'] += 1
                    s['masks'][mask] += 1
                    started[uid] = (skill, ms)
                    if offer and skill in offer[1]:
                        chains[offer[0]]['taken'][skill] += 1
                        chains[offer[0]]['taken_after_ms'].append(ms - offer[2])
                        s['chain_from'][offer[0]] += 1
                elif op == '0x3806':
                    key, i = varint(b, 2)
                    if key != me:
                        continue
                    skill, i = i32(b, i)
                    uid, tail = b[i], b[i + 1]
                    if uid in started and started[uid][0] == skill:
                        skills[skill]['end_ms'].append(ms - started[uid][1])
                        skills[skill]['end_tails'][tail] += 1
                elif op == '0x3809':
                    key, i = varint(b, 2)
                    if key != me:
                        continue
                    mask = b[i]
                    skill, i = i32(b, i + 1)
                    skills[skill]['casting_raw'] = r['body'][:120]
                    skills[skill].setdefault('casting_samples', []).append(r['body'])
                elif op == '0x3803':
                    key, i = varint(b, 2)
                    if key != me:
                        continue
                    i += 1
                    _, i = varint(b, i)
                    skill, i = i32(b, i)
                    i += 1
                    projectile, i = varint(b, i)
                    i += 12
                    flight, i = varint(b, i)
                    skills[skill]['projectiles'][projectile] += 1
                    skills[skill]['flight_ms'].append(flight)
                elif op == '0x3818':
                    prev, i = i32(b, 2)
                    n, i = varint(b, i)
                    follow = []
                    for _ in range(n):
                        v, i = i32(b, i)
                        follow.append(v)
                    offer = (prev, follow, ms)
                    c = chains[prev]
                    c['offers'] += 1
                    for f in follow:
                        c['follow_ups'][f] += 1
                elif op == '0x3819':
                    n, i = varint(b, 2)
                    gone = []
                    for _ in range(n):
                        v, i = i32(b, i)
                        gone.append(v)
                    if offer and set(gone) & set(offer[1]):
                        chains[offer[0]]['window_ms'].append(ms - offer[2])
                        offer = None
                elif op == '0x3822':
                    autoload[r['body']] += 1
            except (IndexError, struct.error):
                continue

    def med(v):
        return int(statistics.median(v)) if v else None

    return {
        'recordings': recordings,
        'skills': {str(k): {'starts': v['starts'], 'masks': dict(v['masks']), 'end_ms': med(v['end_ms']), 'end_tails': dict(v['end_tails']),
                            'projectiles': dict(v['projectiles']), 'flight_ms': med(v['flight_ms']),
                            'casting_samples': v.get('casting_samples', [])[:3], 'chain_from': dict(v['chain_from'])}
                   for k, v in sorted(skills.items())},
        'chains': {str(k): {'offers': v['offers'], 'follow_ups': dict(v['follow_ups']), 'taken': dict(v['taken']),
                            'window_ms': med(v['window_ms']), 'window_max_ms': max(v['window_ms']) if v['window_ms'] else None,
                            'taken_after_ms': med(v['taken_after_ms'])}
                   for k, v in sorted(chains.items())},
        'autoload': dict(autoload),
    }


if __name__ == '__main__':
    root = Path(sys.argv[1] if len(sys.argv) > 1 else Path.home() / 'captures')
    print(json.dumps(analyse(sorted(glob.glob(str(root / 'capture-*/timeline-13328/timeline.jsonl')))), indent=1, default=str))
