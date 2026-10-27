"""Extract one bounded IoStore chunk; compressed methods require a decoder."""

import argparse
import hashlib
import json
from pathlib import Path

try:
    from .archive_keys import AES256
    from .index_iostore import read_tables
    from .oodle_decoder import OodleDecoder
except ImportError:
    from archive_keys import AES256
    from index_iostore import read_tables
    from oodle_decoder import OodleDecoder

MAX_CHUNK = 64 * 1024 * 1024


def extract(toc, chunk_id, decrypt=None, expected_sha256=None, limit=MAX_CHUNK, decoders=None):
    """Read fresh tables, remove AES padding, and slice logical block boundaries.

    decrypt takes ciphertext only. No archive-provided pathname becomes output.
    This does not verify IoStore signatures or package hashes.
    """
    meta, digest, chunks, blocks = read_tables(toc)
    if expected_sha256 is not None and digest != expected_sha256:
        raise ValueError("TOC hash differs from expected baseline")
    chunk = next((c for c in chunks if c[0] == chunk_id.lower()), None)
    if chunk is None:
        raise ValueError("Chunk not found in selected container")
    _, _, offset, length, first, last = chunk
    if length > limit or limit > MAX_CHUNK or limit < 0:
        raise ValueError("Chunk exceeds extraction limit")
    selected = blocks[first:last + 1] if length else []
    methods = sorted({meta['compression_methods'][b[5] - 1]
                      for b in selected if b[5]})
    decoders = decoders or {}
    missing = [m for m in methods if m not in decoders]
    if missing:
        raise ValueError("Decoder required for: " + ", ".join(missing))
    if meta['encrypted'] and decrypt is None:
        raise ValueError("Encrypted chunk requires an archive key")
    result = bytearray()
    for index, partition, physical, compressed, plain, method, stored in selected:
        payload = toc.with_suffix('.ucas') if partition == 0 else toc.with_name(
            toc.stem + f'_s{partition}.ucas')
        with payload.open('rb') as stream:
            stream.seek(physical)
            data = stream.read(stored)
        if len(data) != stored:
            raise ValueError("Truncated UCAS block")
        if meta['encrypted']:
            data = decrypt(data)
        if len(data) != stored:
            raise ValueError("Invalid decoded block size")
        data = data[:compressed]
        if method:
            data = decoders[meta['compression_methods'][method - 1]](data, plain)
        if len(data) != plain:
            raise ValueError('Decompressed block length mismatch')
        start = max(0, offset - index * meta['compression_block_size'])
        end = min(plain, offset + length - index * meta['compression_block_size'])
        if start > end:
            raise ValueError("Invalid logical slice")
        result.extend(data[start:end])
    if len(result) != length:
        raise ValueError("Decoded chunk length mismatch")
    report = {'chunk_id': chunk_id.lower(), 'toc_sha256': digest,
              'length': length, 'blocks': len(selected),
              'sha256': hashlib.sha256(result).hexdigest(),
              'encrypted': meta['encrypted'],
              'compression_methods': methods,
              'limitation': 'Payload decoded; signatures, package format and dependencies not verified.'}
    return bytes(result), report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--toc', type=Path, required=True)
    parser.add_argument('--chunk', required=True)
    parser.add_argument('--key-report', type=Path)
    parser.add_argument('--expected-toc-sha256')
    parser.add_argument('--oodle-dll', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        local = (Path(__file__).resolve().parents[1] / 'research/local').resolve()
        output = args.output.resolve()
        if not output.is_relative_to(local):
            raise ValueError('Extracted data must stay under research/local/')
        if output.exists():
            raise ValueError('Output already exists')
        decoders = {'Oodle': OodleDecoder(args.oodle_dll)} if args.oodle_dll else {}
        if args.key_report:
            key = bytes.fromhex(json.loads(args.key_report.read_text())['key_hex'])
            if len(key) != 32:
                raise ValueError('Expected AES-256 key')
            with AES256() as aes:
                data, report = extract(args.toc, args.chunk, lambda b: aes.decrypt(key, b),
                                       args.expected_toc_sha256, decoders=decoders)
        else:
            data, report = extract(args.toc, args.chunk,
                                   expected_sha256=args.expected_toc_sha256, decoders=decoders)
        if decoders:
            report['oodle_dll_sha256'] = decoders['Oodle'].sha256
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open('xb') as stream:
            stream.write(data)
        print(json.dumps(report, indent=2))
    except (OSError, ValueError, KeyError) as error:
        parser.exit(1, f'Extraction failed: {error}\n')


if __name__ == '__main__':
    main()
