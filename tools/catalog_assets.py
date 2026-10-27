"""Decrypt Pak indexes for hash checks and catalog IoStore directory names.

Does not decompress/export package payloads or write paths supplied by archives.
All decoded names stay in a local SQLite catalog, not extraction destinations.
"""

import argparse
from collections import Counter
from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3
import struct

try:
    from .archive_keys import AES256, load_targets, validate
    from .inspect_formats import pak_metadata, utoc_metadata
except ImportError:
    from archive_keys import AES256, load_targets, validate
    from inspect_formats import pak_metadata, utoc_metadata

NULL = 0xffffffff
MAX_TABLE = 2_000_000


class Reader:
    def __init__(self, data):
        self.data, self.offset = data, 0

    def take(self, count):
        if count < 0 or self.offset + count > len(self.data):
            raise ValueError("Directory data exceeds bounds")
        value = self.data[self.offset:self.offset + count]
        self.offset += count
        return value

    def integer(self, signed=False):
        return int.from_bytes(self.take(4), "little", signed=signed)

    def string(self):
        count = self.integer(signed=True)
        if abs(count) > 32768:
            raise ValueError("FString exceeds configured limit")
        if not count:
            return ""
        width = 2 if count < 0 else 1
        value = self.take(abs(count) * width)
        if value[-width:] != bytes(width):
            raise ValueError("Missing FString terminator")
        result = value[:-width].decode("utf-16le" if count < 0 else "utf-8")
        if "\0" in result:
            raise ValueError("Embedded FString terminator")
        return result

    def rows(self, width):
        count = self.integer()
        if count > MAX_TABLE:
            raise ValueError("Directory array exceeds limit")
        return list(struct.iter_unpack("<" + "I" * width, self.take(count * width * 4)))


def directory_entries(data, chunk_ids, encrypted):
    reader = Reader(data)
    mount = reader.string()
    directories, files = reader.rows(4), reader.rows(3)
    count = reader.integer()
    if count > MAX_TABLE:
        raise ValueError("String table exceeds limit")
    names = [reader.string() for _ in range(count)]
    padding = len(data) - reader.offset
    if padding > (15 if encrypted else 0):
        raise ValueError("Unexpected bytes after directory resource")
    if not directories:
        raise ValueError("Directory index has no root")
    for name in names:
        if not name or name in (".", "..") or "/" in name or "\\" in name:
            raise ValueError("Invalid directory/file component")
    seen_dirs, seen_files = set(), set()
    pending = [(0, "", 0)]
    result = []
    while pending:
        index, parent, depth = pending.pop()
        if index >= len(directories) or index in seen_dirs or depth > 256:
            raise ValueError("Invalid/cyclic directory hierarchy")
        seen_dirs.add(index)
        name, child, sibling, first_file = directories[index]
        if index == 0:
            if name != NULL or sibling != NULL:
                raise ValueError("Unexpected root directory fields")
            path = ""
        else:
            if name >= len(names):
                raise ValueError("Directory name index out of bounds")
            path = parent + names[name] + "/"
        if sibling != NULL:
            pending.append((sibling, parent, depth))
        if child != NULL:
            pending.append((child, path, depth + 1))
        while first_file != NULL:
            if first_file >= len(files) or first_file in seen_files:
                raise ValueError("Invalid/cyclic file chain")
            seen_files.add(first_file)
            name, next_file, user_data = files[first_file]
            if name >= len(names) or user_data >= len(chunk_ids):
                raise ValueError("File name/chunk index out of bounds")
            chunk = chunk_ids[user_data]
            result.append((mount + path + names[name], user_data, chunk.hex(), chunk[11]))
            first_file = next_file
    if len(seen_dirs) != len(directories) or len(seen_files) != len(files):
        raise ValueError("Unreachable directory/file records")
    return {"mount": mount, "directories": len(directories), "files": len(files),
            "strings": len(names), "padding_bytes": padding}, result


