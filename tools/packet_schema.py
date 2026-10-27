"""Schema engine for AION2 packet structs derived from the local usmap.

This module turns the usmap mapping into three things:

- An opcode map. The usmap lists the packet structs in registration order and
  that order *is* the opcode numbering. The game-server family starts at
  0x3600 but the handshake (struct index 4) is 0x3610, i.e. there is a
  12-opcode hole at 0x3604..0x360F, then opcodes continue 0x3610, 0x3611, ...
  The login-server family starts at 0x3900 with no hole. This was verified
  against every observed opcode (live loopback sessions and official captures).

- A flattened field list per struct, with super-class fields first
  (BaseDevPacketData is empty; ActionProtocol_RS contributes _key, etc).

- A generic parse/build engine for the ten property types that actually occur
  inside DevPacketData structs: IntProperty (i32 LE), Int64Property (i64 LE),
  FloatProperty (f32), ByteProperty (u8), BoolProperty (u8), EnumProperty (u8,
  all packet enums are ByteProperty-backed), StrProperty (u8 length + UTF-8),
  ArrayProperty (ULEB128 count + elements), StructProperty (recurse), and
  MapProperty (unsupported on the wire; raises NotImplementedError).

Known exception: some IntProperty fields are varint-encoded on the wire
(observed: DevPacketData_GameServer_SelectCharacter_RQ._key). These are listed
in VARINT_FIELDS and handled by the engine.
"""
from __future__ import annotations

import struct
from pathlib import Path

try:
    from .usmap_dump import UsmapParser
except ImportError:
    from usmap_dump import UsmapParser

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_USMAP = ROOT / "research/local/tooling/mappings/AION2-5.3.2.0-2.0.6.0.usmap"

# Full field paths whose IntProperty is ULEB128 on the wire instead of i32 LE.
VARINT_FIELDS = {
    "DevPacketData_GameServer_SelectCharacter_RQ._key",
}

GAME_HOLE = 12  # opcode = 0x3600 + pos (pos < 4), else 0x360C + pos
LOGIN_BASE = 0x3900


class SchemaError(ValueError):
    pass


class WireError(ValueError):
    """A packet body does not match its schema."""


def _game_opcode(pos):
    return 0x3600 + pos if pos < 4 else 0x3600 + GAME_HOLE + pos


def _game_pos(opcode):
    if opcode < 0x3604:
        return opcode - 0x3600
    pos = opcode - 0x3600 - GAME_HOLE
    if pos < 4:
        return None  # opcodes 0x3604..0x360F are a hole; no structs
    return pos


def _field_descriptor(prop_type):
    """Convert a usmap property type dict into a compact (kind, payload) pair."""
    kind = prop_type["type"]
    if kind == "IntProperty":
        return "i32", None
    if kind == "Int64Property":
        return "i64", None
    if kind == "FloatProperty":
        return "f32", None
    if kind == "ByteProperty":
        return "u8", None
    if kind == "BoolProperty":
        return "bool", None
    if kind == "EnumProperty":
        return "enum", prop_type.get("enum")
    if kind == "StrProperty":
        return "str", None
    if kind == "StructProperty":
        return "struct", prop_type["struct"]
    if kind == "ArrayProperty":
        return "array", _field_descriptor(prop_type["inner"])
    if kind == "MapProperty":
        return "map", (_field_descriptor(prop_type["inner"]),
                       _field_descriptor(prop_type["value"]))
    raise SchemaError("Unsupported property type in packet schema: " + kind)


