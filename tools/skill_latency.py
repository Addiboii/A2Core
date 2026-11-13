"""Skill latency of our own server, from the client traces in /var/log/aion/traces (or given files).

Each trace timeline row is [ms, opcode, length] for a client request and [ms, opcode, length, 1] for a server
message. For every cast request (0x3800) this measures the time to the next 0x3801 (Skill start result), from that
0x3801 to the next 0x3804 (SkillEffect_NT) and to the next 0x3806 (SkillEnd), within 3 s. The official targets
measured on 2026-11-06 over the internet (~15 ms ping): request -> 0x3801 median 32 ms, p90 51, p99 61;
0x3801 -> first 0x3804 median 52 ms, p90 296 (follows the skill's hit timing).

    python3 tools/skill_latency.py [trace.json ...] [--since YYYYMMDD-HHMMSS]
"""
import glob
import json
import statistics
import sys

WINDOW_MS = 3000


def quantiles(values):
    if not values:
        return 'no samples'
    v = sorted(values)
    pick = lambda q: v[min(len(v) - 1, int(len(v) * q))]
    return f'n={len(v)} median {statistics.median(v):.0f} ms, p90 {pick(0.9)} ms, p99 {pick(0.99)} ms, max {v[-1]} ms'


def measure(paths):
    to_result, to_effect, to_end = [], [], []
    for path in paths:
        with open(path, encoding='utf-8') as f:
            timeline = json.load(f).get('timeline', [])
        for i, row in enumerate(timeline):
            if len(row) != 3 or row[1] != 0x3800:
                continue
            start, result_at = row[0], None
            for later in timeline[i + 1:]:
                if later[0] - start > WINDOW_MS:
                    break
                if len(later) == 3 and later[1] == 0x3800:
                    break  # the next cast request: this one's replies are over
                if len(later) < 4:
                    continue
                if later[1] == 0x3801 and result_at is None:
                    result_at = later[0]
                    to_result.append(result_at - start)
                elif later[1] == 0x3804 and result_at is not None and len(to_effect) < len(to_result):
                    to_effect.append(later[0] - result_at)
                elif later[1] == 0x3806 and result_at is not None:
                    to_end.append(later[0] - start)
                    break
    return to_result, to_effect, to_end


def main(argv):
    since = None
    if '--since' in argv:
        since = argv[argv.index('--since') + 1]
        argv = [a for a in argv if a not in ('--since', since)]
    paths = argv or sorted(glob.glob('/var/log/aion/traces/client-trace-*.json'))
    if since:
        paths = [p for p in paths if p.rsplit('client-trace-', 1)[-1][:15] >= since]
    to_result, to_effect, to_end = measure(paths)
    print(f'{len(paths)} trace files')
    print('cast request -> 0x3801:', quantiles(to_result))
    print('0x3801 -> first 0x3804:', quantiles(to_effect))
    print('cast request -> 0x3806:', quantiles(to_end))


if __name__ == '__main__':
    main(sys.argv[1:])
