"""Read UE build markers from an already-running AION2 main image on Windows.

Uses read/query process rights only. Does not launch, modify, inject into, or
dump the process. Account data and arbitrary strings are not included in output.
"""

import argparse
import ctypes
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import struct

PATTERNS = (
    ("ascii", re.compile(rb"\+\+UE[45]\+Release-[0-9]+\.[0-9]+(?:\.[0-9]+)?")),
    ("utf-16le", re.compile(rb"\+\x00\+\x00U\x00E\x00[45]\x00\+\x00R\x00e\x00l\x00e\x00a\x00s\x00e\x00-\x00[0-9]\x00(?:[0-9]\x00)*\.\x00[0-9]\x00(?:[0-9]\x00)*(?:\.\x00[0-9]\x00(?:[0-9]\x00)*)?")),
)
MAX_IMAGE = 512 * 1024 * 1024
READ_BYTES = 1024 * 1024
OVERLAP = 256


def markers(data, offset=0):
    result = []
    for encoding, pattern in PATTERNS:
        for match in pattern.finditer(data):
            result.append({"rva": offset + match.start(), "encoding": encoding,
                           "marker": match.group().decode(encoding)})
    return result


def inspect(pid, executable, block_observer=None, finish_observer=None):
    if os.name != "nt" or struct.calcsize("P") != 8:
        raise ValueError("Requires 64-bit Python on Windows")
    from ctypes import wintypes as w

    class Module(ctypes.Structure):
        _fields_ = [("size", w.DWORD), ("module_id", w.DWORD), ("pid", w.DWORD),
                    ("global_usage", w.DWORD), ("process_usage", w.DWORD),
                    ("base", ctypes.c_void_p), ("image_size", w.DWORD),
                    ("handle", w.HMODULE), ("name", w.WCHAR * 256),
                    ("path", w.WCHAR * 260)]

    class Region(ctypes.Structure):
        _fields_ = [("base", ctypes.c_void_p), ("allocation_base", ctypes.c_void_p),
                    ("allocation_protect", w.DWORD), ("partition_id", w.WORD),
                    ("size", ctypes.c_size_t), ("state", w.DWORD),
                    ("protect", w.DWORD), ("kind", w.DWORD)]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    signatures = {
        "OpenProcess": ([w.DWORD, w.BOOL, w.DWORD], w.HANDLE),
        "CloseHandle": ([w.HANDLE], w.BOOL),
        "QueryFullProcessImageNameW": ([w.HANDLE, w.DWORD, w.LPWSTR, ctypes.POINTER(w.DWORD)], w.BOOL),
        "CreateToolhelp32Snapshot": ([w.DWORD, w.DWORD], w.HANDLE),
        "Module32FirstW": ([w.HANDLE, ctypes.POINTER(Module)], w.BOOL),
        "Module32NextW": ([w.HANDLE, ctypes.POINTER(Module)], w.BOOL),
        "VirtualQueryEx": ([w.HANDLE, ctypes.c_void_p, ctypes.POINTER(Region), ctypes.c_size_t], ctypes.c_size_t),
        "ReadProcessMemory": ([w.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                               ctypes.POINTER(ctypes.c_size_t)], w.BOOL),
    }
    for name, (args, result) in signatures.items():
        getattr(kernel, name).argtypes = args
        getattr(kernel, name).restype = result
    process = kernel.OpenProcess(0x410, False, pid)  # QUERY_INFORMATION | VM_READ
    if not process:
        error = ctypes.WinError(ctypes.get_last_error())
        raise OSError(f"OpenProcess(QUERY_INFORMATION | VM_READ) failed: {error}") from error
    try:
        name, length = ctypes.create_unicode_buffer(32768), w.DWORD(32768)
        if not kernel.QueryFullProcessImageNameW(process, 0, name, ctypes.byref(length)):
            raise OSError(f"QueryFullProcessImageNameW failed: {ctypes.WinError(ctypes.get_last_error())}")
        if Path(name.value).resolve() != executable.resolve():
            raise ValueError("Process image does not match the specified AION2 executable")
        snapshot = kernel.CreateToolhelp32Snapshot(0x18, pid)
        if snapshot == ctypes.c_void_p(-1).value:
            raise OSError(f"CreateToolhelp32Snapshot failed: {ctypes.WinError(ctypes.get_last_error())}")
        try:
            module = Module()
            module.size = ctypes.sizeof(module)
            valid = kernel.Module32FirstW(snapshot, ctypes.byref(module))
            found = False
            while valid:
                if Path(module.path).resolve() == executable.resolve():
                    base, size = module.base, module.image_size
                    found = True
                    break
                valid = kernel.Module32NextW(snapshot, ctypes.byref(module))
            if not found:
                raise ValueError("Main image not found in module snapshot")
        finally:
            kernel.CloseHandle(snapshot)
        if not base or not 0 < size <= MAX_IMAGE:
            raise ValueError("Main image exceeds configured bounds")
        cursor, end = base, base + size
        read_bytes = skipped_bytes = 0
        hits = {}
        tail = b""
        failures = []
        while cursor < end:
            region = Region()
            if not kernel.VirtualQueryEx(process, cursor, ctypes.byref(region), ctypes.sizeof(region)):
                failures.append({"rva": cursor - base, "operation": "query", "winerror": ctypes.get_last_error()})
                skipped_bytes += end - cursor
                break
            stop = min(end, region.base + region.size)
            if stop <= cursor:
                raise ValueError("Invalid memory region bounds")
            # Do not touch guard, inaccessible, reserved, or execute-only pages.
            if region.state != 0x1000 or region.protect & 0x101 or not region.protect & 0xEE:
                skipped_bytes += stop - cursor
                tail = b""
                cursor = stop
                continue
            while cursor < stop:
                amount = min(READ_BYTES, stop - cursor)
                buffer = ctypes.create_string_buffer(amount)
                got = ctypes.c_size_t()
                ok = kernel.ReadProcessMemory(process, cursor, buffer, amount, ctypes.byref(got))
                if ok and got.value == amount:
                    data = tail + buffer.raw
                    if block_observer is not None:
                        block_observer(data, cursor - base - len(tail))
                    for hit in markers(data, cursor - base - len(tail)):
                        hits[(hit["rva"], hit["encoding"])] = hit
                    tail = data[-OVERLAP:]
                    read_bytes += amount
                else:
                    if len(failures) < 100:
                        failures.append({"rva": cursor - base, "operation": "read", "winerror": ctypes.get_last_error()})
                    skipped_bytes += amount
                    tail = b""
                cursor += amount
        result = {"schema_version": 1, "collected_utc": datetime.now(timezone.utc).isoformat(),
                "pid": pid, "executable": str(executable.resolve()), "image_size": size,
                "image_base": int(base or 0),
                "bytes_read": read_bytes, "bytes_skipped": skipped_bytes,
                "markers": sorted(hits.values(), key=lambda h: h["rva"]), "failures": failures,
                "limitations": ["Only the loaded main image is scanned, and only UE release branch markers are retained.",
                                "A marker is a version candidate, not proof of asset-reader compatibility.",
                                "No marker found does not rule out a customized engine or markers elsewhere."]}
        if finish_observer is not None:
            total = 0
            requests = 0

            def read_bounded(address, length):
                nonlocal total, requests
                if not 0x10000 <= address < 0x800000000000 or not 1 <= length <= 4096:
                    raise ValueError("Targeted read exceeds address/size bounds")
                total += length
                requests += 1
                if total > 65536 or requests > 64:
                    raise ValueError("Targeted read exceeds total budget")
                at, stop = address, address + length
                while at < stop:
                    region = Region()
                    if not kernel.VirtualQueryEx(process, at, ctypes.byref(region), ctypes.sizeof(region)):
                        raise OSError("Targeted memory query failed")
                    if region.state != 0x1000 or region.protect & 0x101 or not region.protect & 0xEE:
                        raise OSError("Targeted memory is not committed/readable")
                    next_at = min(stop, region.base + region.size)
                    if next_at <= at:
                        raise ValueError("Invalid targeted memory region bounds")
                    at = next_at
                buffer = ctypes.create_string_buffer(length)
                got = ctypes.c_size_t()
                if not kernel.ReadProcessMemory(process, address, buffer, length, ctypes.byref(got)) or got.value != length:
                    raise OSError("Targeted memory read failed")
                return buffer.raw

            result["targeted_observation"] = finish_observer(base, size, read_bounded)
            result["targeted_read_requests"] = requests
            result["targeted_requested_bytes"] = total
        return result
    finally:
        kernel.CloseHandle(process)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", required=True, type=int)
    parser.add_argument("--install", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root, output = args.install.resolve(), args.output.resolve()
    executable = root / "Aion2/Binaries/Win64/AION2.exe"
    if not executable.is_file() or output.is_relative_to(root):
        parser.error("Provide an AION2 installation and an output outside it")
    try:
        result = inspect(args.pid, executable)
        result["status"] = "scanned"
    except (OSError, ValueError) as error:
        result = {"schema_version": 1, "collected_utc": datetime.now(timezone.utc).isoformat(),
                  "pid": args.pid, "expected_executable": str(executable), "status": "probe_failed",
                  "error": str(error),
                  "note": "No successful scan result; this does not establish marker absence."}
    try:
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("x", encoding="utf-8") as stream:
            json.dump(result, stream, indent=2)
            stream.write("\n")
    except (OSError, ValueError) as error:
        parser.exit(1, f"Read-only build probe failed: {error}\n")
    print(json.dumps(result, indent=2))
    if result["status"] == "probe_failed":
        parser.exit(1)


if __name__ == "__main__":
    main()
