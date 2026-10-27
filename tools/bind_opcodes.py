"""Learn field wire widths by parsing observed plaintext bodies against schemas.

The usmap gives property *types* but not wire *widths* for every field:
IntProperty is usually i32 LE but is observed as u16 in some structs
(DevPacketData_Common_StatType._stat_type), u16 for hand-coded _result fields
(Ping_RS, Handshake_RS), and ULEB128 in SelectCharacter_RQ._key. Rather than
hand-maintaining that table, this tool solves it from plaintext samples.

Input: plaintext server->client bodies. The official captures under
research/local/capture-*/server-13328-prefix.bin are plaintext after the
handshake; our own loopback runs save followup-<port>.bin whose bodies decrypt
with injected-key-<port>.bin. Only lengths, widths and enum/bool/float
plausibility are scored; string values are never stored.

Output: a JSON ledger of {opcode -> struct, family, sample_count, widths} where
widths maps absolute field path (Struct.field) to a width kind:
u8/u16/i32/i64/f32/bool/enum/str/varint. The packet_schema width_overrides
mechanism consumes the same keys.
"""
import argparse
import json
import math
import struct
from collections import defaultdict
from pathlib import Path

try:
    from .packet_schema import PacketSchema
    from .aion_protocol import Incomplete, encode_varint, split_frames
except ImportError:
    from packet_schema import PacketSchema
    from aion_protocol import Incomplete, encode_varint, split_frames

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / "research/local"
DEFAULT_OUTPUT = LOCAL / "tooling/opcodes.json"

FIXED = {"i32": 4, "u16": 2, "u8": 1, "i64": 8, "f32": 4}
CANDIDATE_CAP = 32

_WIDTH_RANK = {1: 1, 2: 2, 4: 3, 8: 4, "varint": 5}


def _candidate_key(cand):
    """Sort by score desc, then lexicographic width ascending (narrow first).

    Narrower widths are preferred because the observed special cases (_result,
    _key, _char_key, _stat_type) are all narrower than the i32 default. Only
    applies when byte consumption is identical, so it never changes framing.
    """
    score, widths, _parsed, _end = cand
    ranks = tuple(_WIDTH_RANK.get(w, 3) for _p, _k, w in widths)
    return (-score, ranks)


def _try_varint(data, offset):
    value, after = 0, offset
    for shift in range(0, 35, 7):
        if after >= len(data):
            raise Incomplete("truncated varint")
        byte = data[after]
        after += 1
        value |= (byte & 127) << shift
        if value > 0xFFFFFFFF:
            raise ValueError("varint overflow")
        if not byte & 128:
            if after - offset > 1 and value < 1 << (7 * (after - offset - 1)):
                raise ValueError("noncanonical varint")
            return value, after
    raise ValueError("varint too long")


def _field_options(kind):
    if kind == "i32":
        return [4, 2, 1, 8, "varint"]
    if kind == "enum":
        return [1, 2, 4]
    if kind == "f32":
        return [4]
    if kind == "i64":
        return [8]
    if kind in ("u8", "bool"):
        return [1]
    if kind == "u16":
        return [2]
    if kind == "varint":
        return ["varint"]
    return []


def _decode(kind, width, raw):
    if width == 1:
        v = raw[0]
        if kind == "bool":
            return v, 10.0 if v in (0, 1) else 2.0
        if kind == "enum":
            return v, 5.0
        return v, 3.0
    if width == 2:
        v = int.from_bytes(raw, "little")
        if kind == "i32":
            return v, 6.0 if v <= 0xFFFF else 1.0
        if kind == "enum":
            return v, 4.0
        return v, 3.0
    if width == 4:
        if kind == "f32":
            f = struct.unpack("<f", raw)[0]
            return f, 8.0 if math.isfinite(f) else -math.inf
        v = int.from_bytes(raw, "little")
        if kind == "i32":
            # Plausible int32s are small; huge values usually mean a u16 (or
            # two u16s) were wrongly swallowed as one i32.
            return v, 7.0 if v < 1 << 24 else 1.0
        if kind == "enum":
            return v, 3.0
        return v, 3.0
    if width == 8:
        return int.from_bytes(raw, "little"), 5.0
    return None, -math.inf


