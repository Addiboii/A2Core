"""Read-only deep memory scan for AES-256 archive key candidates on Windows.

Enumerates committed readable regions (private, image, mapped) via VirtualQueryEx.
Does not attach a debugger, inject code, or write to the target process.
"""

from datetime import datetime, timezone
import ctypes
import os
from pathlib import Path
import struct

try:
    from .inspect_live_build import OVERLAP, READ_BYTES
    from .key_constants import (
        binary_key_candidates,
        faes_register_candidates,
        raw_key_prefilter,
        vector_key_candidates,
    )
    from .archive_keys import key_candidates
except ImportError:
    from inspect_live_build import OVERLAP, READ_BYTES
    from key_constants import (
        binary_key_candidates,
        faes_register_candidates,
        raw_key_prefilter,
        vector_key_candidates,
    )
    from archive_keys import key_candidates

MEM_COMMIT = 0x1000
MEM_PRIVATE = 0x20000
MEM_MAPPED = 0x40000
MEM_IMAGE = 0x1000000
USER_MIN = 0x10000
USER_MAX = 0x800000000000
DEEP_MAX_TOTAL_READ = 8 * 1024 * 1024 * 1024
DEEP_MAX_SINGLE_REGION = 512 * 1024 * 1024
RAW_PHASES = 16


def _region_kind(protect, kind):
    if kind & MEM_IMAGE:
        return "image"
    if kind & MEM_PRIVATE:
        return "private"
    if kind & MEM_MAPPED:
        return "mapped"
    return "unknown"


def _region_readable(protect):
    if protect & 0x101:
        return False
    return bool(protect & 0xEE)


def observe_deep(check, data, base_va, region_kind, module_name=None):
    """Submit textual, compiled, vector, FAES-layout, and raw window candidates."""
    for key, source in key_candidates(data, base_va):
        source = {**source, "region": region_kind}
        if module_name:
            source["module"] = module_name
        check.offer(key, source)
        if check.verified and check.stop_on_verify:
            return True
    for key, source in binary_key_candidates(data, base_va):
        source = {**source, "region": region_kind}
        if module_name:
            source["module"] = module_name
        check.offer(key, source)
        if check.verified and check.stop_on_verify:
            return True
    for key, source in vector_key_candidates(data, base_va):
        source = {**source, "region": region_kind}
        if module_name:
            source["module"] = module_name
        check.offer(key, source)
        if check.verified and check.stop_on_verify:
            return True
    for key, source in faes_register_candidates(data, base_va):
        source = {**source, "region": region_kind}
        if module_name:
            source["module"] = module_name
        check.offer(key, source)
        if check.verified and check.stop_on_verify:
            return True
    limit = len(data) - 31
    if limit <= 0:
        return False
    for phase in range(RAW_PHASES):
        cursor = phase
        while cursor < limit:
            window = data[cursor:cursor + 32]
            if raw_key_prefilter(window):
                source = {"encoding": "raw_memory_window", "offset": base_va + cursor,
                          "region": region_kind, "alignment_phase": phase}
                if module_name:
                    source["module"] = module_name
                check.offer(window, source)
                if check.verified and check.stop_on_verify:
                    return True
            cursor += RAW_PHASES
    return False


def scan_deep(pid, executable, check):
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

    process = kernel.OpenProcess(0x410, False, pid)
    if not process:
        raise OSError(f"OpenProcess failed: {ctypes.WinError(ctypes.get_last_error())}")
    modules = []
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
            while valid:
                base = int(module.base or 0)
                modules.append({"base": base, "size": module.image_size,
                                "name": module.name, "path": module.path})
                valid = kernel.Module32NextW(snapshot, ctypes.byref(module))
        finally:
            kernel.CloseHandle(snapshot)

        def module_for(address):
            for entry in modules:
                if entry["base"] <= address < entry["base"] + entry["size"]:
                    return entry["name"]
            return None

        cursor = USER_MIN
        read_bytes = skipped_bytes = regions_scanned = 0
        failures = []
        tail = b""
        tail_base = 0
        while cursor < USER_MAX:
            if check.verified and check.stop_on_verify:
                break
            if read_bytes >= DEEP_MAX_TOTAL_READ:
                break
            region = Region()
            if not kernel.VirtualQueryEx(process, cursor, ctypes.byref(region), ctypes.sizeof(region)):
                break
            base = int(region.base or 0)
            size = int(region.size or 0)
            if size <= 0:
                break
            stop = base + size
            if stop <= cursor:
                cursor += 4096
                continue
            cursor = max(cursor, base)
            kind = _region_kind(region.protect, region.kind)
            allowed_kind = region.kind & (MEM_PRIVATE | MEM_MAPPED | MEM_IMAGE)
            if region.state != MEM_COMMIT or not allowed_kind or not _region_readable(region.protect):
                skipped_bytes += stop - cursor
                tail = b""
                cursor = stop
                continue
            if size > DEEP_MAX_SINGLE_REGION:
                skipped_bytes += stop - cursor
                tail = b""
                cursor = stop
                continue
            regions_scanned += 1
            region_end = stop
            while cursor < region_end:
                if check.verified and check.stop_on_verify:
                    break
                if read_bytes >= DEEP_MAX_TOTAL_READ:
                    break
                amount = min(READ_BYTES, region_end - cursor)
                buffer = ctypes.create_string_buffer(amount)
                got = ctypes.c_size_t()
                ok = kernel.ReadProcessMemory(process, cursor, buffer, amount, ctypes.byref(got))
                if ok and got.value == amount:
                    chunk = tail + buffer.raw
                    chunk_base = cursor - len(tail)
                    mod = module_for(chunk_base)
                    if observe_deep(check, chunk, chunk_base, kind, mod):
                        break
                    tail = chunk[-OVERLAP:]
                    read_bytes += amount
                else:
                    if len(failures) < 100:
                        failures.append({"va": cursor, "operation": "read",
                                         "winerror": ctypes.get_last_error(), "region": kind})
                    skipped_bytes += amount
                    tail = b""
                cursor += amount
            if cursor < region_end:
                cursor = region_end
        return {"schema_version": 1, "collected_utc": datetime.now(timezone.utc).isoformat(),
                "pid": pid, "executable": str(executable.resolve()),
                "bytes_read": read_bytes, "bytes_skipped": skipped_bytes,
                "regions_scanned": regions_scanned, "modules": len(modules),
                "read_budget_bytes": DEEP_MAX_TOTAL_READ, "failures": failures,
                "limitations": ["Committed private, mapped, and image regions only.",
                                "Guard, no-access, and oversized single regions are skipped.",
                                "Raw 32-byte windows use entropy and zero-run prefilters.",
                                "No debugger attach, injection, or process writes."]}
    finally:
        kernel.CloseHandle(process)
