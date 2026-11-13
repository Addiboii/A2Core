"""Stage 1 step 4: C# message code generated from the usmap schema and the wire probe's proven rules.

    python3 tools/cs_codegen.py <probe.json>      (tools/wire_probe.py --learn [--masks] --json <probe.json>)

Writes:
  server/Aion.Protocol/Native/Generated/Packets.Generated.cs  one record per message type that decodes the official samples
      byte-exactly (and the nested Common structs), with ReadFrom/WriteTo, plus GeneratedPackets.Registry: live opcode -> codec
  tools/data/golden-samples.json  up to 6 official bodies per generated message (tests/Aion.Native.Checks rebuilds each byte for
      byte: decode, encode, compare)
Wire rules (the probe's): IntProperty i32, _result u16, *key varint, learned varint fields; Int64 i64; Float f32; Byte u8;
Bool packed bits (PacketWriter.Bool); Enum by its underlying width; Str varint byte count + UTF-8; Array/Map varint count;
presence masks (when the probe learned one): the mask after the super class's fields, optional fields read only when their bit
is set (nullable in the record).
"""
import collections
import glob
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from packet_schema import PacketSchema  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'server/Aion.Protocol/Native/Generated/Packets.Generated.cs'
GOLDEN = ROOT / 'tools/data/golden-samples.json'
PREFIX = 'DevPacketData_GameServer_'


def cs_name(struct):
    return struct.replace('DevPacketData_GameServer_', '').replace('DevPacketData_', '').replace('_', '')


def prop_name(name):
    parts = [p for p in name.strip('_').split('_') if p]
    n = ''.join(p[:1].upper() + p[1:] for p in parts) or 'Value'
    return n if not n[0].isdigit() else 'F' + n


