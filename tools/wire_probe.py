"""Stage 1 probe: how far do generic wire rules from the usmap schema get on the official recordings?

For every server-to-client message type in ~/captures, every DevPacketData struct whose dev opcode fits one of the known
live offsets is tried with the rules below; a struct "fits" a message type when it consumes every official body of it
exactly (all bytes, no error). Output: the fitting struct per live opcode (it also finds the opcode offsets) and the counts.

Wire rules (from the hand-written, recording-checked writers):
  IntProperty i32 LE, except _result (u16) and keys (names ending in "key", not item uids which are Int64): ULEB128 varint
  Int64Property i64 LE, FloatProperty f32, ByteProperty u8
  BoolProperty: packed bits; the first bool takes a byte, the next 7 bools of the message fill it (PacketWriter.Bool)
  EnumProperty: the width of its underlying property (ByteProperty u8, UInt16Property u16, Int32/IntProperty i32)
  StrProperty: varint byte count + UTF-8; ArrayProperty / MapProperty: varint count + elements (map: key then value)
  StructProperty: its fields in order (super class first)

    python3 tools/wire_probe.py [captures dir] [--json out.json]
"""
import collections
import glob
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from packet_schema import PacketSchema  # noqa: E402
import packet_structs  # noqa: E402

OFFSETS = [0, 0x156, 0x157, 0xa2, 0xa3, 0xa4, 0x8ed, 0x1ebe, 0x3ef9, 0x53c5, 0x54ca, 0xa762, 0xa761, 0xa763, 0x89d1]


class Fail(Exception):
    pass


class Reader:
    def __init__(self, b, i=2):
        self.b, self.i, self.bool_at, self.bits = b, i, -1, 8

    def take(self, n):
        if n < 0 or self.i + n > len(self.b):
            raise Fail('short')
        v = self.b[self.i:self.i + n]
        self.i += n
        return v

    def varint(self):
        n = s = 0
        while True:
            x = self.take(1)[0]
            n |= (x & 0x7f) << s
            s += 7
            if not x & 0x80:
                # A canonical varint never ends in a zero byte after the first: such bytes are something else (e.g. a u16
                # EStat 205 = cd 00 read as a 2-byte varint 77, which re-encodes to one byte; the golden test caught it).
                if s > 7 and x == 0:
                    raise Fail('noncanonical varint')
                return n
            if s > 63:
                raise Fail('varint')

    def boolean(self):
        if self.bits == 8:
            self.bool_at = self.i
            self.take(1)
            self.bits = 0
        v = (self.b[self.bool_at] >> self.bits) & 1
        self.bits += 1
        return bool(v)


class Rules:
    def __init__(self, schema):
        self.s = schema

    varints = set()

    def value(self, r, t, name, depth, path=''):
        kind = t['type']
        inner = t.get('inner') or {}
        if depth > 12:
            raise Fail('deep')
        if kind == 'IntProperty':
            if name == '_result':
                return struct.unpack('<H', r.take(2))[0]
            if name.lower().endswith('key') or path in self.varints:
                return r.varint()
            return struct.unpack('<i', r.take(4))[0]
        if kind in ('UInt32Property',):
            return struct.unpack('<I', r.take(4))[0]
        if kind == 'UInt16Property':
            return struct.unpack('<H', r.take(2))[0]
        if kind == 'Int64Property':
            return struct.unpack('<q', r.take(8))[0]
        if kind == 'FloatProperty':
            return struct.unpack('<f', r.take(4))[0]
        if kind == 'ByteProperty':
            return r.take(1)[0]
        if kind == 'BoolProperty':
            return r.boolean()
        if kind == 'EnumProperty':
            w = {'ByteProperty': 1, 'UInt16Property': 2, 'IntProperty': 4, 'Int32Property': 4}.get(inner.get('type'), 1)
            return int.from_bytes(r.take(w), 'little')
        if kind == 'StrProperty':
            n = r.varint()
            if n > 4096:
                raise Fail('str')
            return r.take(n).decode('utf-8', 'replace')
        if kind == 'StructProperty':
            return self.struct(r, t['struct'], depth + 1, path + '.')
        if kind == 'ArrayProperty':
            n = r.varint()
            if n > 20000:
                raise Fail('array')
            ipath = path + '[]' if inner.get('type') != 'StructProperty' else path + '[]'
            if inner.get('type') == 'StructProperty':
                return [self.struct(r, inner['struct'], depth + 1, path + '[].') for _ in range(n)]
            return [self.value(r, inner, name, depth + 1, ipath) for _ in range(n)]
        if kind == 'MapProperty':
            n = r.varint()
            if n > 20000:
                raise Fail('map')
            return [(self.value(r, inner, name, depth + 1), self.value(r, t.get('value') or {}, name, depth + 1)) for _ in range(n)]
        raise Fail('kind ' + kind)

    def props(self, name):
        st = self.s.parser.structs.get(name)
        if st is None:
            raise Fail('no struct ' + name)
        out = list(self.props(st['super'])) if st['super'] and st['super'] in self.s.parser.structs else []
        return out + [p for _, p in sorted(st['properties'].items())]

    def struct(self, r, name, depth=0, prefix=''):
        return {p['name']: self.value(r, p['type'], p['name'], depth, prefix + p['name']) for p in self.props(name)}

    def fits(self, name, body):
        r = Reader(body)
        try:
            self.struct(r, name)
        except (Fail, struct.error, UnicodeDecodeError, TypeError, RecursionError):
            # TypeError: a CPython 3.14 misbehaviour under deep recursion on this VM (the built-in len reported as an int, 2026-11-07);
            # such a decode counts as not fitting.
            return False
        return r.i == len(body)


