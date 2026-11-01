"""Recognize x64 constant-construction and FAES-layout patterns, not full disassembly.

Byte matches need not be executed instructions. All resulting candidates require
independent cryptographic validation before being called archive keys.
"""

import re
import struct

STARTS = re.compile(rb"[\x40-\x4f]?\xc7|[\x48-\x4f][\xb8-\xbf]")
SSE_RIP_LOAD = re.compile(
    rb"(?:\x66)?\x0f(?:\x10|\x28|\x6f)\x05(.{4})|"
    rb"(?:\xf3)?\x0f(?:\x10|\x28|\x6f)\x05(.{4})",
    re.DOTALL,
)
FAES_STRIDE = 8


def raw_key_prefilter(window):
    if len(window) != 32:
        return False
    zeros = window.count(0)
    if zeros > 6 or len(set(window)) < 12:
        return False
    run = 0
    for byte in window:
        if byte == 0:
            run += 1
            if run >= 8:
                return False
        else:
            run = 0
    if window in (b"\xff" * 32, b"\x00" * 32):
        return False
    return True


def _rip_target(instruction_end, disp):
    return (instruction_end + struct.unpack("<i", disp)[0]) & ((1 << 64) - 1)


def vector_key_candidates(data, offset=0, image_base=0):
    """Pair RIP-relative SSE 128-bit loads that may materialize a 32-byte key."""
    base_va = image_base if image_base else offset
    for match in SSE_RIP_LOAD.finditer(data):
        start = match.start()
        disp = match.group(1) or match.group(2)
        if not disp or len(disp) != 4:
            continue
        end = match.end()
        rip = base_va + end
        target = _rip_target(rip, disp)
        local = target - base_va
        if not 0 <= local <= len(data) - 32:
            continue
        first = data[local:local + 16]
        for other in SSE_RIP_LOAD.finditer(data, start + 1, min(len(data), start + 96)):
            disp2 = other.group(1) or other.group(2)
            if not disp2 or len(disp2) != 4:
                continue
            end2 = other.end()
            rip2 = base_va + end2
            target2 = _rip_target(rip2, disp2)
            local2 = target2 - base_va
            if not 0 <= local2 <= len(data) - 16:
                continue
            if local2 == local:
                continue
            second = data[local2:local2 + 16]
            if local < local2:
                key = first + second
                key_offset = local
            else:
                key = second + first
                key_offset = local2
            if not raw_key_prefilter(key):
                continue
            yield key, {"offset": offset + key_offset, "encoding": "x64_sse128_pair",
                        "instruction_bytes": end2 - start,
                        "note": "Two RIP-relative SSE loads; order and execution unverified"}


def prefix(data, cursor):
    if cursor >= len(data):
        raise ValueError("Truncated instruction")
    rex = data[cursor] if 0x40 <= data[cursor] <= 0x4f else 0
    return rex, cursor + bool(rex)


def address(data, cursor, rex):
    if cursor >= len(data):
        raise ValueError("Missing ModRM")
    modrm = data[cursor]
    cursor += 1
    mod, base = modrm >> 6, modrm & 7
    if mod == 3 or rex & 2:
        raise ValueError("Register or indexed operand unsupported")
    if base == 4:
        if cursor >= len(data) or data[cursor] & 0xf8 != 0x20:
            raise ValueError("Indexed SIB unsupported")
        base = data[cursor] & 7
        cursor += 1
    if mod == 0 and base == 5:
        raise ValueError("RIP-relative or baseless operand unsupported")
    size = (0, 1, 4)[mod]
    if cursor + size > len(data):
        raise ValueError("Truncated displacement")
    displacement = int.from_bytes(data[cursor:cursor + size], "little", signed=True) if size else 0
    return (base + (8 if rex & 1 else 0), displacement), (modrm >> 3) & 7, cursor + size


def store32(data, cursor):
    rex, cursor = prefix(data, cursor)
    if rex & 8 or cursor >= len(data) or data[cursor] != 0xc7:
        raise ValueError("Not a supported dword immediate store")
    target, extension, cursor = address(data, cursor + 1, rex)
    if extension != 0 or cursor + 4 > len(data):
        raise ValueError("Not MOV or truncated immediate")
    return target, data[cursor:cursor + 4], cursor + 4


def store64(data, cursor):
    rex, cursor = prefix(data, cursor)
    if not rex & 8 or cursor + 9 > len(data) or not 0xb8 <= data[cursor] <= 0xbf:
        raise ValueError("Not MOV register, imm64")
    register = data[cursor] - 0xb8 + (8 if rex & 1 else 0)
    value = data[cursor + 1:cursor + 9]
    rex, cursor = prefix(data, cursor + 9)
    if not rex & 8 or cursor >= len(data) or data[cursor] != 0x89:
        raise ValueError("Not MOV memory, register64")
    target, source, cursor = address(data, cursor + 1, rex)
    if source + (8 if rex & 4 else 0) != register or target[0] == register:
        raise ValueError("Source mismatch or overwritten address register")
    return target, value, cursor


def binary_key_candidates(data, offset=0):
    for match in STARTS.finditer(data):
        start = match.start()
        rex, opcode = prefix(data, start)
        reader, width = (store32, 4) if data[opcode] == 0xc7 else (store64, 8)
        cursor = start
        values = []
        first = None
        try:
            for i in range(32 // width):
                target, value, cursor = reader(data, cursor)
                if first is None:
                    first = target
                if target != (first[0], first[1] + i * width):
                    raise ValueError("Stores do not construct consecutive bytes")
                values.append(value)
        except ValueError:
            continue
        yield b"".join(values), {"offset": offset + start,
                                "encoding": f"x64_consecutive_imm{width * 8}_stores",
                                "instruction_bytes": cursor - start,
                                "note": "Byte-pattern candidate; execution and instruction alignment unverified"}


def faes_register_candidates(data, offset=0):
    """FAES-style register blobs: 16-byte FGuid-like prefix followed by 32 key bytes."""
    limit = len(data) - 48
    if limit < 0:
        return
    for cursor in range(0, limit + 1, FAES_STRIDE):
        guid = data[cursor:cursor + 16]
        if guid == b"\x00" * 16 or guid == b"\xff" * 16:
            continue
        key = data[cursor + 16:cursor + 48]
        if not raw_key_prefilter(key):
            continue
        yield key, {"offset": offset + cursor + 16, "encoding": "faes_guid_prefix_key",
                    "guid_prefix_hex": guid.hex(),
                    "note": "Layout hypothesis; GUID role and lifetime unverified"}