class Gen:
    def __init__(self, schema, varints):
        self.s = schema
        self.varints = varints          # set of (struct, field)
        self.emitted, self.queue, self.out = set(), [], []

    def props(self, name):
        st = self.s.parser.structs[name]
        sup = self.props(st['super']) if st['super'] and st['super'] in self.s.parser.structs else []
        return sup + [p for _, p in sorted(st['properties'].items())]

    def owner_props(self, name):
        """(owning struct, prop) for every field, the super class's own owner kept (varint choices are per owner)."""
        st = self.s.parser.structs[name]
        sup = self.owner_props(st['super']) if st['super'] and st['super'] in self.s.parser.structs else []
        return sup + [(name, p) for _, p in sorted(st['properties'].items())]

    def kind(self, owner, p, t=None):
        t = t or p['type']
        k = t['type']
        name = p['name']
        inner = t.get('inner') or {}
        if k == 'IntProperty':
            if name == '_result':
                return ('u16', 'ushort')
            if name.lower().endswith('key') or (owner, name) in self.varints:
                return ('varint', 'long')
            return ('i32', 'int')
        if k == 'UInt32Property':
            return ('u32', 'uint')
        if k == 'UInt16Property':
            return ('u16', 'ushort')
        if k == 'Int64Property':
            return ('i64', 'long')
        if k == 'FloatProperty':
            return ('f32', 'float')
        if k == 'ByteProperty':
            return ('u8', 'byte')
        if k == 'BoolProperty':
            return ('bool', 'bool')
        if k == 'EnumProperty':
            w = {'ByteProperty': ('u8', 'byte'), 'UInt16Property': ('u16', 'ushort'), 'IntProperty': ('i32', 'int')}.get(inner.get('type'), ('u8', 'byte'))
            return w
        if k == 'StrProperty':
            return ('str', 'string')
        if k == 'StructProperty':
            self.need(t['struct'])
            return ('struct:' + cs_name(t['struct']), cs_name(t['struct']))
        if k == 'ArrayProperty':
            ik, it = self.kind(owner, p, inner)
            return ('array', f'List<{it}>', ik, it)
        if k == 'MapProperty':
            kk, kt = self.kind(owner, p, inner)
            vk, vt = self.kind(owner, p, t.get('value') or {})
            return ('map', f'List<({kt} Key, {vt} Value)>', (kk, kt), (vk, vt))
        raise ValueError('unsupported ' + k)

    def need(self, struct):
        if struct not in self.emitted:
            self.emitted.add(struct)
            self.queue.append(struct)

    @staticmethod
    def read_expr(k):
        return {'u8': 'r.U8()', 'u16': 'r.U16()', 'u32': 'r.U32()', 'i32': 'r.I32()', 'i64': 'r.I64()', 'f32': 'r.F32()',
                'bool': 'r.Bool()', 'varint': '(long)r.Varint()', 'str': 'Wire.Str(r)'}.get(k) or (k.split(':', 1)[1] + '.ReadFrom(r)')

    @staticmethod
    def write_stmt(k, v):
        return {'u8': f'w.U8({v});', 'u16': f'w.U16({v});', 'u32': f'w.U32({v});', 'i32': f'w.I32({v});', 'i64': f'w.I64({v});',
                'f32': f'w.F32({v});', 'bool': f'w.Bool({v});', 'varint': f'w.Varint({v});', 'str': f'Wire.Str(w, {v});'}.get(k) \
            or f'{v}.WriteTo(w);'

    def body(self, name, mask=None):
        lines_f, lines_r, lines_w = [], [], []
        owned = self.owner_props(name)
        own_names = {p['name'] for _, p in sorted(self.s.parser.structs[name]['properties'].items())}
        optional = {f for f, how, _ in (mask or {}).get('fields', []) if how == 'optional'}
        mask_enc = {f: enc for f, _, enc in (mask or {}).get('fields', []) if enc}
        width = (mask or {}).get('mask_width')
        bit = 0
        mask_done = False
        used = set()
        for owner, p in owned:
            if width and not mask_done and p['name'] in own_names:
                lines_f.append(f'    public {"byte" if width == 1 else "ushort"} PresenceMask {{ get; init; }}')
                lines_r.append(f'        x = x with {{ PresenceMask = r.{"U8" if width == 1 else "U16"}() }};')
                lines_w.append(f'        w.{"U8" if width == 1 else "U16"}(PresenceMask);')
                mask_done = True
            if mask and p['name'] in mask_enc:
                self.varints.discard((owner, p['name']))
                if mask_enc[p['name']] == 'varint':
                    self.varints.add((owner, p['name']))
            info = self.kind(owner, p)
            prop = prop_name(p['name'])
            base, n = prop, 2
            while prop in used or prop == cs_name(name) or prop in ('Opcode', 'PresenceMask'):
                prop, n = f'{base}{n}', n + 1
            used.add(prop)
            opt = p['name'] in optional and p['name'] in own_names
            ctype = info[1] + ('?' if opt and not info[1].startswith('List') else '')
            default = ' = [];' if info[1].startswith('List') else (' = "";' if info[1] == 'string' and not opt else '')
            if info[1] not in ('string',) and not info[1].startswith('List') and not opt and info[0].startswith('struct'):
                default = ' = new();'
            lines_f.append(f'    public {ctype} {prop} {{ get; init; }}{default}')
            if info[0] == 'array':
                rd = f'{{ var n = (int)r.Varint(); var l = new {info[1]}(n); for (var i = 0; i < n; i++) l.Add({self.read_expr(info[2])}); return l; }}'
                read = f'((Func<{info[1]}>)(() => {rd}))()'
                write = f'w.Varint({prop}.Count); foreach (var e in {prop}) {{ {self.write_stmt(info[2], "e")} }}'
            elif info[0] == 'map':
                (kk, _), (vk, _) = info[2], info[3]
                rd = f'{{ var n = (int)r.Varint(); var l = new {info[1]}(n); for (var i = 0; i < n; i++) {{ var k = {self.read_expr(kk)}; l.Add((k, {self.read_expr(vk)})); }} return l; }}'
                read = f'((Func<{info[1]}>)(() => {rd}))()'
                write = f'w.Varint({prop}.Count); foreach (var e in {prop}) {{ {self.write_stmt(kk, "e.Key")} {self.write_stmt(vk, "e.Value")} }}'
            else:
                read = self.read_expr(info[0])
                write = self.write_stmt(info[0], prop + ('!.Value' if opt and not info[1] in ('string',) and not info[0].startswith('struct') else ('!' if opt else '')))
            if opt:
                lines_r.append(f'        if (((x.PresenceMask >> {bit}) & 1) != 0) x = x with {{ {prop} = {read} }};')
                lines_w.append(f'        if (((PresenceMask >> {bit}) & 1) != 0) {{ {write} }}')
                bit += 1
            else:
                lines_r.append(f'        x = x with {{ {prop} = {read} }};')
                lines_w.append(f'        {write}')
        return lines_f, lines_r, lines_w

    def record(self, struct, opcode=None, mask=None):
        name = cs_name(struct)
        f, r, w = self.body(struct, mask)
        out = [f'/// <summary>{struct} (usmap){" live opcode 0x%04x" % opcode if opcode else ""}.</summary>',
               f'public sealed partial record {name}', '{']
        if opcode:
            out.append(f'    /// <summary>A live opcode of this struct (others may share it: see GeneratedPackets.Registry).</summary>')
            out.append(f'    public const ushort Opcode = 0x{opcode:04x};')
        out += f
        out += ['', f'    public static {name} ReadFrom(PacketReader r)', '    {', f'        var x = new {name}();'] + r + ['        return x;', '    }', '',
                '    public void WriteTo(PacketWriter w)', '    {'] + w + ['    }']
        if opcode:
            out += ['', '    public byte[] ToArray(ushort opcode = Opcode) { var w = PacketWriter.Opcode(opcode); WriteTo(w); return w.ToArray(); }']
        out.append('}')
        return out


