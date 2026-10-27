"""Hash saved files without retaining contents; compare snapshots after experiments.

This does not launch a client, change saved settings, or establish isolation.
"""
import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


def snapshot(root):
    root = Path(root)
    result = {"schema_version": 1, "root": str(root.resolve()),
              "collected_utc": datetime.now(timezone.utc).isoformat(),
              "present": root.exists(), "files": {}, "errors": []}
    if not root.exists():
        return result
    # Windows junctions and symlinks must not lead this audit outside its root.
    def linked(path):
        return path.is_symlink() or bool(getattr(path.lstat(), "st_file_attributes", 0) & 0x400)
    def walk(directory):
        try:
            if linked(directory):
                raise ValueError("reparse point excluded")
            for path in sorted(directory.iterdir()):
                relative = path.relative_to(root).as_posix()
                try:
                    if linked(path):
                        raise ValueError("reparse point excluded")
                    if path.is_dir():
                        walk(path)
                    elif path.is_file():
                        before = path.stat()
                        digest = hashlib.sha256()
                        with path.open("rb") as stream:
                            for block in iter(lambda: stream.read(1024 * 1024), b""):
                                digest.update(block)
                        after = path.stat()
                        if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
                            raise ValueError("file changed while hashing")
                        result["files"][relative] = {"size": after.st_size, "sha256": digest.hexdigest()}
                except (OSError, ValueError) as error:
                    result["errors"].append({"path": relative, "error": str(error)})
        except (OSError, ValueError) as error:
            result["errors"].append({"path": directory.relative_to(root).as_posix(), "error": str(error)})
    walk(root)
    return result


def compare(before, after):
    if before["root"] != after["root"]:
        raise ValueError("Snapshots must describe the same root")
    old, new = before["files"], after["files"]
    added, removed = sorted(new.keys() - old.keys()), sorted(old.keys() - new.keys())
    changed = sorted(key for key in old.keys() & new.keys() if old[key] != new[key])
    return {"added": added, "removed": removed, "changed": changed,
            "root_presence_changed": before["present"] != after["present"],
            "complete": not before["errors"] and not after["errors"],
            "limitation": "Equal snapshots cannot exclude transient writes, registry, services, or network activity."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--baseline", type=Path)
    args = parser.parse_args()
    if args.output.resolve().is_relative_to(args.root.resolve()):
        parser.error("Output must be outside the audited directory")
    report = snapshot(args.root)
    if args.baseline:
        report["comparison"] = compare(json.loads(args.baseline.read_text(encoding="utf-8")), report)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Audited {len(report['files'])} files; {len(report['errors'])} errors. Saved {args.output}")
    if "comparison" in report:
        print(json.dumps(report["comparison"], indent=2))
    return 1 if report["errors"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
