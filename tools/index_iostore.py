"""Index exposed IoStore v5 chunk tables and locate payload blocks without decryption."""

import argparse
from collections import Counter
from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3
import struct

try:
    from .inspect_formats import utoc_metadata
except ImportError:
    from inspect_formats import utoc_metadata

MAX_TOC_BYTES = 256 * 1024 * 1024


def read_tables(path):
    meta = utoc_metadata(path)
    if not meta.get("parsed"):
        raise ValueError("Only IoStore v5 is supported")
    if path.stat().st_size > MAX_TOC_BYTES:
        raise ValueError("TOC exceeds size limit")
    data = path.read_bytes()
    count, blocks = meta["entry_count"], meta["compression_block_count"]
    chunk_start = meta["header_size"]
    offset_start = chunk_start + count * 12
    block_start = offset_start + count * 10 + 4 * (
        meta["perfect_hash_seed_count"] + meta["perfect_hash_overflow_count"])
    end = block_start + blocks * 12
    if end > len(data) or not meta["compression_block_size"]:
        raise ValueError("Invalid chunk/block table bounds")
    partition_size = struct.unpack_from("<Q", data, 88)[0]
    if not partition_size or not meta["partition_count"]:
        raise ValueError("Invalid partition dimensions")
    rows = []
    seen = set()
    block_size = meta["compression_block_size"]
    for index in range(count):
        chunk = data[chunk_start + index * 12:chunk_start + (index + 1) * 12]
        if chunk in seen:
            raise ValueError("Duplicate chunk ID within container")
        seen.add(chunk)
        entry = data[offset_start + index * 10:offset_start + (index + 1) * 10]
        offset, length = int.from_bytes(entry[:5], "big"), int.from_bytes(entry[5:], "big")
        first = offset // block_size
        last = (offset + length - 1) // block_size if length else first
        if length and last >= blocks:
            raise ValueError("Chunk exceeds logical block table")
        rows.append((chunk.hex(), chunk[11], offset, length, first, last))
    block_rows = []
    for index in range(blocks):
        entry = data[block_start + index * 12:block_start + (index + 1) * 12]
        physical = int.from_bytes(entry[:5], "little")
        compressed = int.from_bytes(entry[5:8], "little")
        plain = int.from_bytes(entry[8:11], "little")
        method = entry[11]
        if method > len(meta["compression_methods"]) or not 0 < plain <= block_size:
            raise ValueError("Invalid block compression fields")
        if not compressed or (method == 0 and compressed != plain):
            raise ValueError("Invalid block sizes")
        partition, local = divmod(physical, partition_size)
        stored = (compressed + 15) & ~15 if meta["encrypted"] else compressed
        if partition >= meta["partition_count"] or local + stored > partition_size:
            raise ValueError("Block exceeds partition bounds")
        block_rows.append((index, partition, local, compressed, plain, method, stored))
    # Chunks may begin/end partway through a block, but cannot reference padding.
    for _, _, offset, length, first, last in rows:
        if length and ((offset + length - 1) % block_size >= block_rows[last][4]
                       or offset % block_size >= block_rows[first][4]):
            raise ValueError("Chunk references block padding")
    return meta, hashlib.sha256(data).hexdigest(), rows, block_rows


