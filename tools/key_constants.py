"""Recognize two narrow x64 constant-construction patterns, not general disassembly.

Byte matches need not be executed instructions. All resulting candidates require
independent cryptographic validation before being called archive keys.
"""

import re

STARTS = re.compile(rb"[\x40-\x4f]?\xc7|[\x48-\x4f][\xb8-\xbf]")


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
