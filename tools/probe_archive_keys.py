"""Find textual/compiled AES candidates and accept only multi-Pak hash matches.

Static mode reads the installed main executable. Live mode reads its loaded
image using the existing query/read-only probe. No memory dump is saved.
"""

import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import mmap
from itertools import chain
from pathlib import Path

try:
    from .archive_keys import AES256, key_candidates, load_targets, validate
    from .inspect_live_build import inspect, MAX_IMAGE, READ_BYTES, OVERLAP
    from .key_constants import binary_key_candidates
except ImportError:
    from archive_keys import AES256, key_candidates, load_targets, validate
    from inspect_live_build import inspect, MAX_IMAGE, READ_BYTES, OVERLAP
    from key_constants import binary_key_candidates


class CandidateCheck:
    def __init__(self, targets, decrypt):
        self.targets, self.decrypt = targets, decrypt
        self.seen = set()
        self.verified = []
        self.formats = Counter()

    def observe(self, data, offset):
        for key, source in chain(key_candidates(data, offset), binary_key_candidates(data, offset)):
            self.offer(key, source)

    def offer(self, key, source):
        if len(key) != 32:
            raise ValueError("Candidate must be 32 bytes")
        if key in self.seen:
            return
        if len(self.seen) >= 10000:
            raise ValueError("Candidate count exceeds bounded search")
        self.seen.add(key)
        self.formats[source["encoding"]] += 1
        matches = validate(key, self.targets, self.decrypt)
        if matches:
            self.verified.append({"key_hex": key.hex(), "key_sha256": hashlib.sha256(key).hexdigest(),
                                  "source": source, "validated_indexes": matches})


def run(install, pid=None):
    executable = install / "Aion2/Binaries/Win64/AION2.exe"
    targets = load_targets(install)
    with AES256() as aes:
        check = CandidateCheck(targets, aes.decrypt)
        report = {"schema_version": 2, "collected_utc": datetime.now(timezone.utc).isoformat(),
                  "mode": "live" if pid is not None else "static", "pid": pid,
                  "executable": str(executable),
                  "targets": [{k: v for k, v in t.items() if k != "ciphertext"} for t in targets],
                  "search_scope": "ASCII/UTF-16LE hex plus consecutive x64 imm32 stores or imm64 register/store pairs constructing 32 bytes; main image only",
                  "limitations": ["Does not search arbitrary binary key bytes, vector loads, nonconsecutive instructions, generated keys, heaps or other modules.",
                                  "No match does not establish that a key is absent or unrecoverable.",
                                  "Validation tests AES-256-ECB and recorded Pak index SHA-1 values, not every archive."]}
        try:
            if pid is not None:
                report["runtime_scan"] = inspect(pid, executable, block_observer=check.observe)
                report["source_offset_kind"] = "main_image_rva"
            else:
                if not 0 < executable.stat().st_size <= MAX_IMAGE:
                    raise ValueError("Executable exceeds configured size bound")
                with executable.open("rb") as stream, mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
                    report["executable_sha256"] = hashlib.sha256(data).hexdigest()
                    for offset in range(0, len(data), READ_BYTES):
                        start = max(0, offset - OVERLAP)
                        check.observe(data[start:min(len(data), offset + READ_BYTES)], start)
                report["source_offset_kind"] = "file_offset"
            report["status"] = "scanned"
        except (OSError, ValueError) as error:
            report.update(status="probe_failed", error=str(error))
        report["distinct_candidates_tested"] = len(check.seen)
        report["candidate_format_counts"] = dict(check.formats)
        report["verified_keys"] = check.verified
        return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", required=True, type=Path)
    parser.add_argument("--pid", type=int, help="Omit for static installed-executable scan")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root, output = args.install.resolve(), args.output.resolve()
    local = Path(__file__).resolve().parents[1] / "research/local"
    if not output.is_relative_to(local.resolve()) or output.is_relative_to(root):
        parser.error("Key reports must remain under this project's research/local directory and outside the installation")
    if output.exists():
        parser.error("Output already exists; choose a new report filename")
    try:
        report = run(root, args.pid)
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("x", encoding="utf-8") as stream:
            json.dump(report, stream, indent=2)
            stream.write("\n")
    except (OSError, ValueError) as error:
        parser.exit(1, f"Archive-key probe failed: {error}\n")
    print(json.dumps({"status": report["status"], "mode": report["mode"],
                      "pid": args.pid, "distinct_candidates_tested": report["distinct_candidates_tested"],
                      "candidate_format_counts": report["candidate_format_counts"],
                      "verified_key_count": len(report["verified_keys"]), "error": report.get("error"),
                      "report": str(output)}, indent=2))
    if report["status"] == "probe_failed":
        parser.exit(1)


if __name__ == "__main__":
    main()
