"""Test decoder for the supported spawn subset, from live receiver order.

Rejects extra bytes and walks counts rather than relying on old sample offsets.
"""
import struct
from tools.aion_protocol import Reader


def decode_spawn(body):
    r = Reader(body)
    assert r.uint(2) == 0x3633
    result = {'key': r.varuint()}
    assert r.uint(4) == 0x2006
    assert r.uint(1) == 0x37
    result['name'] = r.take(r.varuint()).decode()
    # Detail prefix already compared independently with the official sample.
    r.take(44 + 118)
    assert r.uint(2) == 0
    result['position'] = struct.unpack('<3f', r.take(12))
    r.take(6)  # direction f32 and movement direction u16
    result['live'] = r.uint(1)
    result['hp'], result['hp_max'] = r.varuint(), r.varuint()
    pools = struct.unpack('<13i', r.take(52))
    result['mp'], result['mp_max'] = pools[:2]
    r.take(r.varuint() * 6)
    assert r.varuint() == 0  # no abnormals
    equips = []
    bool_index = 8
    bool_byte = 0

    def boolean():
        nonlocal bool_index, bool_byte
        if bool_index == 8:
            bool_byte, bool_index = r.uint(1), 0
        value = bool(bool_byte & (1 << bool_index))
        bool_index += 1
        return value

    for _ in range(r.varuint()):
        item, enchant, slot, expiry = struct.unpack('<iBBq', r.take(14))
        expired = boolean()
        skin = r.uint(4)
        assert r.varuint() == 0  # no dyes
        r.take(r.varuint() * 4)
        display, default_mesh, override = boolean(), boolean(), boolean()
        fx = r.varuint()
        equips.append((item, slot, expired, display, default_mesh, override))
    result['equips'] = equips
    result['reason'] = r.uint(1)
    result['map_key'], result['map_id'] = r.uint(8), r.uint(4)
    result['subzone'], result['dbid'] = r.uint(4), r.uint(8)
    result['customizing'] = r.take(r.varuint())
    result['purchased'] = boolean()
    result['voice'] = r.uint(1)
    r.take(16)  # conqueror
    result['power'] = (r.uint(8), r.uint(8))
    for _ in range(2):
        r.take(r.varuint() * 3)
    assert r.offset == len(body), (r.offset, len(body))
    return result
