"""Assemble a bounded image range from saved probes; never read a live process."""
import argparse
import glob
import json
from pathlib import Path


def segments(report):
    for ref in report.get('reference_candidates', []):
        if 'context_hex' in ref:
            yield ref['context_rva'], bytes.fromhex(ref['context_hex'])
    followup = report.get('followup', {})
    if followup.get('status') == 'anchors_matched':
        for window in followup.get('windows', []):
            if window.get('status') == 'read' and 'hex' in window:
                yield window['rva'], bytes.fromhex(window['hex'])
        for ref in followup.get('direct_calls', {}).get('candidates', []):
            if 'context_hex' in ref:
                yield ref['context_rva'], bytes.fromhex(ref['context_hex'])


def assemble(reports, pid, rva, size):
    if rva < 0 or not 1 <= size <= 65536:
        raise ValueError('Request a nonnegative RVA and 1..65536 bytes.')
    data = bytearray(size)
    seen = bytearray(size)
    conflicts = set()
    sources = []
    for name, report in reports:
        if report.get('pid') != pid:
            continue
        used = False
        for start, value in segments(report):
            lo, hi = max(start, rva), min(start + len(value), rva + size)
            for address in range(lo, hi):
                index, byte = address - rva, value[address - start]
                if seen[index] and data[index] != byte:
                    conflicts.add(address)
                data[index] = byte
                seen[index] = 1
                used = True
        if used:
            sources.append(str(name))
    gaps = []
    index = 0
    while index < size:
        if seen[index]:
            index += 1
            continue
        start = index
        while index < size and not seen[index]:
            index += 1
        gaps.append({'rva': rva + start, 'size': index - start})
    status = 'conflict' if conflicts else 'incomplete' if gaps else 'complete'
    result = {'pid': pid, 'rva': rva, 'size': size, 'status': status,
              'covered_bytes': sum(seen), 'missing_ranges': gaps,
              'conflicting_rvas': sorted(conflicts), 'source_reports': sources,
              'limitation': 'PID filtering is not build identity. Use reports from one known build/session.'}
    if status == 'complete':
        result['hex'] = data.hex()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reports', required=True, help='Saved-report glob from one known build/session')
    parser.add_argument('--pid', type=int, required=True)
    parser.add_argument('--rva', type=lambda value: int(value, 0), required=True)
    parser.add_argument('--size', type=lambda value: int(value, 0), required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    paths = sorted(glob.glob(args.reports))
    if not paths:
        parser.error('No saved reports matched.')
    result = assemble(((path, json.loads(Path(path).read_text(encoding='utf-8'))) for path in paths),
                      args.pid, args.rva, args.size)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k != 'hex'}))


if __name__ == '__main__':
    main()
