"""Collect bounded encryption-related string references from a loaded AION2 image.

Retains the readable main image temporarily in memory, but writes only selected
needle locations and small code windows around candidate RIP-relative LEAs.
Does not collect heaps, inject code, change memory, or save a full memory dump.
"""

import argparse
import hashlib
from bisect import bisect_right
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import struct

try:
    from .inspect_live_build import inspect, MAX_IMAGE
except ImportError:
    from inspect_live_build import inspect, MAX_IMAGE

NEEDLES = ("RegisterEncryptionKey", "GetPakEncryptionKey", "EncryptionKeyGuid",
           "EncryptionKey", "encryption key", "Encryption key", "AES key",
           "Invalid AES", "Failed to decrypt", "DecryptData", "FAES",
           "FPakPlatformFile", "FIoStoreReader")
STARTUP_NEEDLES = ("ConfigurationServerUrl", "DefaultLoginServer", "DefaultPurpleLoginServer",
                   "NetworkConfig", "NcPlatformNetworkType", "mobileappconfig",
                   "LoginServerAddress", "LoginServerIP", "LoginServerPort",
                   "NcPlatformNetworks", "GamePlatformType", "AionGamePlatformSettings")
LEA = re.compile(rb"[\x48-\x4f]\x8d[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]")
# RIP-relative loads/address-of with disp32: LEA r64 and MOV r64,[rip+disp32].
RIP_REF = re.compile(rb"[\x48-\x4f][\x8d\x8b][\x05\x0d\x15\x1d\x25\x2d\x35\x3d]")
MAX_STRINGS = 512
MAX_REFS = 128
WINDOW = 128


class ImageCapture:
    def __init__(self):
        self.data = bytearray()
        self.ranges = []

    def observe(self, data, offset):
        end = offset + len(data)
        if offset < 0 or end > MAX_IMAGE:
            raise ValueError("Image observer exceeds bounds")
        if end > len(self.data):
            self.data.extend(bytes(end - len(self.data)))
        self.data[offset:end] = data
        if self.ranges and offset <= self.ranges[-1][1]:
            self.ranges[-1] = (self.ranges[-1][0], max(end, self.ranges[-1][1]))
        else:
            self.ranges.append((offset, end))

    def readable(self, start, end):
        if start < 0 or end < start:
            return False
        i = bisect_right(self.ranges, (start, MAX_IMAGE + 1)) - 1
        return i >= 0 and self.ranges[i][0] <= start <= end <= self.ranges[i][1]


def string_start(image, offset, width):
    # Only accept a nearby terminator on the matching code-unit alignment.
    cursor = offset
    while cursor >= width and offset - cursor <= 512:
        if not image.readable(cursor - width, cursor):
            return None
        if image.data[cursor - width:cursor] == bytes(width):
            return cursor
        cursor -= width
    return None


