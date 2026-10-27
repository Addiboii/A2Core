"""Package project evidence and a SHA-256 manifest, or verify an extracted snapshot.

Uses only the standard library. Includes ignored research/local evidence;
excludes disposable caches, test-temp directories, and transfer outputs.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import stat
import zipfile

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = "TRANSFER_MANIFEST.json"
CHUNK = 1024 * 1024


def digest(stream):
    value = hashlib.sha256()
    for block in iter(lambda: stream.read(CHUNK), b""):
        value.update(block)
    return value.hexdigest()


def excluded(relative):
    return (relative.parts[0] == "_transfer" or "__pycache__" in relative.parts
            or ".venv" in relative.parts or relative.suffix == ".pyc"
            or relative.parts[:3] == ("research", "local", "test-tmp")
            or relative.as_posix() == MANIFEST)


def reject_link(path):
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 1024:
        raise ValueError(f"Linked/reparse paths require separate review: {path}")


def project_files(root):
    files, omissions = [], []

    def failed(error):
        raise error

    for base, directories, names in os.walk(root, onerror=failed, followlinks=False):
        for name in list(directories):
            path = Path(base) / name
            if excluded(path.relative_to(root)):
                directories.remove(name)
                omissions.append(path.relative_to(root).as_posix() + "/")
            else:
                reject_link(path)
        for name in names:
            path = Path(base) / name
            if excluded(path.relative_to(root)):
                omissions.append(path.relative_to(root).as_posix())
                continue
            reject_link(path)
            files.append(path)
    return sorted(files), sorted(omissions)


def validate_records(manifest):
    seen = set()
    for record in manifest["files"]:
        name = record["path"]
        path = PurePosixPath(name)
        if (not name or path.is_absolute() or ".." in path.parts or ":" in name
                or "\\" in name or name in seen or name == MANIFEST):
            raise ValueError(f"Unsafe or duplicate manifest path: {name}")
        seen.add(name)
    return seen


def verify_archive(path):
    with zipfile.ZipFile(path) as archive:
        manifest = json.loads(archive.read(MANIFEST))
        expected = validate_records(manifest) | {MANIFEST}
        if set(archive.namelist()) != expected or len(archive.namelist()) != len(expected):
            raise ValueError("Archive entries do not match manifest")
        for record in manifest["files"]:
            if archive.getinfo(record["path"]).file_size != record["bytes"]:
                raise ValueError(f"Size mismatch: {record['path']}")
            with archive.open(record["path"]) as stream:
                if digest(stream) != record["sha256"]:
                    raise ValueError(f"SHA-256 mismatch: {record['path']}")
    return {"verified_files": len(manifest["files"]), "verified_source_bytes": manifest["total_bytes"]}


def verify_directory(root):
    root = root.resolve()
    manifest = json.loads((root / MANIFEST).read_text(encoding="utf-8"))
    validate_records(manifest)
    for record in manifest["files"]:
        path = root / record["path"]
        if not path.resolve().is_relative_to(root):
            raise ValueError(f"Manifest path escapes snapshot: {record['path']}")
        if path.stat().st_size != record["bytes"]:
            raise ValueError(f"Size mismatch: {record['path']}")
        with path.open("rb") as stream:
            if digest(stream) != record["sha256"]:
                raise ValueError(f"SHA-256 mismatch: {record['path']}")
    return {"verified_files": len(manifest["files"]), "verified_source_bytes": manifest["total_bytes"]}


def package(root, output):
    root, output = root.resolve(), output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    files, omissions = project_files(root)
    files = [path for path in files if path != output]
    manifest = {"schema_version": 1, "created_utc": datetime.now(timezone.utc).isoformat(),
                "scope": "Project tools, documentation, tests, and local research evidence; game installation and chat transcript are external.",
                "excluded_entries": omissions, "files": []}
    with zipfile.ZipFile(output, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in files:
            name = path.relative_to(root).as_posix()
            original = path.stat()
            value = hashlib.sha256()
            size = 0
            with path.open("rb") as source, archive.open(name, "w", force_zip64=True) as destination:
                for block in iter(lambda: source.read(CHUNK), b""):
                    value.update(block)
                    destination.write(block)
                    size += len(block)
            current = path.stat()
            if (original.st_size, original.st_mtime_ns) != (current.st_size, current.st_mtime_ns) or size != original.st_size:
                raise ValueError(f"Source changed during packaging: {name}")
            manifest["files"].append({"path": name, "bytes": size, "sha256": value.hexdigest()})
        manifest["total_bytes"] = sum(record["bytes"] for record in manifest["files"])
        archive.writestr(MANIFEST, json.dumps(manifest, indent=2) + "\n")
    verification = verify_archive(output)
    manifest_path = output.with_suffix(".manifest.json")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    with output.open("rb") as stream:
        archive_hash = digest(stream)
    output.with_suffix(".sha256.txt").write_text(f"{archive_hash}  {output.name}\n", encoding="utf-8")
    return {"archive": str(output), "archive_bytes": output.stat().st_size,
            "archive_sha256": archive_hash, **verification, "excluded_entries": omissions}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--verify", type=Path, help="Verify a freshly extracted snapshot directory")
    action.add_argument("--verify-archive", type=Path, help="Verify the ZIP against its embedded manifest")
    parser.add_argument("--output", type=Path, default=ROOT / "_transfer/Aion-research-transfer.zip")
    args = parser.parse_args()
    try:
        if args.verify is not None:
            result = verify_directory(args.verify)
        elif args.verify_archive is not None:
            result = verify_archive(args.verify_archive)
        else:
            result = package(ROOT, args.output)
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        parser.exit(1, f"Transfer failed: {error}\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
