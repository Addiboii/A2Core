"""Evidence-aware development diagnostics. Run --help for commands.

Read-only unless --output is specified. No client launch, memory access,
packet sending, or current-router replay occurs during diagnosis.
"""
import argparse
from collections import Counter
import json
from pathlib import Path
try:
    from .aion_protocol import varuint, Incomplete
    from .probe_login_reply import RC4
    from .protocol_knowledge import describe, summarize_body, EVIDENCE, PACKETS
except ImportError:
    from aion_protocol import varuint, Incomplete
    from probe_login_reply import RC4
    from protocol_knowledge import describe, summarize_body, EVIDENCE, PACKETS

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / 'research/local'


def decode_snapshot(raw, key):
    """Decode all complete frames, including sessions beyond 16384 frames.

    Preserve RC4 continuity; report an incomplete tail instead of misclassifying
    it as a malformed packet when the live capture is still growing.
    """
    if len(key) != 32:
        raise ValueError('Expected a 32-byte local injected key')
    cipher = RC4(key)
    offset = 0
    bodies = []
    while offset < len(raw):
        try:
            logical, start = varuint(raw, offset)
        except Incomplete:
            break
        if not 6 <= logical <= 16 * 1024 * 1024:
            raise ValueError(f'Invalid frame length at offset {offset}')
        end = start + logical - 4
        if end > len(raw):
            break
        bodies.append(cipher.crypt(raw[start:end]))
        offset = end
    return bodies, len(raw) - offset


def summarize_bodies(bodies):
    counts, skills, unknown = Counter(), Counter(), Counter()
    unsupported = 0
    position = None
    for body in bodies:
        row = summarize_body(body)
        code = row['opcode']
        counts[code or 'truncated'] += 1
        if row.get('kind') == 'unknown':
            unknown[code] += 1
        if code == '0x3800':
            skills[str(row.get('skill_id', 'truncated'))] += 1
            unsupported += not row.get('supported_shape', False)
        if 'position_candidate' in row:
            position = row['position_candidate']
    stages = [('0x3613', 'game_login_received'), ('0x361b', 'character_creation_received'),
              ('0x361f', 'character_selection_received'), ('0x3622', 'load_complete_received')]
    last = next((label for code, label in reversed(stages) if counts[code]), 'no_game_login_observed')
    movement = sum(counts[x] for x in ('0x3700', '0x3701', '0x3718'))
    if movement:
        last = 'movement_reports_received'
    return {'frames': len(bodies), 'last_observed_stage': last,
            'movement_reports': movement, 'skill_requests': dict(skills),
            'unsupported_skill_shapes': unsupported, 'last_stationary_position_candidate': position,
            'opcode_counts': dict(counts), 'unknown_opcode_counts': dict(unknown)}