def read_directory(path, key, aes):
    meta = utoc_metadata(path)
    if not meta.get("parsed"):
        raise ValueError("Unsupported TOC")
    if path.stat().st_size > 256 * 1024 * 1024:
        raise ValueError("TOC exceeds limit")
    data = path.read_bytes()
    count = meta["entry_count"]
    start = meta["header_size"]
    chunk_end = start + count * 12
    offset = start + count * 22 + 4 * (meta["perfect_hash_seed_count"] + meta["perfect_hash_overflow_count"])
    offset += meta["compression_block_count"] * 12
    method_width = struct.unpack_from("<I", data, 40)[0]
    offset += len(meta["compression_methods"]) * method_width
    if offset > len(data):
        raise ValueError("TOC tables exceed file")
    if meta["signed"]:
        if offset + 4 > len(data):
            raise ValueError("Truncated signature section")
        size = struct.unpack_from("<I", data, offset)[0]
        if not 1 <= size <= 8192:
            raise ValueError("Unexpected signature length")
        offset += 4 + size * 2 + meta["compression_block_count"] * 20
    end = offset + meta["directory_index_size"]
    if end > len(data) or chunk_end > len(data):
        raise ValueError("Directory/chunk table exceeds TOC")
    if not meta["directory_index_size"]:
        return {"files": 0, "status": "no_directory", "toc_sha256": hashlib.sha256(data).hexdigest()}, []
    decoded = data[offset:end]
    if meta["encrypted"]:
        decoded = aes.decrypt(key, decoded)
    chunk_ids = [data[i:i + 12] for i in range(start, chunk_end, 12)]
    summary, rows = directory_entries(decoded, chunk_ids, meta["encrypted"])
    return {**summary, "status": "decoded", "toc_sha256": hashlib.sha256(data).hexdigest(),
            "encrypted": meta["encrypted"], "directory_offset": offset}, rows


def catalog(install, key, output):
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb"):
        pass
    summary = {"schema_version": 1, "key_sha256": hashlib.sha256(key).hexdigest(),
               "pak_indexes": [], "containers": []}
    extensions = Counter()
    try:
        with AES256() as aes, closing(sqlite3.connect(output)) as db, db:
            if not validate(key, load_targets(install), aes.decrypt):
                raise ValueError("Key does not validate against the selected Pak indexes")
            db.executescript("""
                CREATE TABLE assets (container TEXT, path TEXT, toc_entry INTEGER, chunk_id TEXT, type_byte INTEGER);
                CREATE TABLE containers (path TEXT PRIMARY KEY, metadata_json TEXT);
                PRAGMA user_version=1;
            """)
            for path in sorted(install.rglob("*.pak")):
                if "Paks" not in path.parts:
                    continue
                meta = pak_metadata(path)
                if not meta.get("parsed") or not 0 < meta["index_size"] <= 16 * 1024 * 1024:
                    raise ValueError("Unsupported Pak index")
                with path.open("rb") as stream:
                    stream.seek(meta["index_offset"])
                    data = stream.read(meta["index_size"])
                if len(data) != meta["index_size"]:
                    raise ValueError("Truncated Pak index")
                if meta["index_encrypted"]:
                    data = aes.decrypt(key, data)
                matched = hashlib.sha1(data).hexdigest() == meta["index_sha1"]
                summary["pak_indexes"].append({"path": path.relative_to(install).as_posix(),
                                                "exact_decrypted_sha1_match": matched})
            for path in sorted(install.rglob("*.utoc")):
                name = path.relative_to(install).as_posix()
                meta, rows = read_directory(path, key, aes)
                db.execute("INSERT INTO containers VALUES (?,?)", (name, json.dumps(meta)))
                db.executemany("INSERT INTO assets VALUES (?,?,?,?,?)", ((name, *row) for row in rows))
                extensions.update(Path(row[0]).suffix.lower() for row in rows)
                summary["containers"].append({"path": name, **meta})
            db.execute("CREATE INDEX asset_path ON assets(path)")
            db.execute("CREATE INDEX asset_chunk ON assets(chunk_id)")
            summary["asset_records"] = db.execute("SELECT COUNT(*) FROM assets").fetchone()[0]
            summary["distinct_paths"] = db.execute("SELECT COUNT(DISTINCT path) FROM assets").fetchone()[0]
            summary["extension_counts"] = dict(extensions)
            summary["pak_hash_matches"] = sum(r["exact_decrypted_sha1_match"] for r in summary["pak_indexes"])
        return summary
    except BaseException:
        output.unlink(missing_ok=True)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", required=True, type=Path)
    parser.add_argument("--key-report", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    local = Path(__file__).resolve().parents[1] / "research/local"
    if not output.is_relative_to(local.resolve()) or output.is_relative_to(args.install.resolve()):
        parser.error("Catalog must be under research/local and outside the installation")
    summary_path = output.with_suffix(".summary.json")
    if output.exists() or summary_path.exists():
        parser.error("Choose new output paths")
    try:
        report = json.loads(args.key_report.read_text(encoding="utf-8"))
        key = bytes.fromhex(report["key_hex"])
        summary = catalog(args.install.resolve(), key, output)
        with summary_path.open("x", encoding="utf-8") as stream:
            json.dump(summary, stream, indent=2)
            stream.write("\n")
    except (OSError, ValueError, KeyError, sqlite3.Error, struct.error) as error:
        parser.exit(1, f"Catalog failed: {error}\n")
    print(json.dumps({k: v for k, v in summary.items() if k not in ("pak_indexes", "containers", "key_sha256")}, indent=2))


if __name__ == "__main__":
    main()
