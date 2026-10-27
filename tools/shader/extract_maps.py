"""Extract one material's shaders from an IoStore shader archive.

  extract_maps.py plan  <archive.ushaderbytecode> <out dir> <map index>...   writes chunks.txt + plan.json
  (run AssetProbe with ASSETPROBE_CHUNKS=<out dir>/chunks.txt and export dir <out dir>/chunks)
  extract_maps.py split <out dir> <oodle dll>                                writes dxil/m<map>_<vs|ps>_<shader>.dxil
"""
import ctypes
import json
import os
import struct
import sys

from iosa import read_iostore_archive

FREQ = {0: 'vs', 3: 'ps'}


def plan(archive, out, maps):
    a = read_iostore_archive(archive)
    os.makedirs(out, exist_ok=True)
    shaders, groups = [], set()
    for m in maps:
        off, n = a['map_entries'][m]
        for si in a['indices'][off:off + n]:
            p = a['shader_entries'][si]
            grp = (p >> 4) & ((1 << 30) - 1)
            groups.add(grp)
            shaders.append(dict(map=m, shader=si, freq=p & 0xF, group=grp, offset=p >> 34))
    with open(os.path.join(out, 'chunks.txt'), 'w') as f:
        f.write('\n'.join(a['group_chunks'][g].hex() for g in sorted(groups)))
    json.dump(dict(plan=shaders, groups={g: dict(chunk=a['group_chunks'][g].hex(), entry=a['group_entries'][g]) for g in groups}),
              open(os.path.join(out, 'plan.json'), 'w'), indent=1)
    print('shaders', len(shaders), 'groups', len(groups))


def split(out, oodle_path):
    oodle = ctypes.CDLL(os.path.abspath(oodle_path))
    dec = oodle.OodleLZ_Decompress
    dec.restype = ctypes.c_int64
    dec.argtypes = [ctypes.c_void_p, ctypes.c_int64, ctypes.c_void_p, ctypes.c_int64] + [ctypes.c_int] * 3 + [ctypes.c_void_p] * 5 + [ctypes.c_int]
    p = json.load(open(os.path.join(out, 'plan.json')))
    os.makedirs(os.path.join(out, 'dxil'), exist_ok=True)
    for g, info in p['groups'].items():
        _, _, usize, csize = info['entry']
        src = open(os.path.join(out, 'chunks', info['chunk'] + '.bin'), 'rb').read()
        if csize < usize:
            buf = ctypes.create_string_buffer(usize)
            n = dec(src, csize, buf, usize, 1, 0, 0, None, None, None, None, None, 0, 3)
            data = buf.raw[:n]
        else:
            data = src[:usize]
        members = [s for s in p['plan'] if str(s['group']) == g]
        offsets = sorted({s['offset'] for s in members}) + [len(data)]
        for s in members:
            blob = data[s['offset']:offsets[offsets.index(s['offset']) + 1]]
            i = blob.find(b'DXBC')
            if i < 0:
                continue
            size = struct.unpack_from('<I', blob, i + 24)[0]
            name = f"m{s['map']}_{FREQ.get(s['freq'], s['freq'])}_{s['shader']}.dxil"
            open(os.path.join(out, 'dxil', name), 'wb').write(blob[i:i + size])
            s['dxbc_size'] = size
    json.dump(p, open(os.path.join(out, 'plan.json'), 'w'), indent=1)
    for s in sorted((s for s in p['plan'] if s['freq'] == 3), key=lambda s: (s['map'], -s.get('dxbc_size', 0))):
        print(s['map'], s['shader'], s.get('dxbc_size'))


if __name__ == '__main__':
    if sys.argv[1] == 'plan':
        plan(sys.argv[2], sys.argv[3], [int(x) for x in sys.argv[4:]])
    else:
        split(sys.argv[2], sys.argv[3])
