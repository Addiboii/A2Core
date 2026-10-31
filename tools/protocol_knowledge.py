"""Observed local-build contracts; never infer opcodes from usmap ordinals.

This registry describes evidence, not a complete protocol implementation.
Only metadata/small gameplay fields are exposed; identities and keys are not.
"""
import math
import struct
try:
    from .starter_casts import parse_starter_cast
    from .aion_protocol import varuint
except ImportError:
    from starter_casts import parse_starter_cast
    from aion_protocol import varuint


# name, kind, expected reply opcodes. Requests with no known reply use None.
PACKETS = {
    0x3601: ('Client clock', 'telemetry', ()),
    0x3602: ('Ping request', 'request', (0x3603,)),
    0x3603: ('Ping reply', 'reply', ()),
    0x3613: ('Game login', 'request', (0xffff,)),
    0x3619: ('Name check', 'request', (0x361a,)),
    0x361b: ('Character creation', 'request', (0x361c,)),
    0x361f: ('Character selection', 'request', (0x3620, 0x3621)),
    0x3620: ('Character selection reply', 'reply', ()),
    0x3621: ('Prepare map entry', 'notification', ()),
    0x3622: ('Map load complete', 'request', (0x3623, 0x3633)),
    0x3623: ('Map load complete reply', 'reply', ()),
    0x3633: ('Own character appearance', 'notification', ()),
    0x3630: ('Logout request', 'request', (0x3631,)),
    0x3631: ('Logout reply', 'reply', ()),
    0x3648: ('Character stats request', 'request', (0x3649,)),
    0x3649: ('Character stats reply', 'reply', ()),
    0x364c: ('HP/resource update', 'notification', ()),
    0x3700: ('Position report A', 'telemetry', ()),
    0x3701: ('Position report B', 'telemetry', ()),
    0x3718: ('Stationary position report', 'telemetry', ()),
    0x3800: ('Skill start request', 'request', (0x3801,)),
    0x3801: ('Skill start result', 'reply', ()),
    0x381a: ('Skill charging start request (Dev SkillChargingStart_RQ + 0x157)', 'request', (0x381b, 0x3802)),
    0x381b: ('Skill charging start result', 'reply', ()),
    0x3824: ('Basic attack request (Dev BasicAtk_RQ + 0x157)', 'request', (0x3825,)),
    0x3826: ('Basic attack toggle on', 'request', (0x3827,)),
    0x3828: ('Basic attack toggle off', 'request', (0x3829, 0x3806)),
    0x3802: ('Skill start notification', 'notification', ()),
    0x3806: ('Skill end notification', 'notification', ()),
    0x383a: ('Combat on request', 'request', (0x383b,)),
    0x383b: ('Combat on result', 'reply', ()),
    0x383c: ('Combat off request', 'request', (0x383d,)),
    0x383d: ('Combat off result', 'reply', ()),
    0x5100: ('Learned skill list', 'notification', ()),
    0x5600: ('Skill hotbar', 'notification', ()),
    0x5610: ('Inventory request', 'request', (0x5611,)),
    0x5611: ('Inventory reply', 'reply', ()),
    0xe259: ('NCGuard data request (candidate)', 'request', None),
    0xe25a: ('NCGuard data result (candidate)', 'reply', None),
    0xffff: ('Compressed packet bundle', 'envelope', ()),
}

EVIDENCE = {
    'ncguard': {'status': 'candidate_struct_mapping', 'source': 'research/e259-ncguard.md',
                'rules': ['e259/e25a names inferred from schema adjacency and observed envelope',
                          'No per-request acknowledgement established',
                          'Opaque payload content and required response remain unknown']},
    'spawn': {'status': 'decoder_verified', 'source': 'research/spawn-diagnosis.md',
              'rvas': ['0x91f3870', '0x8498b80', '0x8495470', '0x84945b0'],
              'rules': ['HP uses unsigned varints', 'bool bits are shared across records',
                        'map identity follows abnormal and equipment arrays']},
    'skills': {'status': 'mixed_evidence', 'source': 'research/starter-skills.md',
               'request_rva': '0x8f7db28', 'result_rva': '0x91a6a70',
               'start': 'Mask-zero form matches pinned public a2kit fixtures; decoder unreadable',
               'resources_rva': '0x91f88f0'},
    'limits': ['RVA evidence is build-specific; verify the image before live inspection',
               'usmap ordinal opcodes are not verified wire mappings',
               '200 starting MP and uphill position are user reference settings',
               'damage, world targets, and CombatStateUpdate opcode remain unresolved'],
}


def describe(opcode):
    name, kind, replies = PACKETS.get(opcode, ('Unknown', 'unknown', None))
    return {'opcode': f'0x{opcode:04x}', 'name': name, 'kind': kind,
            'evidence': ('candidate_struct_mapping' if opcode in (0xe259, 0xe25a)
                         else 'observed_local_build' if opcode in PACKETS else 'unknown'),
            'expected_replies': None if replies is None else [f'0x{x:04x}' for x in replies]}


def summarize_body(body):
    if len(body) < 2:
        return {'opcode': None, 'name': 'Truncated opcode', 'body_len': len(body)}
    opcode = int.from_bytes(body[:2], 'little')
    result = {**describe(opcode), 'body_len': len(body)}
    if opcode in (0xe259, 0xe25a):
        result.update(_ncguard_metadata(body, opcode))
    if opcode == 0x3800:
        cast = parse_starter_cast(body)
        result['supported_shape'] = cast is not None
        if len(body) >= 8:
            result['skill_id'] = int.from_bytes(body[4:8], 'little')
        if cast:
            result['client_action_uid'] = cast['client_action_uid']
    # Only the observed fixed-length stationary variant. Other variants remain
    # classified as telemetry without guessing their position offsets.
    if opcode == 0x3718 and len(body) == 28:
        position = struct.unpack_from('<3f', body, 4)
        if all(math.isfinite(x) for x in position):
            result['position_candidate'] = list(position)
    return result


def _ncguard_metadata(body, opcode):
    """Only observed structural sizes; never return bytes, nonces or opaque fields."""
    result = {'envelope_shape': 'unrecognized', 'outer_length_valid': False}
    try:
        size, start = varuint(body, 4 if opcode == 0xe25a else 2)
    except ValueError:
        return result
    if size != len(body) - start:
        return result
    result.update(outer_length_valid=True, vector_bytes=size)
    if opcode == 0xe25a:
        result['result_candidate'] = int.from_bytes(body[2:4], 'little')
    blob = memoryview(body)[start:]
    header = bytes.fromhex('1400000000000e001800040008000c00100014000e000000')
    if size < 84 or blob[:24] != header:
        return result
    payload_at = 40 + int.from_bytes(blob[40:44], 'little')
    tag_at = 32 + int.from_bytes(blob[32:36], 'little')
    if payload_at != 44 or not 48 <= tag_at <= size - 36:
        return result
    payload_size = int.from_bytes(blob[44:48], 'little')
    tag_size = int.from_bytes(blob[tag_at:tag_at+4], 'little')
    if tag_at != 48 + payload_size or tag_size != 32 or tag_at + 36 != size:
        return result
    candidate_length = int.from_bytes(blob[36:40], 'little')
    result.update(envelope_shape='observed_five_field_layout', payload_bytes=payload_size,
                  trailing_vector_bytes=tag_size, candidate_unpadded_bytes=candidate_length,
                  block_rounding_matches=((candidate_length + 15) // 16 * 16 == payload_size))
    return result
