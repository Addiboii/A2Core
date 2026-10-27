"""Inspect cached Steam launch metadata and AION2 process history, offline."""

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import struct

try:
    from .inspect_formats import pe_metadata
except ImportError:
    from inspect_formats import pe_metadata

APP_ID = 3393110
DEFAULT_STEAM = Path(r"C:\Program Files (x86)\Steam")


class BinaryKV:
    """Bounded reader for the string-table form used by appinfo version 29."""

    def __init__(self, data, strings):
        self.data, self.strings, self.offset = data, strings, 0

    def take(self, count):
        if count < 0 or self.offset + count > len(self.data):
            raise ValueError("Truncated binary KeyValues")
        value = self.data[self.offset:self.offset + count]
        self.offset += count
        return value

    def number(self, fmt):
        return struct.unpack("<" + fmt, self.take(struct.calcsize("<" + fmt)))[0]

    def string(self):
        end = self.data.find(b"\0", self.offset)
        if end < 0:
            raise ValueError("Unterminated binary KeyValues string")
        return self.take(end - self.offset + 1)[:-1].decode("utf-8", errors="replace")

    def object(self, depth=0):
        if depth > 32:
            raise ValueError("Binary KeyValues nesting limit exceeded")
        result = {}
        while True:
            kind = self.number("B")
            if kind == 8:
                return result
            index = self.number("I")
            if index >= len(self.strings):
                raise ValueError("Invalid string-table index")
            key = self.strings[index]
            if kind == 0:
                value = self.object(depth + 1)
            elif kind == 1:
                value = self.string()
            elif kind in (2, 3, 4, 6, 7, 10):
                value = self.number({2: "i", 3: "f", 4: "I", 6: "I", 7: "Q", 10: "q"}[kind])
            else:
                raise ValueError(f"Unsupported binary KeyValues type {kind}")
            if key in result:
                raise ValueError(f"Duplicate binary KeyValues key: {key}")
            result[key] = value


def read_appinfo(path, app_id):
    data = path.read_bytes()
    if len(data) < 16:
        raise ValueError("Truncated appinfo header")
    magic, universe, string_offset = struct.unpack_from("<IIQ", data)
    if magic != 0x07564429:
        raise ValueError(f"Unsupported appinfo version {magic:#x}")
    if not 16 <= string_offset <= len(data) - 4:
        raise ValueError("Invalid appinfo string-table offset")
    string_count, = struct.unpack_from("<I", data, string_offset)
    strings = data[string_offset + 4:].split(b"\0")
    if len(strings) != string_count + 1 or strings[-1] != b"":
        raise ValueError("Appinfo string-table count mismatch")
    strings = [value.decode("utf-8", errors="replace") for value in strings[:-1]]
    offset = 16
    while offset + 4 <= string_offset:
        current_id, = struct.unpack_from("<I", data, offset)
        if not current_id:
            break
        if offset + 8 > string_offset:
            raise ValueError("Truncated appinfo record header")
        size, = struct.unpack_from("<I", data, offset + 4)
        end = offset + 8 + size
        if size < 60 or end > string_offset:
            raise ValueError("Invalid appinfo record bounds")
        if current_id == app_id:
            parsed = BinaryKV(data[offset + 68:end], strings).object()
            return {"universe": universe, "appinfo": parsed["appinfo"]}
        offset = end
    raise ValueError(f"App {app_id} is absent from the Steam cache")


def argument_summary(command):
    """Record switch names and one known benign value, never opaque payloads."""
    flags = sorted(set(re.findall(r"(?<!\S)-([A-Za-z][A-Za-z0-9_-]*)(?=[:=\s\"]|$)", command)))
    suffix = re.search(r"(?<!\S)-saveddirsuffix=([A-Za-z0-9_-]+)(?=\s|\"|$)", command, re.I)
    return {"switches": flags, "saved_dir_suffix": suffix.group(1) if suffix else None,
            "opaque_lp_argument_present": bool(re.search(r"(?<!\S)-lp[:=]", command, re.I))}


def process_history(path, app_id):
    events = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if not re.search(rf"\b(?:AppID |Remove ){app_id}\b", line):
            continue
        timestamp = re.match(r"\[([^\]]+)\]", line)
        event = {"timestamp_as_logged": timestamp.group(1) if timestamp else None}
        pid = re.search(r"\bPID (\d+)\b", line)
        if pid:
            event["pid"] = int(pid.group(1))
        if "adding PID" in line:
            event["event"] = "process_tracked"
            event["arguments"] = argument_summary(line)
            event["aion2_executable_named"] = bool(re.search(r"[\\/]AION2\.exe\b", line, re.I))
        elif "no longer tracking" in line:
            event["event"] = "process_exited"
            code = re.search(r"exit code (-?\d+)", line)
            event["exit_code"] = int(code.group(1)) if code else None
        elif "from running list" in line:
            event["event"] = "app_removed_from_running_list"
        else:
            event["event"] = "other_app_event"
        events.append(event)
    return events


