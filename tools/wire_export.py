"""Exports what tools/WireLearn (the C# mask learner) needs: the usmap structs, the dev opcode table, up to 200 official bodies per
server-to-client opcode from ~/captures, and the plain probe result (tools/data/wire-probe.json) so only unfitted opcodes are searched.
The mask search moved to C#: under CPython 3.14.4 on this VM the Python MaskLearner segfaulted at random points (2026-11-07).

    python3 tools/wire_export.py <out.json> [captures dir]
"""
import collections, glob, json, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from packet_schema import PacketSchema  # noqa: E402
import packet_structs  # noqa: E402
from wire_probe import OFFSETS  # noqa: E402


def main(argv):
    out = Path(argv[0])
    root = Path(argv[1]) if len(argv) > 1 else Path.home() / 'captures'
    bodies = collections.defaultdict(set)
    for f in glob.glob(str(root / 'capture-*/timeline-13328/timeline.jsonl')):
        for line in open(f):
            try:
                r = json.loads(line)
            except ValueError:
                continue
            if r.get('dir') == 's2c' and r.get('body') and len(bodies[r['op']]) < 200:
                bodies[r['op']].add(r['body'])
    # Our own sent bodies (server traces, /var/log/aion/traces): the hand-written, recording-checked writers the client accepted.
    # WireLearn uses them only to break ties between plans that fit the official bodies. Bodies cut at 2048 bytes are skipped.
    ours = collections.defaultdict(set)
    for f in sorted(glob.glob('/var/log/aion/traces/client-trace-*.json'))[-60:]:
        try:
            d = json.loads(Path(f).read_text())
        except ValueError:
            continue
        for e, body in zip(d.get('timeline') or [], d.get('bodies') or []):
            if len(e) > 3 and body and len(body) < 4096 and len(ours[hex(e[1])]) < 200:
                ours[hex(e[1])].add(body.lower())
    structs = {}
    for name, st in PacketSchema().parser.structs.items():
        structs[name] = {'super': st['super'], 'fields': [{'name': p['name'], 'type': p['type']} for _, p in sorted(st['properties'].items())]}
    probe = json.loads((Path(__file__).resolve().parent / 'data/wire-probe.json').read_text())
    out.write_text(json.dumps({'offsets': OFFSETS, 'opcodes': {str(k): v for k, v in packet_structs.GAME_OPCODES.items()}, 'structs': structs,
                               'bodies': {op: sorted(b) for op, b in sorted(bodies.items())},
                               'ours': {op: sorted(b) for op, b in sorted(ours.items())}, 'probe': probe}))
    print(f'{len(structs)} structs, {len(bodies)} opcodes -> {out}')


if __name__ == '__main__':
    main(sys.argv[1:])
