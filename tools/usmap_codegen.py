"""Generate tools/packet_structs.py from the local usmap mapping.

The generated module exposes a parse()/build() function per DevPacketData
struct plus a struct-ordered opcode table, so packet handling code never
hand-writes byte layouts again. Regenerate with:

    python tools/usmap_codegen.py

Output path and usmap input are fixed to keep the tooling deterministic;
pass --check to verify the file on disk is up to date (CI-friendly).
"""
import argparse
from pathlib import Path

try:
    from .packet_schema import PacketSchema
except ImportError:
    from packet_schema import PacketSchema

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "tools/packet_structs.py"

HEADER = '''# Generated packet struct codec. Do not edit by hand.
#
# Regenerate with: python tools/usmap_codegen.py
#
# Each DevPacketData struct has a parse_<Name>(body) function that returns
# (parsed_dict, consumed_offset) and a build_<Name>(values) function that
# returns the serialized body. Bodies do not include the u16 opcode; wrap
# with the port framing helpers from tools.aion_protocol as usual.
#
# OPCODE_BY_STRUCT maps struct name -> (family, opcode); GAME_OPCODES and
# LOGIN_OPCODES map opcode -> struct name.
'''


def _python_type(kind, payload):
    if kind == "struct":
        return "dict"
    if kind == "array":
        return "list"
    if kind == "map":
        return "dict"
    if kind == "str":
        return "str"
    if kind == "f32":
        return "float"
    if kind in ("i32", "i64", "u8", "u16", "bool", "enum", "enum16",
                "enum32", "varint"):
        return "int"
    return "object"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true",
                        help="Fail if the generated file is out of date")
    args = parser.parse_args(argv)

    schema = PacketSchema()
    ordered = schema.all_packet_structs()
    lines = [HEADER, "", "from __future__ import annotations", ""]
    lines.append("try:")
    lines.append("    from .packet_schema import PacketSchema")
    lines.append("    from .aion_protocol import Reader")
    lines.append("except ImportError:")
    lines.append("    from packet_schema import PacketSchema")
    lines.append("    from aion_protocol import Reader")
    lines.append("")
    lines.append("_SCHEMA = PacketSchema()")
    lines.append("")

    # Opcode tables.
    lines.append("# opcode -> struct name")
    lines.append("GAME_OPCODES = {")
    for opcode, name in sorted(schema.game_opcode_map().items()):
        lines.append("    0x%04X: \"%s\"," % (opcode, name))
    lines.append("}")
    lines.append("")
    lines.append("LOGIN_OPCODES = {")
    for opcode, name in sorted(schema.login_opcode_map().items()):
        lines.append("    0x%04X: \"%s\"," % (opcode, name))
    lines.append("}")
    lines.append("")
    lines.append("# struct name -> (family, opcode)")
    lines.append("OPCODE_BY_STRUCT = {")
    for name in ordered:
        binding = schema.opcode_for_struct(name)
        if binding is None:
            continue
        family, opcode = binding
        lines.append("    \"%s\": (\"%s\", 0x%04X)," % (name, family, opcode))
    lines.append("}")
    lines.append("")

    for name in ordered:
        if not name.startswith("DevPacketData_"):
            continue
        fields = schema.fields(name)
        short = name[len("DevPacketData_"):].replace("_", "")
        doc_fields = ", ".join("%s: %s" % (f, _python_type(k, p))
                               for f, k, p in fields)
        lines.append("")
        lines.append("def parse_%s(body):" % short)
        lines.append("    %s" % repr("%s - Fields: %s" % (name, doc_fields)))
        lines.append("    return _SCHEMA.parse_body(\"%s\", body)" % name)
        lines.append("")
        lines.append("")
        lines.append("def build_%s(values):" % short)
        lines.append("    %s" % repr(name))
        lines.append("    return _SCHEMA.build_body(\"%s\", values)" % name)

    content = "\n".join(lines) + "\n"
    if args.check:
        if OUTPUT.exists() and OUTPUT.read_text(encoding="utf-8") == content:
            print("packet_structs.py is up to date")
            return 0
        print("packet_structs.py is out of date; run python tools/usmap_codegen.py")
        return 1
    OUTPUT.write_text(content, encoding="utf-8")
    print("wrote %s (%d packet structs)" % (OUTPUT.relative_to(ROOT), len(ordered)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
