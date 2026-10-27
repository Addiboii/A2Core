"""Read the unencrypted global IoStore chunk and its length-delimited name batch."""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct

DEFAULT_INSTALL = Path(r"C:\Program Files (x86)\Steam\steamapps\common\AION2")
MAX_BYTES = 64 * 1024 * 1024


def global_chunk(toc, data):
    if len(toc) < 144 or toc[:16] != b"-==--==--==--==-" or toc[16] != 5:
        raise ValueError("Expected an IoStore v5 header")
    header, entries, blocks, block_width, methods = struct.unpack_from("<5I", toc, 20)
    if header != 144 or entries != 1 or block_width != 12 or methods != 0:
        raise ValueError("Only the observed single-entry uncompressed global layout is supported")
    if toc[80] & 3:
        raise ValueError("Encrypted/compressed global containers are unsupported")
    if struct.unpack_from("<I", toc, 52)[0] != 1:
        raise ValueError("Partitioned global containers are unsupported")
    seeds = struct.unpack_from("<I", toc, 84)[0]
    overflow = struct.unpack_from("<I", toc, 96)[0]
    offsets = header + entries * 12
    if offsets + 10 > len(toc):
        raise ValueError("Truncated chunk offset/length table")
    chunk_offset = int.from_bytes(toc[offsets:offsets + 5], "big")
    chunk_size = int.from_bytes(toc[offsets + 5:offsets + 10], "big")
    table = header + entries * 22 + (seeds + overflow) * 4
    if table + blocks * block_width > len(toc) or chunk_size > MAX_BYTES:
        raise ValueError("Invalid global chunk bounds")
    decoded = bytearray()
    for index in range(blocks):
        block = toc[table + index * block_width:table + (index + 1) * block_width]
        physical = int.from_bytes(block[:5], "little")
        compressed_size = int.from_bytes(block[5:8], "little")
        plain_size = int.from_bytes(block[8:11], "little")
        if block[11] != 0 or compressed_size != plain_size:
            raise ValueError("Unexpected compressed global block")
        if physical + plain_size > len(data) or len(decoded) + plain_size > MAX_BYTES:
            raise ValueError("Global block exceeds data bounds")
        decoded.extend(data[physical:physical + plain_size])
    if chunk_offset + chunk_size > len(decoded):
        raise ValueError("Global logical chunk exceeds decoded blocks")
    return bytes(decoded[chunk_offset:chunk_offset + chunk_size])


def name_batch(data):
    if len(data) < 16:
        raise ValueError("Truncated name batch")
    count, string_bytes, hash_version = struct.unpack_from("<IIQ", data)
    if count > 1000000 or string_bytes > MAX_BYTES:
        raise ValueError("Name batch exceeds bounds")
    headers = 16 + count * 8
    cursor = headers + count * 2
    end = cursor + string_bytes
    if end > len(data):
        raise ValueError("Name batch tables exceed data")
    names = []
    for index in range(count):
        header = int.from_bytes(data[headers + index * 2:headers + index * 2 + 2], "big")
        wide = bool(header & 0x8000)
        length = (header & 0x7FFF) * (2 if wide else 1)
        if cursor + length > end:
            raise ValueError("Name length exceeds declared string data")
        names.append(data[cursor:cursor + length].decode("utf-16le" if wide else "utf-8"))
        cursor += length
    if cursor != end:
        raise ValueError("Name lengths do not consume declared string data")
    return {"name_count": count, "string_bytes": string_bytes, "hash_version_raw": hash_version,
            "bytes_consumed": cursor, "names": names}


def script_objects(data, offset, names):
    """Observed count + 32-byte script-object records, not reflected field schemas."""
    if offset < 0 or offset + 4 > len(data):
        raise ValueError("Truncated script-object count")
    count = struct.unpack_from("<I", data, offset)[0]
    if count > 1000000 or offset + 4 + count * 32 != len(data):
        raise ValueError("Script-object table does not consume the remaining chunk")
    records = []
    for index in range(count):
        name, number, reference, outer, cdo = struct.unpack_from("<IIQQQ", data, offset + 4 + index * 32)
        name_index = name & 0x3FFFFFFF
        if name >> 30 != 2 or name_index >= len(names) or reference >> 62 != 1:
            raise ValueError("Script-object reference differs from the observed global layout")
        records.append({"name": names[name_index], "number": number,
                        "reference": reference, "outer": outer, "cdo_class": cdo})
    by_reference = {record["reference"]: record for record in records}
    if len(by_reference) != count:
        raise ValueError("Duplicate script-object reference")
    null = 0xFFFFFFFFFFFFFFFF
    if any(record["outer"] != null and record["outer"] not in by_reference for record in records):
        raise ValueError("Unresolved script-object outer reference")
    children = Counter(record["outer"] for record in records)
    packets = [record for record in records if record["name"].startswith("DevPacketData_")]
    parents = Counter(by_reference[record["outer"]]["name"] if record["outer"] != null else "<root>" for record in packets)
    return {"object_count": count, "record_bytes": 32, "bytes_consumed": 4 + count * 32,
            "global_name_map_type": 2, "script_reference_type": 1,
            "packet_object_count": len(packets), "packet_parent_counts": dict(sorted(parents.items())),
            "packet_objects_with_children": [{"name": record["name"], "child_count": children[record["reference"]]}
                                             for record in packets if children[record["reference"]]],
            "note": "Object references are encoded script imports, not network message tags. Object names/outer relationships do not provide packet field schemas."}


def inspect(install):
    root = install / "Aion2/Content/Paks"
    paths = [root / "global.utoc", root / "global.ucas"]
    if any(path.stat().st_size > MAX_BYTES for path in paths):
        raise ValueError("Global source exceeds configured size bound")
    toc, data = [path.read_bytes() for path in paths]
    chunk = global_chunk(toc, data)
    result = name_batch(chunk)
    packets = [name for name in result["names"] if name.startswith("DevPacketData_")]
    counts = Counter(name.split("_", 2)[1] for name in packets)
    return {"schema_version": 1,
            "source_sha256": {path.name: hashlib.sha256(content).hexdigest() for path, content in zip(paths, (toc, data))},
            "logical_chunk_bytes": len(chunk), **result,
            "script_objects": script_objects(chunk, result["bytes_consumed"], result["names"]),
            "packet_type_count": len(packets), "packet_families": dict(sorted(counts.items())),
            "login_packet_types": [name for name in packets if name.startswith("DevPacketData_LoginServer_")],
            "limitations": ["Name hashes and container signatures were not verified.",
                "Name batch and script-object relationships are decoded; packet field schemas are not present in this table.",
                "Reflected type names do not establish their numeric wire tags or runtime use."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", type=Path, default=DEFAULT_INSTALL)
    parser.add_argument("--output", type=Path, default=Path("research/local/global-name-report.json"))
    args = parser.parse_args()
    if args.output.resolve().is_relative_to(args.install.resolve()):
        parser.error("Output must be outside the installation")
    result = inspect(args.install)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in result.items() if key != "names"}, indent=2))


if __name__ == "__main__":
    main()
