"""Turn a dxc -dumpbin DXIL listing into readable pseudo-HLSL, naming cbuffer reads (UE material preshader slots)
and texture samples. dxil2hlsl.py <listing.ll> <uniforms.json>#<key-prefix> <out.txt>"""
import json, re, struct, sys
from collections import Counter

ll = open(sys.argv[1], encoding='utf-8').read().splitlines()
uni_path, key_prefix = sys.argv[2].split('#')
uni = json.load(open(uni_path))
key = next(k for k in uni if k.startswith(key_prefix))
slots = {int(k): v for k, v in uni[key]['slots'].items()}
tex_names = uni[key]['textures']

CLASS = {'cbuffer': 2, 'texture': 0, 'sampler': 3, 'UAV': 1}
binds = {}
for line in ll:
    m = re.match(r';\s+(\S+)\s+(cbuffer|texture|sampler|UAV)\s+.*?\s+(cb|t|s|u)(\d+)\s+\d+\s*$', line)
    if m:
        binds[(CLASS[m.group(2)], int(m.group(4)))] = m.group(1)


def tex_label(name):
    m = re.match(r'Material_Texture2D(Array)?_(\d+)$', name)
    if not m:
        return name
    group = tex_names.get('2' if m.group(1) else '0', [])
    i = int(m.group(2))
    return f'{group[i]}' if i < len(group) else name


def slot_label(reg, comp):
    off = reg * 4 + comp
    best = None
    for s, (t, e) in slots.items():
        n = {'Float1': 1, 'Float2': 2, 'Float3': 3, 'Float4': 4}.get(t, 1)
        if s <= off < s + n:
            best = (s, t, e, off - s)
    if not best:
        return f'PS[{off}]'
    s, t, e, k = best
    e = re.sub(r'\[([A-Za-z0-9_ ]+)=[^\]]*\]', r'\1', e)
    sw = '' if t == 'Float1' else '.' + 'xyzw'[k]
    return f'{{{e}}}{sw}' if len(e) < 160 else f'{{PS{s}:{e[:150]}...}}{sw}'


body, inside = [], False
for line in ll:
    if line.startswith('define void @'):
        inside = True
        continue
    if inside and line.startswith('}'):
        break
    if inside:
        body.append(line)

order, uses = [], Counter()
for line in body:
    code = line.split(';')[0].rstrip()
    comment = line.split(';', 1)[1].strip() if ';' in line else ''
    m = re.match(r'\s*(%[\w.]+) = (.*)', code)
    for o in re.findall(r'%[\w.]+', code.split('=', 1)[1] if m else code):
        uses[o] += 1
    order.append((m.group(1), m.group(2), comment) if m else (None, code.strip(), comment))


def fmt_const(c):
    c = c.strip()
    if re.fullmatch(r'0x[0-9A-Fa-f]{16}', c):
        return f'{struct.unpack(">d", bytes.fromhex(c[2:]))[0]:.6g}'
    return c


names, handles, cbret = {}, {}, {}


def arg(a):
    a = a.strip()
    m = re.match(r'(?:float|i32|i1|i8|i64|half|double|%dx\.types\.\w+)\s+(.*)', a)
    a = m.group(1) if m else a
    if a.startswith('%'):
        return names.get(a, a)
    return fmt_const(a)


def split_args(s):
    out, depth, cur = [], 0, ''
    for ch in s:
        if ch in '({[':
            depth += 1
        if ch in ')}]':
            depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur)
            cur = ''
        else:
            cur += ch
    if cur.strip():
        out.append(cur)
    return out


BIN = {'fadd': '+', 'fsub': '-', 'fmul': '*', 'fdiv': '/', 'add': '+', 'sub': '-', 'mul': '*', 'and': '&', 'or': '|',
       'xor': '^', 'shl': '<<', 'lshr': '>>', 'ashr': '>>', 'frem': '%', 'udiv': '/', 'sdiv': '/'}
CMP = {'olt': '<', 'ogt': '>', 'ole': '<=', 'oge': '>=', 'oeq': '==', 'une': '!=', 'one': '!=', 'eq': '==', 'ne': '!=',
       'slt': '<', 'sgt': '>', 'ult': '<', 'ugt': '>', 'sle': '<=', 'sge': '>=', 'uge': '>=', 'ule': '<=', 'ord': 'ord', 'uno': 'uno'}