class Learner:
    """
    Per struct, learns which IntProperty fields are varint instead of i32: a search over the field sequence, all samples parsed
    in lockstep, each int field tried as i32 then varint (one choice per field path, the same in every array element); the
    first assignment that makes every sample decode exactly wins. Only top-level and struct fields; arrays of ints follow
    their field's choice. Bounded: at most `budget` decode attempts per message type.
    """
    def __init__(self, rules, budget=4000):
        self.r, self.budget = rules, budget

    def fields(self, name, prefix=''):
        out = []
        for p in self.r.props(name):
            t = p['type']
            path = prefix + p['name']
            out.append((path, p['name'], t))
        return out

    def learn(self, name, blobs):
        ints = []
        def collect(struct_name, prefix, depth=0):
            if depth > 4:
                return
            for path, fname, t in self.fields(struct_name, prefix):
                k = t['type']
                inner = t.get('inner') or {}
                if k == 'IntProperty' and fname != '_result' and not fname.lower().endswith('key'):
                    ints.append(path)
                elif k == 'StructProperty':
                    collect(t['struct'], path + '.', depth + 1)
                elif k == 'ArrayProperty' and inner.get('type') == 'StructProperty':
                    collect(inner['struct'], path + '[].', depth + 1)
                elif k == 'ArrayProperty' and inner.get('type') == 'IntProperty':
                    ints.append(path + '[]')
        collect(name, '')
        ints = ints[:14]
        tries = 0
        # Breadth by the number of varint fields: 0, 1, 2, 3 (most messages need few).
        from itertools import combinations
        for n in range(0, min(4, len(ints)) + 1):
            for combo in combinations(ints, n):
                tries += 1
                if tries > self.budget:
                    return None
                self.r.varints = set(combo)
                if all(self.r.fits(name, b) for b in blobs):
                    return sorted(combo)
        return None


