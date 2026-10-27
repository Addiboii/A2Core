"""Bounded AES-256 candidate checks against installed Pak index SHA-1 values.

Windows CNG supplies AES; no third-party Python dependency is used. A hash match
validates only the tested index, not every container or package deserialization.
"""

import ctypes
import hashlib
import os
from pathlib import Path
import re

try:
    from .inspect_formats import pak_metadata
except ImportError:
    from inspect_formats import pak_metadata

HEX = rb"[0-9a-fA-F]"
KEY_PATTERNS = (
    ("ascii", re.compile(rb"(?<![0-9a-fA-F])" + HEX + rb"{64}(?![0-9a-fA-F])")),
    ("utf-16le", re.compile(rb"(?<![0-9a-fA-F]\x00)(?:" + HEX + rb"\x00){64}(?![0-9a-fA-F]\x00)")),
)


def key_candidates(data, offset=0):
    """Only bounded textual hex keys; arbitrary 32-byte memory windows are not tried."""
    for encoding, pattern in KEY_PATTERNS:
        for match in pattern.finditer(data):
            key = bytes.fromhex(match.group().decode(encoding))
            yield key, {"offset": offset + match.start(), "encoding": encoding}


class AES256:
    def __init__(self):
        if os.name != "nt":
            raise ValueError("AES validation requires Windows CNG")
        from ctypes import wintypes as w
        self.lib = ctypes.WinDLL("bcrypt")
        void = ctypes.c_void_p
        specs = {
            "BCryptOpenAlgorithmProvider": [ctypes.POINTER(void), w.LPCWSTR, w.LPCWSTR, w.ULONG],
            "BCryptSetProperty": [void, w.LPCWSTR, void, w.ULONG, w.ULONG],
            "BCryptGetProperty": [void, w.LPCWSTR, void, w.ULONG, ctypes.POINTER(w.ULONG), w.ULONG],
            "BCryptGenerateSymmetricKey": [void, ctypes.POINTER(void), void, w.ULONG, void, w.ULONG, w.ULONG],
            "BCryptDecrypt": [void, void, w.ULONG, void, void, w.ULONG, void, w.ULONG, ctypes.POINTER(w.ULONG), w.ULONG],
            "BCryptDestroyKey": [void],
            "BCryptCloseAlgorithmProvider": [void, w.ULONG],
        }
        for name, args in specs.items():
            getattr(self.lib, name).argtypes = args
            getattr(self.lib, name).restype = w.LONG
        self.algorithm = void()
        self.check(self.lib.BCryptOpenAlgorithmProvider(ctypes.byref(self.algorithm), "AES", None, 0))
        try:
            mode = ctypes.create_unicode_buffer("ChainingModeECB")
            self.check(self.lib.BCryptSetProperty(self.algorithm, "ChainingMode", mode, ctypes.sizeof(mode), 0))
            size, received = w.ULONG(), w.ULONG()
            self.check(self.lib.BCryptGetProperty(self.algorithm, "ObjectLength", ctypes.byref(size), ctypes.sizeof(size), ctypes.byref(received), 0))
            self.object_size = size.value
        except BaseException:
            self.close()
            raise

    @staticmethod
    def check(status):
        if status < 0:
            raise OSError(f"Windows CNG error 0x{status & 0xffffffff:08x}")

    def decrypt(self, key, ciphertext):
        if len(key) != 32 or not ciphertext or len(ciphertext) % 16:
            raise ValueError("AES-256 requires a 32-byte key and complete 16-byte blocks")
        from ctypes import wintypes as w
        handle = ctypes.c_void_p()
        state = ctypes.create_string_buffer(self.object_size)
        secret = ctypes.create_string_buffer(key)
        self.check(self.lib.BCryptGenerateSymmetricKey(self.algorithm, ctypes.byref(handle), state,
                   self.object_size, secret, len(key), 0))
        try:
            source = ctypes.create_string_buffer(ciphertext)
            target = ctypes.create_string_buffer(len(ciphertext))
            received = w.ULONG()
            self.check(self.lib.BCryptDecrypt(handle, source, len(ciphertext), None, None, 0,
                       target, len(ciphertext), ctypes.byref(received), 0))
            if received.value != len(ciphertext):
                raise ValueError("Unexpected decrypted index length")
            return target.raw
        finally:
            self.lib.BCryptDestroyKey(handle)

    def close(self):
        if self.algorithm:
            self.lib.BCryptCloseAlgorithmProvider(self.algorithm, 0)
            self.algorithm = ctypes.c_void_p()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


def load_targets(install, count=3):
    candidates = []
    for path in sorted(install.rglob("*.pak")):
        if "Paks" not in path.parts:
            continue
        meta = pak_metadata(path)
        size = meta.get("index_size", 0)
        if meta.get("index_encrypted") and 16 <= size <= 1024 * 1024 and size % 16 == 0:
            candidates.append((size, path, meta))
    targets = []
    seen_hashes = set()
    for size, path, meta in sorted(candidates):
        if meta["index_sha1"] in seen_hashes:
            continue
        with path.open("rb") as stream:
            stream.seek(meta["index_offset"])
            ciphertext = stream.read(size)
        if len(ciphertext) != size:
            raise ValueError("Truncated Pak index")
        if hashlib.sha1(ciphertext).hexdigest() == meta["index_sha1"]:
            raise ValueError("Stored index already matches hash despite encryption flag; inspect separately")
        targets.append({"path": path.relative_to(install).as_posix(), "ciphertext": ciphertext,
                        "index_sha1": meta["index_sha1"], "index_offset": meta["index_offset"],
                        "ciphertext_sha256": hashlib.sha256(ciphertext).hexdigest()})
        seen_hashes.add(meta["index_sha1"])
        if len(targets) == count:
            break
    if len(targets) < 2:
        raise ValueError("Need at least two distinct encrypted-index hashes")
    return targets


def validate(key, targets, decrypt):
    matches = []
    for target in targets:
        plain = decrypt(key, target["ciphertext"])
        # IndexSize includes storage alignment in observed encrypted Paks. Check
        # full length and up to one block's trailing alignment; record exact match.
        lengths = [n for n in range(len(plain), max(0, len(plain) - 16), -1)
                   if hashlib.sha1(plain[:n]).hexdigest() == target["index_sha1"]]
        if not lengths:
            return []
        matches.append({"path": target["path"], "index_sha1": target["index_sha1"],
                        "hash_matched_bytes": lengths[0], "storage_bytes": len(plain)})
    return matches if len(matches) >= 2 else []
