"""Bounded ctypes adapter for an explicitly supplied Windows x64 Oodle DLL."""
import ctypes
import hashlib
import os
from pathlib import Path


class OodleDecoder:
    def __init__(self, path):
        if os.name != 'nt' or ctypes.sizeof(ctypes.c_void_p) != 8:
            raise ValueError('Oodle adapter requires Windows x64')
        path = Path(path).resolve(strict=True)
        self.sha256 = hashlib.sha256(path.read_bytes()).hexdigest()
        self.lib = ctypes.CDLL(str(path))
        self.decode = self.lib.OodleLZ_Decompress
        ptr, size, integer = ctypes.c_void_p, ctypes.c_ssize_t, ctypes.c_int
        self.decode.argtypes = [ptr, size, ptr, size, integer, integer, integer,
                                ptr, size, ptr, ptr, ptr, size, integer]
        self.decode.restype = size

    def __call__(self, data, expected):
        if not data or len(data) > 16 * 1024 * 1024 or not 0 < expected <= 16 * 1024 * 1024:
            raise ValueError('Oodle block exceeds size limits')
        source = ctypes.create_string_buffer(data)
        target = ctypes.create_string_buffer(expected)
        # FuzzSafe=Yes, CheckCRC=No, Verbosity=None, ThreadPhase=All.
        count = self.decode(source, len(data), target, expected, 1, 0, 0,
                            None, 0, None, None, None, 0, 3)
        if count != expected:
            raise ValueError(f'Oodle returned {count} bytes; expected {expected}')
        return target.raw
