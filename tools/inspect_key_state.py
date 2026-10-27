"""Read bounded state reached from anchored key-lookup code; validate Pak keys.

No game function is invoked. No broad heap search or raw object dump is saved.
Only pointers from the anchored globals are followed, under strict read budgets.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct

try:
    from .archive_keys import AES256, load_targets
    from .inspect_live_build import inspect
    from .probe_archive_keys import CandidateCheck
except ImportError:
    from archive_keys import AES256, load_targets
    from inspect_live_build import inspect
    from probe_archive_keys import CandidateCheck


def sample_windows(data, label, check):
    for offset in range(0, len(data) - 31, 4):
        check.offer(data[offset:offset + 32], {"encoding": "targeted_binary_window",
                    "region": label, "offset": offset})


def collect(base, size, read, plan, check):
    anchors = plan.get("anchors", [])
    if not 1 <= len(anchors) <= 8:
        raise ValueError("Expected bounded code anchors")
    for anchor in anchors:
        value = bytes.fromhex(anchor["hex"])
        rva = anchor["rva"]
        if not 1 <= len(value) <= 512 or not 0 <= rva <= size - len(value):
            raise ValueError("Invalid key-state anchor")
        if read(base + rva, len(value)) != value:
            return {"status": "anchor_mismatch", "note": "No global/object reads performed"}
    result = {"status": "anchors_matched", "observations": [], "code_windows": []}
    for name in ("manager_rva", "delegate_rva"):
        if not 0 <= plan[name] <= size - 192:
            raise ValueError("Global outside main image")

    def observe(label, address, length):
        try:
            data = read(address, length)
            result["observations"].append({"label": label, "status": "read", "bytes": len(data)})
            return data
        except (OSError, ValueError) as error:
            result["observations"].append({"label": label, "status": "read_failed", "error": str(error)})
            return None

    manager = observe("manager_global_candidate", base + plan["manager_rva"], 192)
    if manager is not None:
        sample_windows(manager, "manager_global_candidate", check)
        # Code in the anchored registration candidate passes manager+0x28 to
        # map-like helpers. Pointer/count/max are a bounded layout hypothesis.
        pointer, count, capacity = struct.unpack_from("<Qii", manager, 0x28)
        dimensions = {"element_count_candidate": count, "capacity_candidate": capacity}
        result["map_dimensions"] = dimensions
        if 0x10000 <= pointer < 0x800000000000 and 0 < count <= capacity <= 128:
            amount = min(4096, count * 64)
            data = observe("manager_elements_candidate", pointer, amount)
            if data is not None:
                sample_windows(data, "manager_elements_candidate", check)
                dimensions["sampled_bytes"] = len(data)
        else:
            dimensions["sampled_bytes"] = 0
            dimensions["note"] = "Empty or outside bounded pointer/count hypothesis; no element-pointer read"
    if check.verified:
        return result
    delegate = observe("delegate_global_candidate", base + plan["delegate_rva"], 16)
    if delegate is None:
        return result
    pointer, flag = struct.unpack_from("<QI", delegate)
    result["delegate_nonzero_field_at_8"] = bool(flag)
    if not flag or not 0x10000 <= pointer < 0x800000000000:
        result["delegate_note"] = "No bounded allocated-instance candidate to follow"
        return result
    instance = observe("delegate_instance_candidate", pointer, 128)
    if instance is None:
        return result
    sample_windows(instance, "delegate_instance_candidate", check)
    if check.verified:
        return result
    vtable = int.from_bytes(instance[:8], "little")
    functions = set()
    if base <= vtable <= base + size - 0x60:
        table = observe("delegate_vtable_candidate", vtable, 0x60)
        if table is not None:
            target = int.from_bytes(table[0x58:0x60], "little")
            if base <= target <= base + size - 384:
                functions.add(target)
    # Potential static callback pointers inside the first 64 instance bytes.
    # Only values inside the verified main image qualify; no other pointer chase.
    for offset in range(8, 64, 8):
        target = int.from_bytes(instance[offset:offset + 8], "little")
        if base <= target <= base + size - 384:
            functions.add(target)
    for target in sorted(functions)[:8]:
        code = observe("callback_code_candidate", target, 384)
        if code is not None:
            check.observe(code, target - base)
            result["code_windows"].append({"rva": target - base, "hex": code.hex(),
                                           "note": "Pointer-shaped value; function identity and execution unverified"})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--install", required=True, type=Path)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root, output = args.install.resolve(), args.output.resolve()
    local = Path(__file__).resolve().parents[1] / "research/local"
    if not output.is_relative_to(local.resolve()) or output.is_relative_to(root) or output.exists():
        parser.error("Choose a new local report outside the installation")
    report = {"schema_version": 1, "pid": args.pid,
              "collected_utc": datetime.now(timezone.utc).isoformat(), "plan": str(args.plan),
              "limitations": ["Global layouts and object roles are hypotheses from anchored code.",
                              "Raw binary candidates require all selected Pak index hashes to match.",
                              "No broad heap scan, function execution, or process modification is performed."]}
    try:
        plan = json.loads(args.plan.read_text(encoding="utf-8"))
        executable = root / "Aion2/Binaries/Win64/AION2.exe"
        with executable.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if digest != plan["expected_executable_sha256"]:
            raise ValueError("Executable hash does not match plan")
        targets = load_targets(root)
        report["targets"] = [{k: v for k, v in t.items() if k != "ciphertext"} for t in targets]
        with AES256() as aes:
            check = CandidateCheck(targets, aes.decrypt)
            report["runtime_scan"] = inspect(args.pid, executable, finish_observer=lambda base, size, read:
                                              collect(base, size, read, plan, check))
            report["distinct_candidates_tested"] = len(check.seen)
            report["verified_keys"] = check.verified
        report["status"] = "scanned"
    except (OSError, ValueError, KeyError, TypeError) as error:
        report.update(status="probe_failed", error=str(error))
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2)
        stream.write("\n")
    targeted = report.get("runtime_scan", {}).get("targeted_observation", {})
    print(json.dumps({"status": report["status"], "pid": args.pid,
                      "targeted_status": targeted.get("status"),
                      "candidates_tested": report.get("distinct_candidates_tested", 0),
                      "verified_key_count": len(report.get("verified_keys", [])),
                      "error": report.get("error"), "report": str(output)}, indent=2))
    if report["status"] == "probe_failed":
        parser.exit(1)


if __name__ == "__main__":
    main()