def dependencies(install):
    records = []
    for name in ("NCTinyUpdater.dll", "libpwagent.dll", "libpwlog.dll", "game_presence-64-vc_v142-MD.dll"):
        path = install / "Aion2/Binaries/Win64" / name
        record = {"file": name}
        try:
            pe = pe_metadata(path)
            data = path.read_bytes()
            # Exclude certificate-table URLs; these describe code signing, not services.
            certificate = pe["certificate_table"]
            if certificate["size"]:
                start = certificate["file_offset"]
                data = data[:start] + bytes(certificate["size"]) + data[start + certificate["size"]:]
            hosts = {match.group(1).decode("ascii").lower() for match in re.finditer(
                rb"https?://([A-Za-z0-9][A-Za-z0-9.-]+)(?=[:/\x00\s])", data)}
            record.update(sha256=pe["sha256"], imports=pe["imports"], delay_imports=pe["delay_imports"],
                          url_host_candidates=sorted(hosts))
        except (OSError, ValueError, struct.error, IndexError) as error:
            record["error"] = str(error)
        records.append(record)
    return records


def saved_metadata(saved):
    result = {"present": saved.is_dir()}
    network_state = saved / "webcache_4430/Network Persistent State"
    if network_state.is_file():
        text = network_state.read_text(encoding="utf-8")
        result["cef_cached_url_hosts"] = sorted({match.group(1).lower() for match in re.finditer(
            r'https?://([A-Za-z0-9][A-Za-z0-9.-]+)(?=[:/\s"])', text)})
    platform = saved / "Config/Windows/GamePlatform.ini"
    if platform.is_file():
        text = platform.read_text(encoding="utf-8-sig")
        result["game_platform_sections"] = re.findall(r"^\[([^\]\r\n]+)\]", text, re.M)
        result["game_platform_keys"] = sorted(set(re.findall(r"^([^=\r\n;\[]+)=", text, re.M)))
    return result


def collect(steam, app_id, saved=None, library=None):
    library = library if library is not None else steam
    manifest_path = library / f"steamapps/appmanifest_{app_id}.acf"
    manifest = manifest_path.read_text(encoding="utf-8-sig")
    allowed = {"appid", "name", "installdir", "buildid", "StateFlags", "LastUpdated",
               "LastPlayed", "SizeOnDisk", "TargetBuildID"}
    fields = {key: value for key, value in re.findall(r'"([^"\r\n]+)"\s+"([^"\r\n]*)"', manifest) if key in allowed}
    cached = read_appinfo(steam / "appcache/appinfo.vdf", app_id)
    info = cached["appinfo"]
    config = info.get("config", {})
    launches = []
    for key, launch in config.get("launch", {}).items():
        if not isinstance(launch, dict):
            continue
        record = {"id": key}
        for field in ("executable", "workingdir", "type", "description"):
            if field in launch:
                record[field] = launch[field]
        record["arguments"] = argument_summary(launch.get("arguments", ""))
        record["platform"] = {key: value for key, value in launch.get("config", {}).items()
                              if key in ("oslist", "osarch")}
        record["branch_condition"] = launch.get("config", {}).get("BetaKey")
        launches.append(record)
    install_name = fields["installdir"]
    if "/" in install_name or "\\" in install_name or install_name in (".", ".."):
        raise ValueError("Unexpected installation directory name")
    install = library / "steamapps/common" / install_name
    return {"schema_version": 1, "collected_utc": datetime.now(timezone.utc).isoformat(),
            "app_id": app_id, "manifest": fields,
            "source_paths": {"steam": str(steam), "library": str(library), "installation": str(install)},
            "cached_app_name": info.get("common", {}).get("name"),
            "launches": launches,
            "bootstrap_libraries": dependencies(install),
            "saved_data_metadata": saved_metadata(saved) if saved else None,
            "process_history": process_history(steam / "logs/gameprocess_log.txt", app_id)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--steam", type=Path, default=DEFAULT_STEAM)
    parser.add_argument("--library", type=Path, help="Steam library root containing steamapps; defaults to --steam")
    parser.add_argument("--app-id", type=int, default=APP_ID)
    parser.add_argument("--saved-data", type=Path, default=(
        Path(os.environ["LOCALAPPDATA"]) / "Aion2/Saved_Steam" if "LOCALAPPDATA" in os.environ else None))
    parser.add_argument("--output", type=Path, default=Path("research/local/bootstrap.json"))
    args = parser.parse_args()
    steam, output = args.steam.resolve(), args.output.resolve()
    if output.is_relative_to(steam):
        parser.error("Output must be outside the Steam directory")
    library = args.library.resolve() if args.library else steam
    if output.is_relative_to(library):
        parser.error("Output must be outside the Steam library")
    if args.saved_data and output.is_relative_to(args.saved_data.resolve()):
        parser.error("Output must be outside the saved-data directory")
    try:
        report = collect(steam, args.app_id, args.saved_data, library)
    except (OSError, ValueError, KeyError, struct.error) as error:
        parser.exit(1, f"Bootstrap inspection failed: {error}\n")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Saved {output}")
    print(f"App {args.app_id}, build {report['manifest'].get('buildid')}: "
          f"{len(report['launches'])} cached launches, {len(report['process_history'])} history events")
    for library in report["bootstrap_libraries"]:
        print(library["file"], library.get("url_host_candidates", library.get("error")))


if __name__ == "__main__":
    main()
