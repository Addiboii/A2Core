"""Name packet opcodes from a server log: what we observed, the usmap struct, its fields.

  python3 tools/opcode_lookup.py 0x3645 3626           # opcodes (hex with or without 0x)
  python3 tools/opcode_lookup.py --find PartyMember    # structs whose name contains the text
  python3 tools/opcode_lookup.py 0x3645 --body 0a00... # also try parsing a logged body (hex)

Sources: tools/protocol_knowledge.py (observed names), the usmap
(research/local/tooling/mappings, packet structs in registration order = opcode order, see
tools/packet_schema.py) and research/local/tooling/opcodes.json (field widths learned from
captures, used as width overrides). game server family (port 13328). The login server (port 3700) numbers its own packets from 0x3900,
which overlaps the game family's later opcodes, so both are shown when both exist. A usmap struct
name is a mapping by order, not proof of the wire layout.
"""
import argparse
import json
from pathlib import Path

try:
    from .packet_schema import DEFAULT_USMAP, PacketSchema
    from .protocol_knowledge import PACKETS
except ImportError:
    from packet_schema import DEFAULT_USMAP, PacketSchema
    from protocol_knowledge import PACKETS

ROOT = Path(__file__).resolve().parents[1]
LEDGER = ROOT / 'research/local/tooling/opcodes.json'
WIDTH_BYTES = {'u8': 1, 'u16': 2, 'i32': 4}


def load_ledger(path=LEDGER):
    """{opcode: ledger entry} from the learned-widths file, or {} when it is absent."""
    path = Path(path)
    if not path.exists():
        return {}
    return {int(key): entry for key, entry in json.loads(path.read_text(encoding='utf-8')).items()}


def width_overrides(ledger):
    """Learned integer widths in PacketSchema's override form (field path -> bytes)."""
    return {field: WIDTH_BYTES[kind] for entry in ledger.values()
            for field, kind in (entry.get('widths') or {}).items() if kind in WIDTH_BYTES}


def describe(schema, ledger, opcode, families=('game', 'login')):
    """Text lines describing one opcode, and the first usmap struct found for it."""
    lines = [f'0x{opcode:04x}']
    if opcode in PACKETS:
        name, kind, replies = PACKETS[opcode]
        reply = ', '.join(f'0x{r:04x}' for r in replies if r != 0xffff)
        lines.append(f'  observed: {name} [{kind}]' + (f' -> {reply}' if reply else ''))
    first = None
    for fam in families:
        struct = schema.struct_for_opcode(fam, opcode)
        if struct is None:
            continue
        first = first or struct
        lines.append(f'  usmap ({fam}): {struct}')
        learned = ledger.get(opcode) if fam == 'game' else None
        if learned and learned.get('struct') == struct:
            lines.append(f"    learned widths from {learned.get('sample_count', 0)} captured samples")
        for name, kind, payload in schema.fields(struct):
            detail = payload if isinstance(payload, str) else ''
            lines.append(f'    {name}: {kind}' + (f' {detail}' if detail else ''))
    if first is None:
        lines.append('  usmap: no packet struct at this opcode')
    return lines, first


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('opcodes', nargs='*', help='opcodes, hex (0x3645 or 3645)')
    parser.add_argument('--find', help='list packet structs whose name contains this text')
    parser.add_argument('--body', help='hex body to parse with the (first) opcode\'s struct')
    parser.add_argument('--login', action='store_true', help='login-server opcodes only (port 3700)')
    parser.add_argument('--game', action='store_true', help='game-server opcodes only (port 13328)')
    parser.add_argument('--usmap', type=Path, default=DEFAULT_USMAP)
    parser.add_argument('--ledger', type=Path, default=LEDGER)
    args = parser.parse_args(argv)
    if not args.usmap.exists():
        parser.error(f'usmap not found: {args.usmap} (copy research/local/tooling/mappings from the research PC)')
    ledger = load_ledger(args.ledger)
    schema = PacketSchema(args.usmap, width_overrides(ledger))

    if args.find:
        needle = args.find.lower()
        for name in schema.game_structs + schema.login_structs:
            if needle in name.lower():
                fam, opcode = schema.opcode_for_struct(name)
                print(f'0x{opcode:04x} {fam:5} {name}')
    first = None
    for text in args.opcodes:
        families = ('login',) if args.login else ('game',) if args.game else ('game', 'login')
        lines, struct = describe(schema, ledger, int(text, 16), families)
        first = first or struct
        print('\n'.join(lines))
    if args.body:
        if first is None:
            parser.error('--body needs an opcode with a usmap struct')
        try:
            body = bytes.fromhex(args.body.replace(' ', ''))
            values, used = schema.parse_body(first, body)
            print(json.dumps(values, indent=2, default=str))
            print(f'  parsed {used} of {len(body)} bytes')
        except Exception as error:  # a candidate layout that does not fit is a finding, not a crash
            print(f'  body does not parse as {first}: {error}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