def build_index(install, output):
    # Preserve earlier indexes and do not leave a partial successful-looking file.
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb"):
        pass
    try:
        with closing(sqlite3.connect(output)) as db, db:
            db.executescript("""
                CREATE TABLE containers (path TEXT PRIMARY KEY, toc_sha256 TEXT,
                    encrypted INTEGER, block_size INTEGER, methods_json TEXT);
                CREATE TABLE chunks (container TEXT, chunk_id TEXT, type_byte INTEGER,
                    logical_offset INTEGER, logical_size INTEGER, first_block INTEGER,
                    last_block INTEGER, PRIMARY KEY(container, chunk_id));
                CREATE TABLE blocks (container TEXT, block_index INTEGER, partition INTEGER,
                    physical_offset INTEGER, compressed_size INTEGER, plain_size INTEGER,
                    method_index INTEGER, stored_size INTEGER,
                    PRIMARY KEY(container, block_index));
                PRAGMA user_version=1;
            """)
            paths = sorted(install.rglob("*.utoc"))
            if not paths:
                raise ValueError("No TOCs found")
            counts = Counter()
            for path in paths:
                name = path.relative_to(install).as_posix()
                meta, digest, chunks, blocks = read_tables(path)
                # Check the referenced UCAS ranges against actual files.
                sizes = {}
                for _, partition, physical, _, _, _, stored in blocks:
                    if partition not in sizes:
                        payload = path.with_suffix(".ucas") if partition == 0 else path.with_name(
                            path.stem + f"_s{partition}.ucas")
                        sizes[partition] = payload.stat().st_size
                    if physical + stored > sizes[partition]:
                        raise ValueError(f"Block exceeds UCAS file: {name}")
                db.execute("INSERT INTO containers VALUES (?,?,?,?,?)", (
                    name, digest, meta["encrypted"], meta["compression_block_size"],
                    json.dumps(meta["compression_methods"])))
                db.executemany("INSERT INTO chunks VALUES (?,?,?,?,?,?,?)",
                               ((name, *row) for row in chunks))
                db.executemany("INSERT INTO blocks VALUES (?,?,?,?,?,?,?,?)",
                               ((name, *row) for row in blocks))
                counts.update(row[1] for row in chunks)
            db.execute("CREATE INDEX chunk_lookup ON chunks(chunk_id)")
            repeated = db.execute("SELECT COUNT(*) FROM (SELECT chunk_id FROM chunks GROUP BY chunk_id HAVING COUNT(*)>1)").fetchone()[0]
            result = {"containers": len(paths), "chunk_records": sum(counts.values()),
                      "type_byte_counts": dict(sorted(counts.items())),
                      "ids_in_multiple_containers": repeated,
                      "note": "IDs and byte types do not establish asset names or patch precedence. No payload is decrypted."}
        return result
    except BaseException:
        output.unlink(missing_ok=True)
        raise


def lookup(index, chunk_id):
    if len(chunk_id) != 24 or any(c not in "0123456789abcdefABCDEF" for c in chunk_id):
        raise ValueError("Chunk ID must be 24 hexadecimal characters")
    with closing(sqlite3.connect(index.resolve().as_uri() + "?mode=ro", uri=True)) as db:
        db.row_factory = sqlite3.Row
        rows = db.execute("""SELECT chunks.*, encrypted, block_size, methods_json
            FROM chunks JOIN containers ON chunks.container=containers.path
            WHERE chunk_id=? ORDER BY container""", (chunk_id.lower(),)).fetchall()
        result = []
        for row in rows:
            item = dict(row)
            item["blocks"] = [dict(b) for b in db.execute("""SELECT * FROM blocks
                WHERE container=? AND block_index BETWEEN ? AND ? ORDER BY block_index""",
                (row["container"], row["first_block"], row["last_block"]))] if row["logical_size"] else []
            result.append(item)
        return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_subparsers(dest="action", required=True)
    build = actions.add_parser("build")
    build.add_argument("--install", required=True, type=Path)
    build.add_argument("--output", required=True, type=Path)
    find = actions.add_parser("lookup")
    find.add_argument("index", type=Path)
    find.add_argument("chunk_id")
    args = parser.parse_args()
    try:
        if args.action == "build":
            install, output = args.install.resolve(), args.output.resolve()
            if not install.is_dir() or output.is_relative_to(install):
                parser.error("Provide an installation and an output outside it")
            result = build_index(install, output)
        else:
            result = lookup(args.index, args.chunk_id)
    except (OSError, ValueError, sqlite3.Error, struct.error) as error:
        parser.exit(1, f"Index operation failed: {error}\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
