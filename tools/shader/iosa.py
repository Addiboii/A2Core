"""IoStore shader code archive header (version 1) parser."""
import struct

def read_iostore_archive(path):
    b = open(path, 'rb').read()
    o = 4
    def arr(size):
        nonlocal o
        n, = struct.unpack_from('<i', b, o); o += 4
        s = o; o += n * size
        return [b[s + i * size:s + (i + 1) * size] for i in range(n)]
    a = dict(version=struct.unpack_from('<I', b)[0])
    a['map_hashes'] = arr(20)
    a['shader_hashes'] = arr(20)
    a['group_chunks'] = arr(12)
    a['map_entries'] = [struct.unpack('<II', x) for x in arr(8)]
    a['shader_entries'] = [struct.unpack('<Q', x)[0] for x in arr(8)]
    a['group_entries'] = [struct.unpack('<IIII', x) for x in arr(16)]
    a['indices'] = [struct.unpack('<I', x)[0] for x in arr(4)]
    assert o == len(b), (path, o, len(b))
    return a