lines = []
for dst, rhs, comment in order:
    if dst is None:
        if 'storeOutput' in rhs:
            a = split_args(rhs[rhs.index('(') + 1:rhs.rindex(')')])
            lines.append(f'  OUT[{arg(a[1])}][{arg(a[3])}] = {arg(a[4])};')
        elif rhs.startswith('br ') or rhs.endswith(':') or rhs.startswith('ret') or rhs.startswith('switch'):
            lines.append(f'  // {rhs}')
        elif rhs.strip() and not rhs.startswith('call void @dx.op.discard'):
            lines.append(f'  // {rhs[:140]}')
        elif rhs.startswith('call void @dx.op.discard'):
            lines.append(f'  discard_if({arg(split_args(rhs[rhs.index("(") + 1:rhs.rindex(")")])[1])});')
        continue
    expr = None
    m = re.match(r'(fadd|fsub|fmul|fdiv|frem|add|sub|mul|udiv|sdiv|and|or|xor|shl|lshr|ashr)(?: fast| nsw| nuw| exact)* \S+ (.+)', rhs)
    if m:
        a, b = split_args(m.group(2))
        expr = f'({arg(a)} {BIN[m.group(1)]} {arg(b)})'
    m2 = re.match(r'(?:f|i)cmp(?: fast)? (\w+) \S+ (.+)', rhs)
    if m2:
        a, b = split_args(m2.group(2))
        expr = f'({arg(a)} {CMP.get(m2.group(1), m2.group(1))} {arg(b)})'
    m3 = re.match(r'select(?: fast)? i1 ([^,]+), \S+ ([^,]+), \S+ (.+)', rhs)
    if m3:
        expr = f'({arg(m3.group(1))} ? {arg(m3.group(2))} : {arg(m3.group(3))})'
    m4 = re.match(r'(sitofp|uitofp|fptosi|fptoui|bitcast|zext|sext|trunc|fpext|fptrunc) \S+ (\S+) to (\S+)', rhs)
    if m4:
        expr = f'{m4.group(1)}({arg(m4.group(2))})'
    m5 = re.match(r'extractvalue \S+ (%[\w.]+), (\d+)', rhs)
    if m5:
        src, idx = m5.group(1), int(m5.group(2))
        if src in cbret:
            h, reg = cbret[src]
            if h == 'UniformBufferConstants_Material' and isinstance(reg, int):
                expr = slot_label(reg, idx)
            else:
                expr = f'{h.replace("UniformBufferConstants_", "")}[{reg}].{"xyzw"[idx]}'
        else:
            expr = f'{names.get(src, src)}.{"xyzw"[idx] if idx < 4 else idx}'
    m6 = re.match(r'phi \S+ (.+)', rhs)
    if m6:
        expr = 'phi(' + ', '.join(arg(x.strip('[] ').split(',')[0]) for x in re.findall(r'\[[^\]]+\]', m6.group(1))) + ')'
    if rhs.startswith('call'):
        fn = comment.split('(')[0] if comment else 'call'
        inner = rhs[rhs.index('(') + 1:rhs.rindex(')')]
        a = split_args(inner)[1:]
        if fn == 'CreateHandleFromBinding':
            b = re.search(r'ResBind \{ i32 (-?\d+), i32 -?\d+, i32 -?\d+, i8 (\d+) \}', rhs)
            cls, reg = (int(b.group(2)), int(b.group(1))) if b else (0, 0)  # zeroinitializer = SRV t0
            handles[dst] = binds.get((cls, reg), f'res{cls}:{reg}')
            continue
        if fn == 'AnnotateHandle':
            src = re.search(r'Handle (%[\w.]+)', rhs).group(1)
            handles[dst] = handles.get(src, src)
            continue
        if fn == 'CBufferLoadLegacy':
            h = re.search(r'Handle (%[\w.]+)', rhs).group(1)
            r = arg(a[1])
            cbret[dst] = (handles.get(h, h), int(r) if r.isdigit() else r)
            continue
        if fn.startswith('Sample'):
            t = re.findall(r'Handle (%[\w.]+)', rhs)
            coords = [arg(x) for x in a[2:6] if 'undef' not in x]
            expr = f'{fn}({tex_label(handles.get(t[0], t[0]))}, [{", ".join(coords)}])'
        elif fn == 'LoadInput':
            c = arg(a[2])
            expr = f'IN{arg(a[0])}.{"xyzw"[int(c)] if c.isdigit() else c}'
        elif fn in ('RawBufferLoad', 'BufferLoad', 'TextureLoad'):
            t = re.findall(r'Handle (%[\w.]+)', rhs)
            expr = f'{fn}({handles.get(t[0], t[0])}, {", ".join(arg(x) for x in a[1:3])})'
        else:
            expr = f'{fn.lower()}({", ".join(arg(x) for x in a)})'
    if expr is None:
        expr = f'/*{rhs[:100]}*/'
    if uses[dst] <= 1 and len(expr) < 160 and not expr.startswith(('Sample', 'phi')):
        names[dst] = expr
    else:
        n = 't' + dst[1:].replace('.', '_')
        names[dst] = n
        lines.append(f'  {n} = {expr};')
open(sys.argv[3], 'w', encoding='utf-8').write('\n'.join(lines))
print('lines', len(lines))