def read_json(path):
    try:
        return json.loads(path.read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return None


def diagnose(chain):
    report = {'chain': chain.name, 'connections': [], 'errors': [],
              'manifest': read_json(chain / 'manifest.json'),
              'interpretation': 'Observed inbound traffic; not proof of rendering or skill effects.'}
    for error in sorted(chain.glob('*/error.json')):
        report['errors'].append({'server': error.parent.name, 'error': read_json(error)})
    for capture in sorted((chain / 'game').glob('followup-*.bin')):
        peer = capture.stem.removeprefix('followup-')
        key_path = capture.with_name(f'injected-key-{peer}.bin')
        row = {'peer_port': peer}
        try:
            bodies, trailing = decode_snapshot(capture.read_bytes(), key_path.read_bytes())
            row.update(summarize_bodies(bodies), incomplete_tail_bytes=trailing)
        except (OSError, ValueError) as exc:
            row['diagnostic_error'] = type(exc).__name__ + ': ' + str(exc)
        # Journal is authoritative for what this server version actually sent.
        events = capture.with_name(f'events-{peer}.jsonl')
        gaps, rejected, exchanges, partial = Counter(), Counter(), 0, 0
        if events.exists():
            with events.open(encoding='utf-8') as stream:
                for line in stream:
                    try:
                        event = json.loads(line)
                    except ValueError:
                        partial += 1
                        continue
                    request = event.get('request', {})
                    expected = request.get('expected_replies') or []
                    if expected and not set(expected).issubset(event.get('reply_opcodes', [])):
                        gaps[request.get('opcode', 'unknown')] += 1
                    if event.get('cast_accepted') is False:
                        rejected[str(request.get('skill_id', 'unknown'))] += 1
                    exchanges += 1
        row['recorded_exchange_count'] = exchanges
        row['reply_evidence'] = 'recorded_sent_replies' if events.exists() else 'unavailable_for_this_run'
        row['reply_contract_gaps'] = dict(gaps)
        row['rejected_skills'] = dict(rejected)
        row['incomplete_journal_lines'] = partial
        report['connections'].append(row)
    return report


def compare_reports(before, after):
    def totals(report):
        counts = Counter()
        for row in report['connections']:
            counts.update(row.get('opcode_counts', {}))
        return counts
    old, new = totals(before), totals(after)
    old_manifest, new_manifest = before.get('manifest') or {}, after.get('manifest') or {}
    old_hashes, new_hashes = old_manifest.get('source_sha256'), new_manifest.get('source_sha256')
    return {'before': before['chain'], 'after': after['chain'],
            'changed_source_files': (sorted(k for k in old_hashes.keys() | new_hashes.keys()
                                           if old_hashes.get(k) != new_hashes.get(k))
                                     if old_hashes is not None and new_hashes is not None else None),
            'opcode_count_deltas': {k: new[k] - old[k] for k in sorted(old.keys() | new.keys())
                                    if new[k] != old[k]},
            'note': 'Count differences also depend on session duration and user actions.'}


def resolve_chain(value):
    if value == 'latest':
        candidates = sorted(p for p in LOCAL.glob('chain-[0-9]*') if p.is_dir())
        if not candidates:
            raise ValueError('No chain runs found')
        return candidates[-1]
    path = Path(value)
    if not path.is_dir():
        path = LOCAL / value
    path = path.resolve()
    if not path.is_dir() or not path.is_relative_to(LOCAL.resolve()):
        raise ValueError('Expected a saved chain directory under research/local')
    return path


def table_rows(path, row_id=None, name=None, fields=None, limit=10):
    data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
    rows = data if isinstance(data, list) else [data]
    matches = []
    for row in rows:
        identity = row.get('ID')
        if isinstance(identity, dict):
            identity = identity.get('Value')
        if row_id is not None and identity != row_id:
            continue
        if name and name.casefold() not in str(row.get('Name', '')).casefold():
            continue
        matches.append({k: row[k] for k in fields if k in row} if fields else row)
    return {'matches': len(matches), 'rows': matches[:limit], 'truncated': len(matches) > limit}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    diag = sub.add_parser('diagnose', help='Summarize a current or historical local run')
    diag.add_argument('chain', nargs='?', default='latest')
    diag.add_argument('--compare', metavar='CHAIN')
    diag.add_argument('--json', action='store_true')
    diag.add_argument('--output', type=Path)
    sub.add_parser('knowledge', help='Show observed packet contracts and evidence limits')
    table = sub.add_parser('table', help='Query a decoded WorldProbe table without inline Python')
    table.add_argument('path', type=Path)
    table.add_argument('--id', type=int)
    table.add_argument('--name')
    table.add_argument('--fields', help='Comma-separated field names')
    table.add_argument('--limit', type=int, default=10)
    args = parser.parse_args(argv)
    try:
        if args.command == 'knowledge':
            print(json.dumps({'evidence': EVIDENCE, 'packets': [describe(x) for x in PACKETS]}, indent=2))
            return 0
        if args.command == 'table':
            if args.limit < 1:
                raise ValueError('limit must be positive')
            print(json.dumps(table_rows(args.path, args.id, args.name,
                                        args.fields.split(',') if args.fields else None, args.limit), indent=2))
            return 0
        report = diagnose(resolve_chain(args.chain))
        if args.compare:
            report['comparison'] = compare_reports(diagnose(resolve_chain(args.compare)), report)
        if args.output:
            output = args.output.resolve()
            if not output.is_relative_to(LOCAL.resolve()):
                raise ValueError('Diagnostic artifacts belong under research/local')
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        if args.json:
            print(json.dumps(report, indent=2))
        else:
            print(report['chain'])
            print('Saved server errors:', len(report['errors']))
            for error in report['errors']:
                print(' ', error['server'], json.dumps(error['error']))
            for row in report['connections']:
                print('Game', row['peer_port'], row.get('last_observed_stage', row.get('diagnostic_error')))
                print('  Skill requests:', row.get('skill_requests', {}))
                print('  Unsupported shapes:', row.get('unsupported_skill_shapes', 0))
                print('  Last stationary position:', row.get('last_stationary_position_candidate'))
                print('  Reply evidence:', row['reply_evidence'], 'gaps:', row['reply_contract_gaps'])
                print('  Unknown opcodes:', row.get('unknown_opcode_counts', {}))
            if 'comparison' in report:
                print('Comparison:', json.dumps(report['comparison']))
        return 0
    except (OSError, ValueError, KeyError) as exc:
        parser.error(str(exc))


if __name__ == '__main__':
    raise SystemExit(main())
