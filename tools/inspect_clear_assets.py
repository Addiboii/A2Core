"""Audit clear single-entry IoStore payloads without decrypting game content."""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct

from inspect_formats import utoc_metadata
from inspect_global_names import MAX_BYTES, global_chunk


def classify(chunk, container_id):
    # Exact observed pattern; do not label arbitrary short payloads as empty.
    empty = (len(chunk) == 44 and chunk[:4] == b"nCoI"
             and struct.unpack_from("<I", chunk, 4)[0] == 3
             and chunk[8:16].hex() == container_id and chunk[16:] == bytes(28))
    return "empty_container_header_candidate" if empty else "unclassified"


def inspect(install):
    entries = []
    for path in sorted(install.rglob("*.utoc")):
        record = {"path": path.relative_to(install).as_posix()}
        try:
            meta = utoc_metadata(path)
            if not meta.get("parsed"):
                raise ValueError("Unsupported TOC layout")
            if meta["encrypted"]:
                record["status"] = "encrypted_flag_set"
            elif path.name == "global.utoc":
                record["status"] = "global_metadata_separate_reader"
            else:
                sources = [path, path.with_suffix(".ucas")]
                if any(p.stat().st_size > MAX_BYTES for p in sources):
                    raise ValueError("Source exceeds bounded clear-container reader")
                toc, data = [p.read_bytes() for p in sources]
                chunk = global_chunk(toc, data)
                record.update(status=classify(chunk, meta["container_id"]),
                              logical_bytes=len(chunk), chunk_id_hex=toc[144:156].hex(),
                              source_sha256={p.name: hashlib.sha256(b).hexdigest()
                                             for p, b in zip(sources, (toc, data))},
                              logical_sha256=hashlib.sha256(chunk).hexdigest(),
                              prefix_hex=chunk[:64].hex())
        except (OSError, ValueError, struct.error, IndexError) as error:
            record.update(status="error", error=str(error))
        entries.append(record)
    return {"schema_version": 1, "installation": str(install),
            "counts": dict(Counter(r["status"] for r in entries)), "containers": entries,
            "limitations": ["Empty-header classification is an exact observed byte-pattern candidate.",
                            "Encrypted payloads and Pak indexes are not decoded.",
                            "Signatures are not verified; no rendering/import support is established."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root, output = args.install.resolve(), args.output.resolve()
    if not root.is_dir() or output.is_relative_to(root):
        parser.error("Provide an existing installation and an output outside it")
    report = inspect(root)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report["counts"], indent=2))
    if not report["containers"] or report["counts"].get("error"):
        parser.exit(1, "No containers found or some containers could not be inspected.\n")


if __name__ == "__main__":
    main()