def solve_prefix(schema, name, body, off):
    """Parse struct at body[off:] as a prefix.

    Returns a list of (score, widths, parsed, end_offset) candidates, where
    widths maps absolute field path -> (kind, width) and parsed holds decoded
    values (strings, nested dicts, and list-of-items). Best score first.
    """
    fields = schema.fields(name)
    memo = {}

    def solve_fields(i, off, widths, parsed):
        if i == len(fields):
            return [(0.0, widths, parsed, off)]
        key = (i, off)
        if key in memo:
            return memo[key]
        fname, kind, payload = fields[i]
        path = name + "." + fname
        cands = []

        if kind == "str":
            if off + 1 <= len(body):
                n = body[off]
                if off + 1 + n <= len(body):
                    raw = body[off + 1:off + 1 + n]
                    try:
                        text = raw.decode("utf-8")
                        printable = sum(32 <= c < 127 for c in raw)
                        score = 8.0 if printable == len(raw) else printable * 0.4
                        for rest in solve_fields(i + 1, off + 1 + n,
                                                 widths, parsed + [text]):
                            cands.append((score + rest[0], rest[1], rest[2], rest[3]))
                    except UnicodeDecodeError:
                        pass
        elif kind == "struct":
            for inner in solve_prefix(schema, payload, body, off):
                for rest in solve_fields(i + 1, inner[3], widths + inner[1],
                                         parsed + [inner[2]]):
                    cands.append((inner[0] + rest[0], rest[1], rest[2], rest[3]))
        elif kind == "array":
            if off < len(body):
                try:
                    count, after = _try_varint(body, off)
                except (Incomplete, ValueError):
                    count = None
                if count is not None:
                    inner_kind, inner_payload = payload
                    for arr in solve_array(inner_kind, inner_payload, count, after):
                        arr_score, items, arr_off, arr_widths = arr
                        for rest in solve_fields(i + 1, arr_off, widths + arr_widths,
                                                 parsed + [items]):
                            cands.append((arr_score + rest[0], rest[1], rest[2], rest[3]))
        else:
            for width in _field_options(kind):
                if width == "varint":
                    try:
                        value, after = _try_varint(body, off)
                    except (Incomplete, ValueError):
                        continue
                    score = 6.0
                else:
                    if off + width > len(body):
                        continue
                    value, score = _decode(kind, width, body[off:off + width])
                    after = off + width
                    if value is None or score < 0:
                        continue
                for rest in solve_fields(i + 1, after,
                                         widths + [(path, kind, width)],
                                         parsed + [value]):
                    cands.append((score + rest[0], rest[1], rest[2], rest[3]))

        cands.sort(key=_candidate_key)
        cands = cands[:CANDIDATE_CAP]
        memo[key] = cands
        return cands

    def solve_array(inner_kind, inner_payload, count, off):
        """Solve `count` array elements at body[off:].

        Returns list of (score, items, end_offset, widths). Items are decoded
        values; widths records the field widths used inside each element.
        """
        if inner_kind == "struct":
            return _solve_struct_array(inner_payload, count, off)
        results = [(0.0, [], off, [])]
        for _ in range(count):
            next_results = []
            for score, items, cur, _w in results:
                if inner_kind == "varint":
                    try:
                        value, after = _try_varint(body, cur)
                    except (Incomplete, ValueError):
                        continue
                    next_results.append((score + 6.0, items + [value], after, []))
                elif inner_kind in FIXED:
                    width = FIXED[inner_kind]
                    if cur + width <= len(body):
                        value, sc = _decode(inner_kind, width, body[cur:cur + width])
                        if value is not None and sc >= 0:
                            next_results.append((score + sc, items + [value], cur + width, []))
                elif inner_kind in ("u8", "bool", "enum"):
                    if cur + 1 <= len(body):
                        v = body[cur]
                        sc = 10.0 if inner_kind == "bool" and v in (0, 1) else 3.0
                        next_results.append((score + sc, items + [v], cur + 1, []))
                else:
                    continue
            results = next_results[:CANDIDATE_CAP]
            if not results:
                break
        return results

    def _solve_struct_array(inner_name, count, off):
        """Solve `count` fixed-width struct elements. All elements must parse
        to the same width, which makes array-of-struct detection exact even
        when a single element would be ambiguous (e.g. StatType u16+u16 vs
        u16+i32)."""
        if count == 0:
            return [(0.0, [], off, [])]
        out = []
        for sol in solve_prefix(schema, inner_name, body, off):
            width = sol[3] - off
            if width <= 0:
                continue
            total = sol[0]
            items = [sol[2]]
            cur = sol[3]
            ok = True
            for _ in range(count - 1):
                match = None
                for sub in solve_prefix(schema, inner_name, body, cur):
                    if sub[3] == cur + width:
                        match = sub
                        break
                if match is None:
                    ok = False
                    break
                total += match[0]
                items.append(match[2])
                cur = match[3]
            if ok:
                out.append((total, items, cur, sol[1]))
        return sorted(out, key=lambda c: -c[0])[:CANDIDATE_CAP]

    return solve_fields(0, off, [], [])


def collect_samples():
    samples = defaultdict(list)
    for capture in LOCAL.glob("capture-*/server-13328-prefix.bin"):
        try:
            data = capture.read_bytes()
            frames, _ = split_frames(data)
        except (ValueError, OSError):
            continue
        for frame in frames:
            if len(frame.body) >= 2:
                samples[int.from_bytes(frame.body[:2], "little")].append(frame.body[2:])
    return samples


def width_kind(kind, width):
    if kind == "f32":
        return "f32"
    if kind == "i64" or width == 8:
        return "i64"
    if width == "varint":
        return "varint"
    if width == 2:
        return "u16"
    if width == 1:
        return "u8"
    return "i32"


def learn(schema, samples, family):
    ledger = {}
    for opcode, bodies in sorted(samples.items()):
        struct = schema.struct_for_opcode(family, opcode)
        if struct is None:
            ledger[opcode] = {"struct": None, "sample_count": len(bodies)}
            continue
        solved = None
        for body in bodies:
            for score, widths, parsed, end in solve_prefix(schema, struct, body, 0):
                if end == len(body):
                    solved = (score, widths)
                    break
            if solved is not None:
                break
        entry = {"struct": struct, "sample_count": len(bodies), "opcode": opcode}
        if solved is None:
            entry["widths"] = None
        else:
            score, widths = solved
            entry["score"] = round(score, 2)
            widths_map = {}
            for path, kind, width in widths:
                widths_map[path] = width_kind(kind, width)
            entry["widths"] = widths_map
        ledger[opcode] = entry
    return ledger


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--family", choices=("game", "login"), default="game")
    args = parser.parse_args(argv)

    schema = PacketSchema()
    samples = collect_samples()
    ledger = learn(schema, samples, args.family)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(ledger, indent=2, sort_keys=True) + "\n",
                           encoding="utf-8")
    solved = sum(1 for e in ledger.values() if e.get("widths"))
    print("wrote %s: %d opcodes, %d with solved widths, %d without a known struct"
          % (args.output.relative_to(ROOT), len(ledger), solved,
             sum(1 for e in ledger.values() if e["struct"] is None)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
