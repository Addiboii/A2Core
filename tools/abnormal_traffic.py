"""Abnormals (buffs, debuffs, crowd control) the player's skills apply, from the official recordings → tools/data/abnormal-observed.json.

AbnormalAdd_NT 0x382a {key varint, count varint, Common_Abnormal: mask u8 (1 caster, 2 skill, 4 multiply caster, 8 multiply
value, 16 range base), uid varint, abnormal i32, duration i64, expire i64, [caster varint], level u8, [skill i32], ready u8,
[multiply caster varint], [multiply value i32], [range base Vec3]}. Per abnormal the player cast (caster = the player): on whom
(self, monster, other), the durations seen, the skill that applied it, how it ended (AbnormalRemoveList_NT 0x382c reason) and,
for abnormals on the player, the CharStatsUpdate_NT 0x364a stats that changed within 150 ms of the add and of the removal
(the buff's stat values). Duplicate recordings count once.

    python3 tools/abnormal_traffic.py [captures dir]
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
OUT = ROOT / 'tools/data/abnormal-observed.json'


def read_abnormal(b, i):
    mask = b[i]
    uid, i = varint(b, i + 1)
    ab, dur, exp = struct.unpack_from('<iqq', b, i)
    i += 20
    caster = None
    if mask & 1:
        caster, i = varint(b, i)
    level = b[i]
    i += 1
    skill = None
    if mask & 2:
        skill = struct.unpack_from('<i', b, i)[0]
        i += 4
    i += 1
    if mask & 4:
        _, i = varint(b, i)
    if mask & 8:
        i += 4
    if mask & 16:
        i += 12
    return {'uid': uid, 'abnormal': ab, 'duration': dur, 'caster': caster, 'level': level, 'skill': skill}, i


def stats_of(b):
    key, i = varint(b, 2)
    n = b[i]
    i += 1
    out = {}
    for _ in range(n):
        t = struct.unpack_from('<H', b, i)[0]
        v = struct.unpack_from('<i', b, i + 2)[0]
        out[t] = v
        i += 6
    return key, out


def main(argv):
    root = Path(argv[0] if argv else Path.home() / 'captures')
    seen = set()
    info = collections.defaultdict(lambda: {'on': collections.Counter(), 'durations': collections.Counter(), 'skills': collections.Counter(),
                                            'levels': collections.Counter(), 'removed': collections.Counter(), 'lasted_ms': [],
                                            'stat_add': collections.Counter(), 'samples': 0})
    for path in sorted(glob.glob(str(root / 'capture-*/timeline-13328/timeline.jsonl'))):
        rows = [json.loads(line) for line in open(path)]
        digest = hash(''.join(r.get('body') or '' for r in rows if r['dir'] == 's2c')[:3_000_000])
        if digest in seen:
            continue
        seen.add(digest)
        me = own_key(rows)
        live = {}            # (target, uid) -> (abnormal, ms)
        last_stats = {}
        pending = []         # (abnormal, ms) of adds on the player waiting for the stat update
        s2c = [r for r in rows if r['dir'] == 's2c' and r.get('body')]
        for r in s2c:
            b = bytes.fromhex(r['body'])
            try:
                if r['op'] == '0x382a':
                    target, i = varint(b, 2)
                    n, i = varint(b, i)
                    for _ in range(n):
                        a, i = read_abnormal(b, i)
                        if a['caster'] != me:
                            continue
                        e = info[a['abnormal']]
                        e['samples'] += 1
                        e['on']['self' if target == me else 'monster' if target < 0x40000 else 'other'] += 1
                        e['durations'][a['duration']] += 1
                        e['levels'][a['level']] += 1
                        if a['skill']:
                            e['skills'][a['skill']] += 1
                        live[(target, a['uid'])] = (a['abnormal'], r['ms'])
                        if target == me:
                            pending.append((a['abnormal'], r['ms']))
                elif r['op'] == '0x382c':
                    target, i = varint(b, 2)
                    n, i = varint(b, i)
                    for _ in range(n):
                        i += 1
                        uid, i = varint(b, i)
                        reason = b[i]
                        i += 1
                        if (target, uid) in live:
                            ab, t0 = live.pop((target, uid))
                            info[ab]['removed'][reason] += 1
                            info[ab]['lasted_ms'].append(r['ms'] - t0)
                elif r['op'] == '0x364a':
                    key, stats = stats_of(b)
                    if key != me:
                        continue
                    for ab, t0 in [p for p in pending if r['ms'] - p[1] <= 150]:
                        for t, v in stats.items():
                            before = last_stats.get(t)
                            if before is not None and v != before:
                                info[ab]['stat_add'][f'{t}:{v - before:+d}'] += 1
                    pending = [p for p in pending if r['ms'] - p[1] <= 150]
                    last_stats.update(stats)
            except (IndexError, struct.error):
                continue
    out = {}
    for ab, e in sorted(info.items()):
        out[str(ab)] = {'samples': e['samples'], 'on': dict(e['on']), 'durations_ms': {str(k): v for k, v in e['durations'].most_common(5)},
                        'skills': dict(e['skills']), 'levels': dict(e['levels']), 'removed_reasons': dict(e['removed']),
                        'lasted_ms': int(statistics.median(e['lasted_ms'])) if e['lasted_ms'] else None,
                        'stat_changes': dict(e['stat_add'].most_common(8))}
    OUT.write_text(json.dumps({'source': 'official recordings 2026-11-06/07 (tools/abnormal_traffic.py)', 'abnormals': out}, indent=1) + '\n')
    print(len(out), 'abnormals cast by the player')


if __name__ == '__main__':
    main(sys.argv[1:])