class MaskLearner:
    """
    Presence masks (Stage 1 step 3). Many messages carry a mask (u8 or u16) right after the super class's fields (the key):
    one bit per OPTIONAL field in declaration order (SkillStart_NT: bit 2 autoload_count, bit 3 autoload_time); a field whose
    bit is clear is absent. The usmap does not say which fields are optional, so this searches it: a depth-first walk over the
    struct's own fields, all samples decoded side by side; each field is tried mandatory or optional (the next mask bit) and,
    for IntProperty, as i32 or varint; a choice is dropped as soon as one sample fails. Nested structs and arrays use the
    generic rules. Bounded by `budget` steps.
    """
    def __init__(self, rules, budget=6000):
        self.r, self.budget = rules, budget

    def learn(self, name, blobs):
        blobs = sorted(blobs, key=len)[:12]
        st = self.r.s.parser.structs.get(name)
        if not st:
            return None
        own = [p for _, p in sorted(st['properties'].items())]
        sup = self.r.props(st['super']) if st['super'] and st['super'] in self.r.s.parser.structs else []
        for width in (1, 2):
            readers = []
            try:
                for b in blobs:
                    rd = Reader(b)
                    for p in sup:
                        self.r.value(rd, p['type'], p['name'], 0, p['name'])
                    mask = int.from_bytes(rd.take(width), 'little')
                    readers.append((rd, mask))
            except (Fail, struct.error, UnicodeDecodeError):
                continue
            self.steps = 0
            found = self.walk(own, 0, readers, 0, [])
            if found is not None:
                return {'mask_width': width, 'fields': found}
        return None

    def choices(self, p):
        t, fname = p['type'], p['name']
        encodings = ['i32', 'varint'] if t['type'] == 'IntProperty' and fname != '_result' and not fname.lower().endswith('key') else [None]
        return [(optional, enc) for optional in (False, True) for enc in encodings]

    def step(self, p, optional, enc, readers, bit):
        """The readers after field p with this choice, or None when a sample fails."""
        t, fname = p['type'], p['name']
        trial = []
        for rd, mask in readers:
            nr = Reader(rd.b, rd.i)
            nr.bool_at, nr.bits = rd.bool_at, rd.bits
            if not optional or (mask >> bit) & 1:
                try:
                    if enc == 'varint':
                        nr.varint()
                    elif enc == 'i32':
                        if nr.take(4) is False:
                            return None
                    else:
                        self.r.value(nr, t, fname, 1, fname)
                except (Fail, struct.error, UnicodeDecodeError, TypeError, RecursionError, IndexError):
                    return None
            trial.append((nr, mask))
        return trial

    def walk(self, fields, i, readers, bit, plan):
        # Depth-first with an explicit stack (the recursive form crashed CPython 3.14.4 on this VM: segfaults and
        # "'NoneType' object is not an iterator" on a constant tuple, 2026-11-07). Each frame: field index, readers, bit, plan,
        # the next choice to try.
        stack = [(i, readers, bit, plan, 0)]
        while stack:
            self.steps += 1
            if self.steps > self.budget:
                return None
            i, readers, bit, plan, c = stack.pop()
            if i == len(fields):
                if all(rd.i == len(rd.b) for rd, _ in readers):
                    return plan
                continue
            options = self.choices(fields[i])
            if c >= len(options):
                continue
            stack.append((i, readers, bit, plan, c + 1))   # the next choice of this field, after the subtree of this one
            optional, enc = options[c]
            if optional and bit >= 16:
                continue
            trial = self.step(fields[i], optional, enc, readers, bit)
            if trial is not None:
                stack.append((i + 1, trial, bit + (1 if optional else 0),
                              plan + [(fields[i]['name'], 'optional' if optional else 'mandatory', enc)], 0))
        return None


def main(argv):
    root = Path(argv[0]) if argv and not argv[0].startswith('--') else Path.home() / 'captures'
    out = argv[argv.index('--json') + 1] if '--json' in argv else None
    bodies = collections.defaultdict(set)
    for f in glob.glob(str(root / 'capture-*/timeline-13328/timeline.jsonl')):
        for line in open(f):
            try:
                r = json.loads(line)
            except ValueError:
                continue   # a line being written (a recording in progress) or damaged
            if r['dir'] == 's2c' and r.get('body') and len(bodies[r['op']]) < 40:
                bodies[r['op']].add(r['body'])
    schema = PacketSchema()
    rules = Rules(schema)
    dev = {k: v for k, v in packet_structs.GAME_OPCODES.items()}
    result, offsets = {}, collections.Counter()
    for op, samples in sorted(bodies.items()):
        live = int(op, 16)
        blobs = [bytes.fromhex(b) for b in samples]
        fit = None
        for off in OFFSETS:
            name = dev.get(live - off)
            if name and all(rules.fits(name, b) for b in blobs):
                fit = (name.replace('DevPacketData_GameServer_', ''), off)
                break
        varint_fields = []
        if not fit and '--learn' in argv:
            learner = Learner(rules)
            for off in OFFSETS:
                name = dev.get(live - off)
                if name:
                    got = learner.learn(name, blobs)
                    rules.varints = set()
                    if got is not None:
                        fit = (name.replace('DevPacketData_GameServer_', ''), off)
                        varint_fields = got
                        break
        masks = None
        if not fit and '--masks' in argv:
            ml = MaskLearner(rules)
            for off in OFFSETS:
                name = dev.get(live - off)
                if name:
                    if '--trace' in argv:
                        print('mask', op, name, file=sys.stderr, flush=True)
                    got = ml.learn(name, blobs)
                    if got is not None:
                        fit = (name.replace('DevPacketData_GameServer_', ''), off)
                        masks = got
                        break
        result[op] = {'samples': len(blobs), 'struct': fit[0] if fit else None, 'offset': hex(fit[1]) if fit else None, 'varints': varint_fields,
                      'mask': masks}
        if fit:
            offsets[(op[:4], hex(fit[1]))] += 1
    ok = sum(1 for v in result.values() if v['struct'])
    print(f'{ok} of {len(result)} official message types decode byte-exactly with the generic rules')
    print('offsets by opcode block:', dict(sorted(offsets.items())))
    if out:
        Path(out).write_text(json.dumps(result, indent=1) + '\n')


if __name__ == '__main__':
    main(sys.argv[1:])