class PacketSchema:
    def __init__(self, usmap_path=None, width_overrides=None):
        self.parser = UsmapParser(str(usmap_path or DEFAULT_USMAP))
        ordered = [n for n in self.parser.structs if n.startswith("DevPacketData_")]
        self.game_structs = [n for n in ordered
                             if n.startswith("DevPacketData_GameServer_")]
        self.login_structs = [n for n in ordered
                              if n.startswith("DevPacketData_LoginServer_")]
        self.common_structs = [n for n in ordered
                               if n.startswith("DevPacketData_Common_")]
        self.width_overrides = dict(width_overrides or {})
        self._fields_cache = {}

    # ---- field flattening -------------------------------------------------
    def fields(self, name):
        """Return the struct's fields as a list of (name, kind, payload).

        Super-class fields come first. VARINT_FIELDS paths are rewritten from
        i32 to varint at their absolute path.
        """
        if name in self._fields_cache:
            return self._fields_cache[name]
        struct = self.parser.structs.get(name)
        if struct is None:
            raise SchemaError("Unknown struct: " + name)
        out = []
        if struct["super"]:
            for fname, kind, payload in self.fields(struct["super"]):
                out.append((fname, kind, payload))
        for index in sorted(struct["properties"]):
            prop = struct["properties"][index]
            kind, payload = _field_descriptor(prop["type"])
            if kind == "i32" and (name + "." + prop["name"]) in VARINT_FIELDS:
                kind = "varint"
            override = self.width_overrides.get(name + "." + prop["name"])
            if override is not None:
                if kind == "i32" and override in (1, 2, 4):
                    kind = {1: "u8", 2: "u16", 4: "i32"}[override]
                elif kind == "enum" and override in (1, 2, 4):
                    kind = {1: "enum", 2: "enum16", 4: "enum32"}[override]
            out.append((prop["name"], kind, payload))
        self._fields_cache[name] = out
        return out

    def all_packet_structs(self):
        """Every DevPacketData struct, in usmap registration order."""
        return list(self.parser.structs)

    # ---- opcode map -------------------------------------------------------
    def game_opcode(self, pos):
        return _game_opcode(pos)

    def opcode_for_struct(self, name):
        """Return (family, opcode) for a packet struct, or None."""
        if name in self.game_structs:
            return "game", _game_opcode(self.game_structs.index(name))
        if name in self.login_structs:
            return "login", LOGIN_BASE + self.login_structs.index(name)
        return None

    def struct_for_opcode(self, family, opcode):
        """Return the struct name for an opcode in a port family, or None."""
        if family == "game":
            pos = _game_pos(opcode)
            if pos is not None and 0 <= pos < len(self.game_structs):
                return self.game_structs[pos]
        elif family == "login":
            pos = opcode - LOGIN_BASE
            if 0 <= pos < len(self.login_structs):
                return self.login_structs[pos]
        return None

    def game_opcode_map(self):
        return {_game_opcode(pos): name
                for pos, name in enumerate(self.game_structs)}

    def login_opcode_map(self):
        return {LOGIN_BASE + pos: name
                for pos, name in enumerate(self.login_structs)}

    # ---- parse engine -----------------------------------------------------
    def read_value(self, reader, kind, payload, struct_path="", field="", _depth=0):
        if _depth > 40:
            raise WireError("Struct nesting too deep")
        if kind == "i32":
            return reader.uint(4)
        if kind == "i64":
            return reader.uint(8)
        if kind == "f32":
            return struct.unpack("<f", reader.take(4))[0]
        if kind == "u16":
            return reader.uint(2)
        if kind in ("u8", "bool"):
            return reader.uint(1)
        if kind == "enum":
            return reader.uint(1)
        if kind == "enum16":
            return reader.uint(2)
        if kind == "enum32":
            return reader.uint(4)
        if kind == "varint":
            return reader.varuint()
        if kind == "str":
            return reader.take(reader.uint(1)).decode("utf-8")
        if kind == "struct":
            return self.read_struct(payload, reader, _depth + 1)
        if kind == "array":
            count = reader.varuint()
            if count > len(reader.data) - reader.offset + 1:
                raise WireError("Array count exceeds remaining body")
            return [self.read_value(reader, payload[0], payload[1], _depth=_depth + 1)
                    for _ in range(count)]
        if kind == "map":
            raise WireError("MapProperty wire encoding is not yet known")
        raise WireError("Unknown field kind: " + kind)

    def read_struct(self, name, reader, _depth=0):
        result = {}
        for fname, kind, payload in self.fields(name):
            result[fname] = self.read_value(reader, kind, payload,
                                            struct_path=name, field=fname,
                                            _depth=_depth)
        return result

    def parse_body(self, name, body, offset=0):
        """Parse a packet body (after the u16 opcode) into a dict.

        Raises WireError when the body does not conform to the schema, or
        Incomplete (from Reader) when truncated.
        """
        try:
            from .aion_protocol import Reader
        except ImportError:
            from aion_protocol import Reader
        reader = Reader(body)
        reader.offset = offset
        result = self.read_struct(name, reader)
        return result, reader.offset

    # ---- build engine -----------------------------------------------------
    def write_value(self, buf, kind, payload, value, _depth=0):
        if _depth > 40:
            raise SchemaError("Struct nesting too deep")
        if kind == "i32":
            buf += int(value).to_bytes(4, "little", signed=True)
        elif kind == "i64":
            buf += int(value).to_bytes(8, "little", signed=True)
        elif kind == "f32":
            buf += struct.pack("<f", float(value))
        elif kind == "u16":
            buf += int(value).to_bytes(2, "little", signed=False)
        elif kind in ("u8", "bool"):
            buf.append(int(value) & 0xFF)
        elif kind == "enum":
            buf.append(int(value) & 0xFF)
        elif kind == "enum16":
            buf += int(value).to_bytes(2, "little", signed=False)
        elif kind == "enum32":
            buf += int(value).to_bytes(4, "little", signed=False)
        elif kind == "varint":
            try:
                from .aion_protocol import encode_varint
            except ImportError:
                from aion_protocol import encode_varint
            buf += encode_varint(int(value))
        elif kind == "str":
            encoded = value.encode("utf-8") if isinstance(value, str) else bytes(value)
            if len(encoded) > 255:
                raise SchemaError("String longer than uint8 FString")
            buf.append(len(encoded))
            buf += encoded
        elif kind == "struct":
            self.write_struct(buf, payload, value, _depth + 1)
        elif kind == "array":
            try:
                from .aion_protocol import encode_varint
            except ImportError:
                from aion_protocol import encode_varint
            items = list(value)
            buf += encode_varint(len(items))
            for item in items:
                self.write_value(buf, payload[0], payload[1], item, _depth + 1)
        elif kind == "map":
            raise SchemaError("MapProperty wire encoding is not yet known")
        else:
            raise SchemaError("Unknown field kind: " + kind)
        return buf

    def write_struct(self, buf, name, values, _depth=0):
        for fname, kind, payload in self.fields(name):
            if fname not in values:
                raise SchemaError("Missing value for field %s.%s" % (name, fname))
            self.write_value(buf, kind, payload, values[fname], _depth)
        return buf

    def build_body(self, name, values):
        """Serialize a packet body (no u16 opcode) from a values dict."""
        return bytes(self.write_struct(bytearray(), name, values))