def analyze(image, needles=NEEDLES, pointer_width=8, image_base=0):
    strings = []
    string_limit = False
    for needle in needles:
        for encoding, width in (("ascii", 1), ("utf-16le", 2)):
            encoded = needle.encode(encoding)
            cursor = 0
            while True:
                at = image.data.find(encoded, cursor)
                if at < 0:
                    break
                cursor = at + len(encoded)
                if not image.readable(at, cursor):
                    continue
                if len(strings) >= MAX_STRINGS:
                    string_limit = True
                    break
                strings.append({"needle": needle, "encoding": encoding, "needle_rva": at,
                                "string_start_rva_candidate": string_start(image, at, width)})
            if string_limit:
                break
        if string_limit:
            break
    targets = {}
    for index, item in enumerate(strings):
        for address in {item["needle_rva"], item["string_start_rva_candidate"]} - {None}:
            targets.setdefault(address, []).append(index)
    pointer_table = {}
    for address in list(targets):
        for candidate in enumerate_absolute_pointers(image, address, pointer_width, image_base):
            pointer_table.setdefault(candidate, []).append({"target_rva": address, "string_indexes": targets[address]})
    # Single pass over all RIP-relative references; record only those whose
    # target is a known string address or a pointer-table entry holding one.
    relevant = set(targets) | set(pointer_table)
    refs = []
    ptr_refs = []
    ref_limit = False
    ptr_ref_limit = False
    for match in RIP_REF.finditer(image.data):
        rva = match.start()
        if not image.readable(rva, rva + 7):
            continue
        target = rva + 7 + int.from_bytes(image.data[rva + 3:rva + 7], "little", signed=True)
        if target not in relevant:
            continue
        begin, end = max(0, rva - WINDOW), min(len(image.data), rva + 7 + WINDOW)
        if not image.readable(begin, end):
            begin, end = rva, rva + 7
        if target in targets:
            if len(refs) >= MAX_REFS:
                ref_limit = True
                continue
            refs.append({"instruction_rva_candidate": rva, "target_rva": target,
                         "string_indexes": targets[target], "context_rva": begin,
                         "context_hex": image.data[begin:end].hex()})
        else:
            if len(ptr_refs) >= MAX_REFS:
                ptr_ref_limit = True
                continue
            entries = pointer_table[target]
            ptr_refs.append({"instruction_rva_candidate": rva, "pointer_entry_rva": target,
                             "indirect_targets": sorted({entry["target_rva"] for entry in entries}),
                             "string_indexes": sorted({index for entry in entries for index in entry["string_indexes"]}),
                             "context_rva": begin, "context_hex": image.data[begin:end].hex()})
    return {"string_matches": strings, "reference_candidates": refs,
            "pointer_table_entries": {hex(key): value for key, value in pointer_table.items()},
            "indirect_reference_candidates": ptr_refs,
            "string_limit_reached": string_limit, "reference_limit_reached": ref_limit,
            "indirect_reference_limit_reached": ptr_ref_limit,
            "exported_code_bytes": sum(len(r["context_hex"]) // 2 for r in refs + ptr_refs)}


def enumerate_absolute_pointers(image, address, width=8, image_base=0):
    """Find readable cells whose stored little-endian absolute address equals `address`.

    Scans the readable image for cells holding the 64-bit image-base-relative RVA.
    Each match is a candidate pointer-table entry, not a proven reference: a
    coincidental match of the same 64-bit value elsewhere is possible.
    """
    if not image.readable(address, address + 1):
        return
    if width not in (4, 8):
        raise ValueError("Pointer width must be 4 or 8")
    value = image_base + address
    needle = (value & 0xFFFFFFFF if width == 4 else value).to_bytes(width, "little")
    cursor = 0
    count = 0
    while count < 512:
        at = image.data.find(needle, cursor)
        if at < 0:
            break
        cursor = at + 1
        if at % width != 0 or not image.readable(at, at + width):
            continue
        count += 1
        yield at


def call_references(image, targets):
    """Find bounded CALL/LEA candidates; byte matches do not prove instructions."""
    if not 1 <= len(targets) <= 8 or any(not isinstance(t, int) or not 0 <= t < MAX_IMAGE for t in targets):
        raise ValueError("Invalid bounded call target list")
    wanted = set(targets)
    refs = []
    pattern = re.compile(rb"\xe8|[\x48-\x4f]\x8d[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]")
    for match in pattern.finditer(image.data):
        at = match.start()
        size = 5 if match.group() == b"\xe8" else 7
        if not image.readable(at, at + size):
            continue
        target = at + size + int.from_bytes(image.data[at+size-4:at+size], "little", signed=True)
        if target not in wanted:
            continue
        if len(refs) >= 32:
            return {"candidates": refs, "limit_reached": True}
        start, end = max(0, at - 128), min(len(image.data), at + 261)
        if not image.readable(start, end):
            start, end = at, at + size
        refs.append({"instruction_rva_candidate": at, "reference_kind": "CALL" if size == 5 else "LEA", "target_rva": target,
                     "context_rva": start, "context_hex": image.data[start:end].hex()})
    return {"candidates": refs, "limit_reached": False}


def followup_snapshot(image, plan):
    """Read a finite reviewed list of main-image spans only after exact anchors match."""
    anchors, targets = plan.get("anchors", []), plan.get("targets", [])
    if not 1 <= len(anchors) <= 16 or not 1 <= len(targets) <= 32:
        raise ValueError("Follow-up plan needs bounded anchors and targets")
    expected = []
    for anchor in anchors:
        value = bytes.fromhex(anchor["hex"])
        start = anchor["rva"]
        if not 0 <= start < MAX_IMAGE or not 1 <= len(value) <= 2048:
            raise ValueError("Invalid follow-up anchor")
        expected.append((start, value))
    if any(not image.readable(start, start + len(value)) or
           image.data[start:start + len(value)] != value for start, value in expected):
        return {"status": "anchor_mismatch", "windows": [],
                "note": "This image does not match the previously observed code anchors; no follow-up windows exported."}
    budget = 0
    windows = []
    for target in targets:
        start, size = target["rva"], target["size"]
        budget += size
        if not 0 <= start < MAX_IMAGE or not 1 <= size <= 4096 or start + size > MAX_IMAGE or budget > 65536:
            raise ValueError("Follow-up target exceeds bounds")
        entry = {"label": target["label"], "rva": start, "requested_bytes": size}
        if not image.readable(start, start + size):
            entry["status"] = "not_fully_readable"
        else:
            data = bytes(image.data[start:start + size])
            entry.update(status="read", hex=data.hex())
            if target.get("encoding") in ("ascii", "utf-16le"):
                encoding = target["encoding"]
                width = 2 if encoding == "utf-16le" else 1
                end = next((i for i in range(0, len(data) - width + 1, width)
                            if data[i:i + width] == bytes(width)), None)
                if end is not None:
                    entry["diagnostic_text"] = data[:end].decode(encoding, errors="replace")
                    # Export only the terminated literal, not adjacent strings/data.
                    entry["hex"] = data[:end + width].hex()
                else:
                    entry.pop("hex")
                    entry["status"] = "no_terminator_within_bound"
        windows.append(entry)
    result = {"status": "anchors_matched", "windows": windows,
              "note": "Labels describe hypotheses from prior snippets; no function symbols or behavior are proven."}
    if "call_targets" in plan:
        result["direct_calls"] = call_references(image, plan["call_targets"])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--install", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--followup-plan", type=Path, help="Bounded, anchored main-image follow-up spans")
    parser.add_argument("--profile", choices=("encryption", "startup"), default="encryption")
    args = parser.parse_args()
    root, output = args.install.resolve(), args.output.resolve()
    local = Path(__file__).resolve().parents[1] / "research/local"
    if not output.is_relative_to(local.resolve()) or output.is_relative_to(root) or output.exists():
        parser.error("Choose a new output under research/local and outside the installation")
    image = ImageCapture()
    report = {"schema_version": 1, "collected_utc": datetime.now(timezone.utc).isoformat(),
              "pid": args.pid, "profile": args.profile, "limitations": [
                  "String and LEA byte matches are candidates, not symbols, function boundaries or proof of execution.",
                  "Only listed needles and direct RIP-relative LEA references in the readable main image are searched.",
                  "No decryption key is recovered by this collector; indirect references and other modules are outside scope."]}
    try:
        plan = None
        if args.followup_plan:
            plan = json.loads(args.followup_plan.read_text(encoding="utf-8"))
            with (root / "Aion2/Binaries/Win64/AION2.exe").open("rb") as source:
                digest = hashlib.file_digest(source, "sha256").hexdigest()
            if digest != plan.get("expected_executable_sha256"):
                raise ValueError("Executable hash does not match the follow-up plan")
        report["runtime_scan"] = inspect(args.pid, root / "Aion2/Binaries/Win64/AION2.exe", image.observe)
        report.update(analyze(image, STARTUP_NEEDLES if args.profile == "startup" else NEEDLES,
                              image_base=report["runtime_scan"].get("image_base", 0)), status="scanned")
        if plan is not None:
            report["followup"] = followup_snapshot(image, plan)
            report["followup_plan"] = str(args.followup_plan)
    except (OSError, ValueError) as error:
        report.update(status="probe_failed", error=str(error))
    finally:
        image.data.clear()
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2)
        stream.write("\n")
    print(json.dumps({"status": report["status"], "pid": args.pid,
                      "string_matches": len(report.get("string_matches", [])),
                      "reference_candidates": len(report.get("reference_candidates", [])),
                      "exported_code_bytes": report.get("exported_code_bytes", 0),
                      "followup_status": report.get("followup", {}).get("status"),
                      "followup_windows": len(report.get("followup", {}).get("windows", [])),
                      "error": report.get("error"), "report": str(output)}, indent=2))
    if report["status"] == "probe_failed":
        parser.exit(1)


if __name__ == "__main__":
    main()
