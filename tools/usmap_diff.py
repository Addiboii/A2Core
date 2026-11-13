"""What a new client build changed in its type information: compare two usmap mappings.

The packet structs (DevPacketData_*) matter most: their order in the mappings is the catalogue order our message numbers
derive from (tools/packet_schema.py), so a message added or removed in a family moves the number of every message after
it. The report lists added/removed/changed structs and enums, per packet family where the order first differs, and the
derived opcodes that move. Idea from the community fork (its usmap_diff.py); written here on our parsers.

    python tools/usmap_diff.py <old.usmap> <new.usmap> [--json <out.json>]
"""
import argparse
import json
from pathlib import Path

try:
    from .packet_schema import PacketSchema
    from .usmap_dump import UsmapParser
except ImportError:   # run as a script from tools/
    from packet_schema import PacketSchema
    from usmap_dump import UsmapParser

FAMILIES = ('DevPacketData_GameServer_', 'DevPacketData_LoginServer_', 'DevPacketData_Common_')


def type_key(t):
    """A property type as a comparable nested list (kind, struct, enum, inner, value)."""
    if t is None:
        return None
    return [t.get('type'), t.get('struct'), t.get('enum'), type_key(t.get('inner')), type_key(t.get('value'))]


def struct_key(struct):
    return {'super': struct['super'],
            'properties': [[index, p['name'], type_key(p['type'])] for index, p in sorted(struct['properties'].items())]}


def first_difference(old, new):
    """The first position at which two orders differ (None when equal)."""
    for i, (a, b) in enumerate(zip(old, new)):
        if a != b:
            return i
    return None if len(old) == len(new) else min(len(old), len(new))


def compare(old, new):
    """The differences between two UsmapParser results, JSON-ready."""
    out = {'counts': {'old': {'structs': len(old.structs), 'enums': len(old.enums)},
                      'new': {'structs': len(new.structs), 'enums': len(new.enums)}}}
    both = sorted(set(old.structs) & set(new.structs))
    out['structs_added'] = sorted(set(new.structs) - set(old.structs))
    out['structs_removed'] = sorted(set(old.structs) - set(new.structs))
    out['structs_changed'] = {name: {'old': struct_key(old.structs[name]), 'new': struct_key(new.structs[name])}
                              for name in both if struct_key(old.structs[name]) != struct_key(new.structs[name])}
    out['enums_added'] = sorted(set(new.enums) - set(old.enums))
    out['enums_removed'] = sorted(set(old.enums) - set(new.enums))
    out['enums_changed'] = {}
    for name in sorted(set(old.enums) & set(new.enums)):
        a, b = old.enums[name], new.enums[name]
        if a != b:
            out['enums_changed'][name] = {'added': {str(k): v for k, v in b.items() if a.get(k) != v},
                                          'removed': {str(k): v for k, v in a.items() if b.get(k) != v}}
    out['families'] = {}
    for prefix in FAMILIES:
        a = [n for n in old.structs if n.startswith(prefix)]
        b = [n for n in new.structs if n.startswith(prefix)]
        place = {n: i for i, n in enumerate(a)}
        moved = [{'name': n, 'old': place[n], 'new': i} for i, n in enumerate(b) if n in place and place[n] != i]
        out['families'][prefix.rstrip('_')] = {
            'old': len(a), 'new': len(b), 'added': [n for n in b if n not in place],
            'removed': [n for n in a if n not in set(b)], 'first_difference': first_difference(a, b),
            'moved': len(moved), 'moved_examples': moved[:20]}
    return out


def opcode_changes(old_path, new_path):
    """{message: [old opcode, new opcode]} for game and login messages whose derived number changes."""
    def numbers(path):
        schema = PacketSchema(path)
        return {name: op for op, name in {**schema.game_opcode_map(), **schema.login_opcode_map()}.items()}
    a, b = numbers(old_path), numbers(new_path)
    return {name: [f'0x{a[name]:04x}', f'0x{b[name]:04x}'] for name in a if name in b and a[name] != b[name]}


def summary(result, changes):
    c = result['counts']
    lines = [f"structs {c['old']['structs']} -> {c['new']['structs']}: +{len(result['structs_added'])} "
             f"-{len(result['structs_removed'])} ~{len(result['structs_changed'])}",
             f"enums {c['old']['enums']} -> {c['new']['enums']}: +{len(result['enums_added'])} "
             f"-{len(result['enums_removed'])} ~{len(result['enums_changed'])}"]
    for family, f in result['families'].items():
        lines.append(f"{family}: {f['old']} -> {f['new']}, +{len(f['added'])} -{len(f['removed'])}, "
                     f"first difference at {f['first_difference']}, {f['moved']} moved")
        lines += [f'  + {n}' for n in f['added']] + [f'  - {n}' for n in f['removed']]
    packets = [n for n in result['structs_changed'] if n.startswith('DevPacketData_')]
    lines.append(f'packet structs changed: {len(packets)}')
    lines += [f'  ~ {n}' for n in packets]
    lines.append(f'derived opcodes that change: {len(changes)}')
    lines += [f'  {n}: {a} -> {b}' for n, (a, b) in sorted(changes.items())]
    return '\n'.join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('old', type=Path)
    parser.add_argument('new', type=Path)
    parser.add_argument('--json', type=Path, help='also write the full comparison here')
    args = parser.parse_args(argv)
    result = compare(UsmapParser(str(args.old)), UsmapParser(str(args.new)))
    changes = opcode_changes(args.old, args.new)
    print(summary(result, changes))
    if args.json:
        args.json.write_text(json.dumps({**result, 'opcode_changes': changes}, indent=1) + '\n', encoding='utf-8')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
