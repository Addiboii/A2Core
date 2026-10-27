"""Inspect the observed v3 container-header package-reference layout.

This is a candidate layout validated against local catalogs, not a general UE
serializer. Optional-segment, redirect and name-table tails remain undecoded.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct

MAX_BYTES = 64 * 1024 * 1024


def parse(data):
    if not 24 <= len(data) <= MAX_BYTES or data[:4] != b'nCoI':
        raise ValueError('Invalid container header size or magic')
    if struct.unpack_from('<I', data, 4)[0] != 3:
        raise ValueError('Only observed header version 3 is supported')
    count = struct.unpack_from('<I', data, 16)[0]
    end_ids = 20 + count * 8
    if end_ids + 4 > len(data):
        raise ValueError('Truncated package ID array')
    size = struct.unpack_from('<I', data, end_ids)[0]
    start = end_ids + 4
    if size < count * 16 or start + size > len(data):
        raise ValueError('Invalid store-entry buffer size')
    buf = data[start:start + size]
    rows = []
    seen = set()
    for i in range(count):
        identifier = data[20 + i * 8:28 + i * 8].hex()
        if identifier in seen:
            raise ValueError('Duplicate package ID')
        seen.add(identifier)
        entry = i * 16
        arrays = []
        for field, width in ((0, 8), (8, 20)):
            length, relative = struct.unpack_from('<II', buf, entry + field)
            offset = entry + field + relative
            if length and (offset < count * 16 or offset + length * width > size):
                raise ValueError('Reference array outside store buffer')
            arrays.append([buf[j:j + width].hex()
                           for j in range(offset, offset + length * width, width)])
        rows.append({'package_id': identifier, 'imported_package_ids': arrays[0],
                     'candidate_shader_hashes': arrays[1]})
    return {'schema_version': 1, 'source_sha256': hashlib.sha256(data).hexdigest(),
            'container_id': data[8:16].hex(), 'package_count': count,
            'packages': rows, 'unparsed_tail_bytes': len(data) - start - size,
            'limitation': 'Candidate package-store layout; tail and package exports not decoded.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('header', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        local = Path(__file__).resolve().parents[1] / 'research/local'
        if not args.output.resolve().is_relative_to(local.resolve()):
            raise ValueError('Reports must stay under research/local/')
        if args.header.stat().st_size > MAX_BYTES:
            raise ValueError('Header exceeds size limit')
        report = parse(args.header.read_bytes())
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open('x', encoding='utf-8') as stream:
            json.dump(report, stream, indent=2)
        print(json.dumps({k: v for k, v in report.items() if k != 'packages'}, indent=2))
    except (OSError, ValueError) as error:
        parser.exit(1, f'Package-store inspection failed: {error}\n')


if __name__ == '__main__':
    main()
