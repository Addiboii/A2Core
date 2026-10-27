"""Decode a cooked material's uniform preshaders into readable expressions per preshader-buffer slot."""
import base64, json, struct, sys

OPS58 = ['Nop', 'ConstantZero', 'Constant', 'Parameter', 'Add', 'Sub', 'Mul', 'Div', 'Fmod', 'Modulo', 'Min', 'Max', 'Clamp',
         'Sin', 'Cos', 'Tan', 'Asin', 'Acos', 'Atan', 'Atan2', 'Dot', 'Cross', 'Sqrt', 'Rcp', 'Length', 'Normalize', 'Saturate',
         'Abs', 'Floor', 'Ceil', 'Round', 'Trunc', 'Sign', 'Frac', 'Fractional', 'Log2', 'Log10', 'ComponentSwizzle',
         'AppendVector', 'TextureSize', 'TexelSize', 'ExternalTextureCoordinateScaleRotation', 'ExternalTextureCoordinateOffset',
         'RuntimeVirtualTextureUniform', 'SparseVolumeTextureUniform', 'Neg', 'PushValue', 'Less', 'Assign', 'Greater',
         'LessEqual', 'GreaterEqual', 'Exp', 'Exp2', 'Log']
OPS_NOMOD = [o for o in OPS58 if o != 'Modulo']
BIN = {'Add': '+', 'Sub': '-', 'Mul': '*', 'Div': '/'}
FUNC2 = {'Fmod', 'Modulo', 'Min', 'Max', 'Atan2', 'Dot', 'Cross', 'AppendVector', 'Less', 'Greater', 'LessEqual', 'GreaterEqual'}
NCOMP = {1: 1, 2: 2, 3: 3, 4: 4, 5: 1, 6: 2, 7: 3, 8: 4, 9: 1, 10: 2, 11: 3, 12: 4, 13: 1, 14: 2, 15: 3, 16: 4}
CSIZE = {**{k: 4 for k in range(1, 5)}, **{k: 8 for k in range(5, 9)}, **{k: 4 for k in range(9, 13)}, **{k: 1 for k in range(13, 17)}}


def fmt(v):
    return f'{v:.6g}'


def decode(code, params, ops):
    stack, i = [], 0
    while i < len(code):
        op = ops[code[i]] if code[i] < len(ops) else f'op{code[i]}'; i += 1
        if op == 'ConstantZero':
            i += 1; stack.append('0')
        elif op == 'Constant':
            t = code[i]; i += 1
            n, sz = NCOMP.get(t, 0), CSIZE.get(t, 0)
            if not n: raise ValueError(f'type {t}')
            vals = []
            for _ in range(n):
                raw = code[i:i + sz]; i += sz
                vals.append(fmt(struct.unpack('<f', raw)[0]) if sz == 4 and t <= 4 else fmt(struct.unpack('<d', raw)[0]) if sz == 8 else str(int.from_bytes(raw, 'little')))
            stack.append(vals[0] if n == 1 else f'float{n}({", ".join(vals)})')
        elif op == 'Parameter':
            idx, = struct.unpack_from('<H', code, i); i += 2
            stack.append(params[idx] if idx < len(params) else f'param#{idx}')
        elif op == 'PushValue':
            off, = struct.unpack_from('<H', code, i); i += 2
            stack.append(stack[off])
        elif op == 'Assign':
            v = stack.pop(); stack[-1] = v
        elif op in BIN:
            b, a = stack.pop(), stack.pop(); stack.append(f'({a} {BIN[op]} {b})')
        elif op in FUNC2:
            b, a = stack.pop(), stack.pop(); stack.append(f'{op.lower()}({a}, {b})')
        elif op == 'Clamp':
            c, b, a = stack.pop(), stack.pop(), stack.pop(); stack.append(f'clamp({a}, {b}, {c})')
        elif op == 'ComponentSwizzle':
            n = code[i]; idx = code[i + 1:i + 5]; i += 5
            stack.append(f'{stack.pop()}.{"".join("xyzw"[k] for k in idx[:n])}')
        elif op in ('TextureSize', 'TexelSize'):
            name, = struct.unpack_from('<H', code, i); i += 2 + 4 + 1 + 4
            stack.append(f'{op}(name#{name})')
        elif op in ('Nop',):
            pass
        elif op.startswith('op') or op in ('ExternalTextureCoordinateScaleRotation', 'ExternalTextureCoordinateOffset', 'RuntimeVirtualTextureUniform', 'SparseVolumeTextureUniform'):
            raise ValueError(f'unsupported {op}')
        else:
            stack.append(f'{op.lower()}({stack.pop()})')
    return stack


def run(path, out):
    d = json.load(open(path, encoding='utf-8'))
    report = {}
    for r in d[0]['LoadedMaterialResources']:
        sm = r['LoadedShaderMap']; u = sm['Content']['MaterialCompilationOutput']['UniformExpressionSet']
        params = []
        for p in u['UniformNumericParameters']:
            v = p.get('Value')
            if isinstance(v, dict): v = f"({v.get('R'):.4g},{v.get('G'):.4g},{v.get('B'):.4g},{v.get('A'):.4g})"
            params.append(f"[{p['ParameterInfo']['Name']}={v}]")
        data = base64.b64decode(u['UniformPreshaderData']['Data'])
        fields = u['UniformPreshaderFields']
        best = None
        for name, ops in (('5.8', OPS58), ('no-Modulo', OPS_NOMOD)):
            bad, slots = 0, {}
            for pre in u['UniformPreshaders']:
                code = data[pre['OpcodeOffset']:pre['OpcodeOffset'] + pre['OpcodeSize']]
                try:
                    st = decode(code, params, ops)
                    if len(st) != 1: bad += 1
                    expr = st[-1] if st else '?'
                except Exception as e:
                    bad += 1; expr = f'<{e}>'
                for f in fields[pre['FieldIndex']:pre['FieldIndex'] + pre['NumFields']]:
                    slots[f['BufferOffset']] = (f['Type'], expr)
            print(sm['ShaderMapId']['QualityLevel'], name, 'bad', bad, 'of', len(u['UniformPreshaders']))
            if best is None or bad < best[0]: best = (bad, name, slots)
        tex = {}
        for ti, group in enumerate(u['UniformTextureParameters']):
            for k, t in enumerate(group or []):
                tex.setdefault(ti, []).append(t['ParameterInfo']['Name'] if t.get('ParameterInfo') else f"tex{t['TextureIndex']}")
        report[str(sm['ShaderMapId']['QualityLevel']) + ':' + sm['ShaderMapId']['CookedShaderMapIdHash']] = dict(
            ops=best[1], slots={str(k): v for k, v in sorted(best[2].items())}, textures=tex)
    json.dump(report, open(out, 'w'), indent=1)


if __name__ == '__main__':
    run(sys.argv[1], sys.argv[2])