def main(argv):
    probe = json.loads(Path(argv[0]).read_text())
    schema = PacketSchema()
    varints = set()
    msgs = []
    for op, v in sorted(probe.items()):
        if not v.get('struct'):
            continue
        full = PREFIX + v['struct']
        if full not in schema.parser.structs:
            continue
        for path in v.get('varints') or []:
            # path like _a._b[]._c -> owner struct of the last field
            owner, parts = full, [x for x in re.split(r'\.', path.replace('[]', '')) if x]
            for part in parts[:-1]:
                p = next((q for q in Gen(schema, set()).props(owner) if q['name'] == part), None)
                if not p:
                    break
                t = p['type']
                owner = t.get('struct') or (t.get('inner') or {}).get('struct') or owner
            for o, p in Gen(schema, set()).owner_props(owner):
                if p['name'] == parts[-1]:
                    varints.add((o, p['name']))
        if (v.get('mask') or {}).get('ambiguous'):
            continue   # tools/WireLearn found more than one layout (or ran out of budget): not proven, not generated
        msgs.append((int(op, 16), full, v.get('mask')))
    gen = Gen(schema, varints)
    blocks = []
    for opcode, full, mask in msgs:
        if full in gen.emitted:
            continue   # a struct that fits more than one live opcode: one record, every opcode in the registry
        gen.emitted.add(full)
        blocks.append(gen.record(full, opcode, mask))
    while gen.queue:
        blocks.append(gen.record(gen.queue.pop(0)))
    reg = ['public static class GeneratedPackets', '{', '    /// <summary>Live opcode -> decode (body with opcode) and encode, for every generated message.</summary>',
           '    public static readonly IReadOnlyDictionary<ushort, (Func<byte[], object> Read, Func<object, byte[]> Write)> Registry =',
           '        new Dictionary<ushort, (Func<byte[], object>, Func<object, byte[]>)>', '        {']
    for opcode, full, _ in msgs:
        n = cs_name(full)
        reg.append(f'            [0x{opcode:04x}] = (b => {n}.ReadFrom(new PacketReader(b) {{ Offset = 2 }}), o => (({n})o).ToArray(0x{opcode:04x})),')
    reg += ['        };', '}']
    head = ['// <auto-generated> tools/cs_codegen.py from the usmap schema and tools/wire_probe.py (Stage 1). Do not edit by hand.',
            '#nullable enable', '#pragma warning disable CS1591', 'using System.Text;', '', 'namespace Aion.Protocol.Native.Generated;', '',
            'static class Wire', '{',
            '    public static string Str(PacketReader r) => Encoding.UTF8.GetString(r.Take(checked((int)r.Varint())));',
            '    public static void Str(PacketWriter w, string? s) => w.String(s ?? "");', '}', '']
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text('\n'.join(head + [l for b in blocks for l in b + ['']] + reg) + '\n')
    # Golden samples: the official bodies of each generated message.
    want = {f'0x{o:04x}' for o, _, _ in msgs}
    golden = collections.defaultdict(list)
    for f in glob.glob(str(Path.home() / 'captures/capture-*/timeline-13328/timeline.jsonl')):
        for line in open(f):
            r = json.loads(line)
            if r['dir'] == 's2c' and r['op'] in want and r.get('body') and len(golden[r['op']]) < 6 and r['body'] not in golden[r['op']]:
                golden[r['op']].append(r['body'])
    GOLDEN.write_text(json.dumps({'source': '~/captures official bodies (tools/cs_codegen.py)', 'messages': dict(sorted(golden.items()))}, indent=0) + '\n')
    print(f'{len(msgs)} messages, {len(gen.emitted)} records -> {OUT.relative_to(ROOT)}; golden samples for {len(golden)} messages')


if __name__ == '__main__':
    main(sys.argv[1:])
