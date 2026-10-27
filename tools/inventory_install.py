"""Inventory an AION2 installation without modifying or executing game files."""

import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def inventory(root):
    files = []
    manifests = []
    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        stat = path.stat()
        relative = path.relative_to(root).as_posix()
        entry = {
            "path": relative,
            "size_bytes": stat.st_size,
            "modified_utc": datetime.fromtimestamp(
                stat.st_mtime, timezone.utc
            ).isoformat(),
        }
        # Hash small build manifests and game executables, not bulk assets.
        if path.name.startswith("Manifest_") or (
            relative.startswith("Aion2/Binaries/") and path.suffix.lower() == ".exe"
        ):
            entry["sha256"] = sha256(path)
        files.append(entry)
        if path.name.startswith("Manifest_") and path.suffix.lower() == ".txt":
            lines = path.read_text(encoding="utf-8-sig").splitlines()
            listed = [line.split("\t", 1)[0] for line in lines if line.strip()]
            manifests.append({
                "path": relative,
                "entry_count": len(listed),
                "listed_paths": listed,
            })

    present = {entry["path"].casefold() for entry in files}
    for manifest in manifests:
        # UFS entries may be packed; absence as loose files is expected.
        listed = manifest.pop("listed_paths")
        manifest["listed_paths_present_as_loose_files"] = sum(
            item.casefold() in present for item in listed
        )
        if "NonUFS" in manifest["path"] or "DebugFiles" in manifest["path"]:
            manifest["listed_paths_absent_as_loose_files"] = [
                item for item in listed if item.casefold() not in present
            ]

    return {
        "schema_version": 1,
        "collected_utc": datetime.now(timezone.utc).isoformat(),
        "installation": str(root),
        "file_count": len(files),
        "total_size_bytes": sum(entry["size_bytes"] for entry in files),
        "extension_counts": dict(sorted(Counter(
            Path(entry["path"]).suffix.lower() or "<none>" for entry in files
        ).items())),
        "manifests": manifests,
        "files": files,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", type=Path, default=Path(
        r"C:\Program Files (x86)\Steam\steamapps\common\AION2"
    ))
    parser.add_argument("--output", type=Path, default=Path("research/local/inventory.json"))
    args = parser.parse_args()
    root = args.install.resolve()
    output = args.output.resolve()
    if not root.is_dir():
        parser.error(f"Installation directory does not exist: {root}")
    if output.is_relative_to(root):
        parser.error("Output must be outside the installation directory")
    report = inventory(root)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Inventoried {report['file_count']} files into {output}")
    print(json.dumps({key: value for key, value in report.items() if key != "files"}, indent=2))


if __name__ == "__main__":
    main()
