"""Dump the AION2 usmap mapping file into a JSON schema for packet structs.

Based on CUE4Parse's UsmapParser / UsmapProperties reference implementation.
"""

import json
import struct
import sys

PROPERTY_TYPES = [
    "ByteProperty", "BoolProperty", "IntProperty", "FloatProperty",
    "ObjectProperty", "NameProperty", "DelegateProperty", "DoubleProperty",
    "ArrayProperty", "StructProperty", "StrProperty", "TextProperty",
    "InterfaceProperty", "MulticastDelegateProperty", "WeakObjectProperty",
    "LazyObjectProperty", "AssetObjectProperty", "SoftObjectProperty",
    "UInt64Property", "UInt32Property", "UInt16Property", "Int64Property",
    "Int16Property", "Int8Property", "MapProperty", "SetProperty",
    "EnumProperty", "FieldPathProperty", "OptionalProperty", "Utf8StrProperty",
    "AnsiStrProperty", "ClassProperty", "MulticastInlineDelegateProperty",
    "SoftClassProperty", "VerseStringProperty", "VerseDynamicProperty",
    "VerseFunctionProperty",
]

INVALID_NAME_INDEX = -1


class Reader:
    def __init__(self, data):
        self.data = data
        self.off = 0

    def u8(self):
        v = self.data[self.off]
        self.off += 1
        return v

    def u16(self):
        v = struct.unpack_from("<H", self.data, self.off)[0]
        self.off += 2
        return v

    def u32(self):
        v = struct.unpack_from("<I", self.data, self.off)[0]
        self.off += 4
        return v

    def i32(self):
        v = struct.unpack_from("<i", self.data, self.off)[0]
        self.off += 4
        return v

    def u64(self):
        v = struct.unpack_from("<Q", self.data, self.off)[0]
        self.off += 8
        return v

    def bytes(self, n):
        v = self.data[self.off:self.off + n]
        self.off += n
        return v


class UsmapParser:
    def __init__(self, path):
        with open(path, "rb") as handle:
            data = handle.read()
        ar = Reader(data)

        magic = ar.u16()
        assert magic == 0x30C4, hex(magic)
        version = ar.u8()
        has_versioning = ar.i32() != 0
        if has_versioning:
            raise NotImplementedError("versioning not supported")
        comp_method = ar.u8()
        comp_size = ar.u32()
        decomp_size = ar.u32()

        payload = ar.bytes(comp_size)
        assert len(payload) == comp_size == decomp_size

        self._parse_payload(payload, version)

    def _read_name(self, ar, lut):
        idx = ar.i32()
        return lut[idx] if idx != INVALID_NAME_INDEX else None

    def _parse_payload(self, data, version):
        ar = Reader(data)

        name_size = ar.u32()
        name_lut = []
        for _ in range(name_size):
            if version >= 2:  # LongFName
                nlen = ar.u16()
            else:
                nlen = ar.u8()
            name_lut.append(ar.bytes(nlen).decode("utf-8", "replace"))

        enum_count = ar.u32()
        enums = {}
        for _ in range(enum_count):
            enum_name = self._read_name(ar, name_lut)
            if version >= 3:  # LargeEnums
                n_names = ar.u16()
            else:
                n_names = ar.u8()
            values = {}
            for j in range(n_names):
                if version >= 4:  # ExplicitEnumValues
                    val = ar.u64()
                    nm = self._read_name(ar, name_lut)
                    values[int(val)] = nm
                else:
                    nm = self._read_name(ar, name_lut)
                    values[j] = nm
            enums[enum_name] = values

        struct_count = ar.u32()
        structs = {}
        for _ in range(struct_count):
            s = self._parse_struct(ar, name_lut)
            structs[s["name"]] = s

        self.names = name_lut
        self.enums = enums
        self.structs = structs

    def _parse_struct(self, ar, name_lut):
        name = self._read_name(ar, name_lut)
        super_type = self._read_name(ar, name_lut)
        property_count = ar.u16()
        serializable_count = ar.u16()
        properties = {}
        for _ in range(serializable_count):
            prop = self._parse_property(ar, name_lut)
            for j in range(prop["array_size"]):
                clone = dict(prop)
                clone["index"] = prop["index"] + j
                properties[clone["index"]] = clone
        return {
            "name": name,
            "super": super_type,
            "property_count": property_count,
            "properties": properties,
        }

    def _parse_property(self, ar, name_lut):
        index = ar.u16()
        array_dim = ar.u8()
        name = self._read_name(ar, name_lut)
        ptype = self._parse_property_type(ar, name_lut)
        return {
            "index": index,
            "array_size": array_dim,
            "name": name,
            "type": ptype,
        }

    def _parse_property_type(self, ar, name_lut):
        type_idx = ar.u8()
        type_name = PROPERTY_TYPES[type_idx] if type_idx < len(PROPERTY_TYPES) else str(type_idx)
        struct_type = None
        inner = None
        value = None
        enum_name = None

        if type_name == "EnumProperty":
            inner = self._parse_property_type(ar, name_lut)
            enum_name = self._read_name(ar, name_lut)
        elif type_name == "StructProperty":
            struct_type = self._read_name(ar, name_lut)
        elif type_name in ("SetProperty", "ArrayProperty", "OptionalProperty"):
            inner = self._parse_property_type(ar, name_lut)
        elif type_name == "MapProperty":
            inner = self._parse_property_type(ar, name_lut)
            value = self._parse_property_type(ar, name_lut)

        return {
            "type": type_name,
            "struct": struct_type,
            "inner": inner,
            "value": value,
            "enum": enum_name,
        }


def fmt_type(t):
    if t["type"] == "StructProperty":
        return "struct " + str(t["struct"])
    if t["type"] == "EnumProperty":
        return "enum " + str(t["enum"])
    if t["type"] == "ArrayProperty":
        return "array<" + fmt_type(t["inner"]) + ">"
    if t["type"] == "OptionalProperty":
        return "optional<" + fmt_type(t["inner"]) + ">"
    if t["type"] == "SetProperty":
        return "set<" + fmt_type(t["inner"]) + ">"
    if t["type"] == "MapProperty":
        return "map<" + fmt_type(t["inner"]) + "," + fmt_type(t["value"]) + ">"
    return t["type"]


def dump_struct(p, name, indent=0, seen=None):
    seen = seen or set()
    if name in seen:
        return
    seen.add(name)
    s = p.structs.get(name)
    if s is None:
        return
    pad = "  " * indent
    print(pad + "struct " + (s["name"] or "?") + (" : " + s["super"] if s["super"] else ""))
    for idx in sorted(s["properties"]):
        pr = s["properties"][idx]
        print(pad + "  [%d] %s: %s" % (pr["index"], pr["name"], fmt_type(pr["type"])))


def main():
    path = sys.argv[1]
    p = UsmapParser(path)
    print("names: %d, enums: %d, structs: %d" % (len(p.names), len(p.enums), len(p.structs)))

    targets = sys.argv[2:]
    if not targets:
        return

    for t in targets:
        print("=" * 70)
        if t in p.structs:
            dump_struct(p, t)
        else:
            print("NOT FOUND: " + t)
            for k in p.structs:
                if t.lower() in k.lower():
                    print("  similar: " + k)


if __name__ == "__main__":
    main()
