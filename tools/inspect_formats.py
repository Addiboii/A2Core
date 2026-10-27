"""Read PE, IoStore and Pak metadata; never launch or modify game files."""

import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import mmap
from pathlib import Path
import re
import struct

DEFAULT_INSTALL = Path(r"C:\Program Files (x86)\Steam\steamapps\common\AION2")


def unpack(data, offset, fmt):
    return struct.unpack_from("<" + fmt, data, offset)


def cstring(data, offset, limit=4096):
    end = data.find(b"\0", offset, min(len(data), offset + limit))
    if end < 0:
        raise ValueError(f"Unterminated string at {offset:#x}")
    return data[offset:end].decode("ascii", errors="replace")


def pe_metadata(path):
    with path.open("rb") as stream, mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
        if data[:2] != b"MZ":
            raise ValueError("Missing DOS signature")
        pe, = unpack(data, 0x3C, "I")
        if data[pe:pe + 4] != b"PE\0\0":
            raise ValueError("Missing PE signature")
        machine, count, timestamp, _, _, optional_size, characteristics = unpack(data, pe + 4, "HHIIIHH")
        optional = pe + 24
        magic, = unpack(data, optional, "H")
        if magic not in (0x10B, 0x20B):
            raise ValueError(f"Unsupported optional header: {magic:#x}")
        is64 = magic == 0x20B
        image_base, = unpack(data, optional + (24 if is64 else 28), "Q" if is64 else "I")
        entry, = unpack(data, optional + 16, "I")
        sections = []
        for i in range(count):
            offset = optional + optional_size + i * 40
            name = data[offset:offset + 8].rstrip(b"\0").decode("ascii", errors="replace")
            virtual_size, rva, raw_size, raw_offset = unpack(data, offset + 8, "IIII")
            sections.append(dict(name=name, virtual_size=virtual_size, rva=rva,
                                 raw_size=raw_size, raw_offset=raw_offset))

        def raw(rva):
            header_size, = unpack(data, optional + 60, "I")
            if rva < header_size and rva < len(data):
                return rva
            for section in sections:
                delta = rva - section["rva"]
                if 0 <= delta < section["raw_size"]:
                    result = section["raw_offset"] + delta
                    if result < len(data):
                        return result
            raise ValueError(f"RVA has no file-backed data: {rva:#x}")

        directory = optional + (112 if is64 else 96)
        directory_count, = unpack(data, directory - 4, "I")

        def get_directory(index):
            if index >= directory_count or directory + (index + 1) * 8 > optional + optional_size:
                return 0, 0
            return unpack(data, directory + index * 8, "II")

        def thunks(rva):
            if not rva:
                return []
            offset = raw(rva)
            width = 8 if is64 else 4
            high_bit = 1 << (width * 8 - 1)
            names = []
            for i in range(65536):
                value, = unpack(data, offset + i * width, "Q" if is64 else "I")
                if not value:
                    return names
                names.append(f"ordinal:{value & 0xFFFF}" if value & high_bit else cstring(data, raw(value) + 2))
            raise ValueError("Import thunk limit exceeded")

        imports = []
        import_rva, import_size = get_directory(1)
        if import_rva:
            for i in range(min(import_size // 20, 4096)):
                fields = unpack(data, raw(import_rva + i * 20), "IIIII")
                if not any(fields):
                    break
                original, _, _, name, first = fields
                imports.append({"dll": cstring(data, raw(name)), "symbols": thunks(original or first)})
        delay_imports = []
        delay_rva, delay_size = get_directory(13)
        if delay_rva:
            for i in range(min(delay_size // 32, 4096)):
                fields = unpack(data, raw(delay_rva + i * 32), "IIIIIIII")
                if not any(fields):
                    break
                attributes, name, _, _, table, _, _, _ = fields
                if not attributes & 1:
                    name -= image_base
                    table -= image_base
                delay_imports.append({"dll": cstring(data, raw(name)), "symbols": thunks(table)})

        codeview = []
        debug_rva, debug_size = get_directory(6)
        if debug_rva:
            for i in range(min(debug_size // 28, 4096)):
                fields = unpack(data, raw(debug_rva + i * 28), "IIHHIIII")
                _, _, _, _, kind, size, _, pointer = fields
                if kind == 2 and size >= 24 and data[pointer:pointer + 4] == b"RSDS":
                    codeview.append({"guid_bytes": data[pointer + 4:pointer + 20].hex(),
                                     "age": unpack(data, pointer + 20, "I")[0],
                                     "pdb_path": cstring(data, pointer + 24, size - 24)})

        # Focused build markers; avoid a bulk dump of arbitrary embedded strings.
        markers = []
        patterns = [rb"\+\+UE[45][ -~]{0,160}", rb"Unreal Engine [0-9][ -~]{0,80}"]
        for pattern in patterns:
            for match in re.finditer(pattern, data):
                markers.append({"offset": match.start(), "encoding": "ascii", "text": match.group().decode("ascii")})
        wide_pattern = rb"(?:\+\x00){2}U\x00E\x00[45]\x00(?:[ -~]\x00){0,160}"
        for match in re.finditer(wide_pattern, data):
            markers.append({"offset": match.start(), "encoding": "utf-16le", "text": match.group().decode("utf-16le")})
        certificate_offset, certificate_size = get_directory(4)
        return dict(sha256=hashlib.sha256(data).hexdigest(), machine=hex(machine),
                    pe_format="PE32+" if is64 else "PE32", coff_timestamp_raw=timestamp,
                    characteristics=hex(characteristics), image_base=hex(image_base),
                    entry_point_rva=hex(entry), sections=sections, imports=imports,
                    delay_imports=delay_imports, codeview=codeview, build_markers=markers,
                    certificate_table=dict(file_offset=certificate_offset, size=certificate_size))


def utoc_metadata(path):
    with path.open("rb") as stream:
        data = stream.read(144)
    if data[:16] != b"-==--==--==--==-":
        raise ValueError("Unrecognized IoStore magic")
    version = data[16]
    if version != 5:
        return {"version": version, "parsed": False, "reason": "Only version 5 layout supported"}
    values = unpack(data, 20, "IIIIIIIII")
    header_size, entries, blocks, block_entry_size, methods, name_length, block_size, directory_size, partitions = values
    if header_size != 144 or block_entry_size != 12 or methods > 256 or not 1 <= name_length <= 1024:
        raise ValueError("Unexpected IoStore header dimensions")
    flags = data[80]
    seed_count, = unpack(data, 84, "I")
    overflow_count, = unpack(data, 96, "I")
    # V5 adds perfect-hash seeds and overflow indices before compression blocks.
    method_offset = header_size + entries * 22 + (seed_count + overflow_count) * 4 + blocks * block_entry_size
    with path.open("rb") as stream:
        stream.seek(method_offset)
        names = stream.read(methods * name_length)
    if len(names) != methods * name_length:
        raise ValueError("Truncated compression method table")
    method_names = []
    for i in range(methods):
        name = names[i * name_length:(i + 1) * name_length].rstrip(b"\0")
        if not re.fullmatch(rb"[A-Za-z0-9_]+", name):
            raise ValueError("Invalid compression method name; verify table layout")
        method_names.append(name.decode("ascii"))
    return dict(version=version, parsed=True, header_size=header_size, entry_count=entries,
                compression_block_count=blocks, compression_block_entry_size=block_entry_size,
                compression_block_size=block_size, directory_index_size=directory_size,
                partition_count=partitions, container_id=data[56:64].hex(),
                encryption_key_guid_bytes=data[64:80].hex(), flags=flags,
                compressed=bool(flags & 1), encrypted=bool(flags & 2),
                signed=bool(flags & 4), indexed=bool(flags & 8),
                perfect_hash_seed_count=seed_count, perfect_hash_overflow_count=overflow_count,
                compression_methods=method_names)


def pak_metadata(path):
    size = path.stat().st_size
    with path.open("rb") as stream:
        stream.seek(max(0, size - 512))
        tail = stream.read()
    magic = struct.pack("<I", 0x5A6F12E1)
    offset = tail.rfind(magic)
    if offset < 0 or offset + 44 > len(tail):
        return {"parsed": False, "reason": "No standard Pak footer found in final 512 bytes"}
    version, index_offset, index_size = unpack(tail, offset + 4, "Iqq")
    if version != 11:
        return {"parsed": False, "version": version, "reason": "Only version 11 layout supported"}
    footer_start = max(0, size - 512) + offset - 17
    if (offset < 17 or offset + 204 != len(tail) or tail[offset - 1] not in (0, 1)
            or index_offset < 0 or index_size < 0 or index_offset + index_size > footer_start):
        raise ValueError("Invalid Pak index bounds")
    return dict(parsed=True, version=version, index_offset=index_offset, index_size=index_size,
                index_encrypted=bool(tail[offset - 1]),
                encryption_key_guid_bytes=tail[offset - 17:offset - 1].hex(),
                index_sha1=tail[offset + 24:offset + 44].hex(),
                signature_sidecar_present=path.with_suffix(".sig").is_file())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", type=Path, default=DEFAULT_INSTALL)
    parser.add_argument("--output", type=Path, default=Path("research/local/formats.json"))
    args = parser.parse_args()
    root, output = args.install.resolve(), args.output.resolve()
    if not root.is_dir() or output.is_relative_to(root):
        parser.error("Provide an existing installation and an output outside it")
    report = {"schema_version": 1, "collected_utc": datetime.now(timezone.utc).isoformat(),
              "installation": str(root), "executables": [], "utoc": [], "pak": []}
    targets = [("executables", root / "Aion2/Binaries/Win64/AION2.exe", pe_metadata)]
    targets += [("utoc", path, utoc_metadata) for path in sorted(root.rglob("*.utoc"))]
    # CEF uses an unrelated format with the same extension.
    targets += [("pak", path, pak_metadata) for path in sorted(root.rglob("*.pak")) if "Paks" in path.parts]
    for group, path, inspect in targets:
        entry = {"path": path.relative_to(root).as_posix()}
        try:
            entry.update(inspect(path))
        except (OSError, ValueError, struct.error, IndexError) as error:
            entry["error"] = str(error)
        report[group].append(entry)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Saved {output}")
    for group in ("executables", "utoc", "pak"):
        print(group, len(report[group]), "errors", sum("error" in item for item in report[group]))
    print("IoStore versions:", dict(Counter(item.get("version") for item in report["utoc"])))
    print("Pak versions:", dict(Counter(item.get("version") for item in report["pak"])))


if __name__ == "__main__":
    main()
