"""Observed AION2 port-13700 framing and selected bootstrap message layouts.

These layouts are derived from captured startup sessions. Opaque message bodies and
cryptographic values are never included in summaries.
"""

from collections import Counter
from dataclasses import dataclass
import hashlib
import math
import struct


class Incomplete(ValueError):
    pass


def varuint(data, offset=0):
    value = 0
    start = offset
    for shift in range(0, 35, 7):
        if offset >= len(data):
            raise Incomplete("Incomplete unsigned varint")
        byte = data[offset]
        offset += 1
        value |= (byte & 127) << shift
        if value > 0xFFFFFFFF:
            raise ValueError("Varint exceeds uint32")
        if not byte & 128:
            if offset - start > 1 and value < 1 << (7 * (offset - start - 1)):
                raise ValueError("Noncanonical varint")
            return value, offset
    raise ValueError("Varint exceeds five bytes")


def encode_varint(value):
    """Encode a canonical unsigned 32-bit ULEB128 value."""
    if not 0 <= value <= 0xFFFFFFFF:
        raise ValueError("Varint exceeds uint32")
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        if value:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return bytes(out)


@dataclass(frozen=True)
class Frame:
    offset: int
    logical_length: int
    prefix_bytes: int
    body: bytes

    @property
    def wire_length(self):
        return self.prefix_bytes + len(self.body)


def split_frames(data, max_frame_size=16 * 1024 * 1024):
    frames = []
    offset = 0
    while offset < len(data):
        start = offset
        try:
            logical, body_start = varuint(data, offset)
        except Incomplete:
            break
        if logical < 6 or logical > max_frame_size:
            raise ValueError("Logical frame length outside supported bounds")
        end = body_start + logical - 4
        if end > len(data):
            break
        frames.append(Frame(start, logical, body_start - start, data[body_start:end]))
        offset = end
        if len(frames) > 16384:
            raise ValueError("Frame count limit exceeded")
    return frames, offset


class Reader:
    def __init__(self, data):
        self.data = data
        self.offset = 0

    def take(self, count):
        if count < 0 or self.offset + count > len(self.data):
            raise Incomplete("Message field exceeds body")
        value = self.data[self.offset:self.offset + count]
        self.offset += count
        return value

    def uint(self, count):
        return int.from_bytes(self.take(count), "little")

    def varuint(self):
        value, self.offset = varuint(self.data, self.offset)
        return value


def lz4_block(data, expected_size, max_size=4 * 1024 * 1024):
    """Decode a raw LZ4 block with exact input/output and expansion bounds."""
    if not 0 <= expected_size <= max_size or len(data) > max_size:
        raise ValueError("LZ4 size exceeds configured bound")
    reader = Reader(data)
    output = bytearray()

    def length(base):
        size = base
        if base == 15:
            while True:
                extra = reader.uint(1)
                size += extra
                if size > expected_size:
                    raise ValueError("LZ4 run exceeds declared output")
                if extra != 255:
                    break
        return size

    while reader.offset < len(data):
        token = reader.uint(1)
        literal_size = length(token >> 4)
        if len(output) + literal_size > expected_size:
            raise ValueError("LZ4 literals exceed declared output")
        output.extend(reader.take(literal_size))
        if reader.offset == len(data):
            break
        distance = reader.uint(2)
        if not 1 <= distance <= len(output):
            raise ValueError("LZ4 match offset outside decoded history")
        match_size = length(token & 15) + 4
        if len(output) + match_size > expected_size:
            raise ValueError("LZ4 match exceeds declared output")
        for _ in range(match_size):
            output.append(output[-distance])
    if len(output) != expected_size:
        raise ValueError("LZ4 output differs from declared size")
    return bytes(output)


def lz4_block_encode(data):
    """Encode a raw LZ4 block with a naive hash-chain matcher.

    Produces canonical raw LZ4 block output: sequences of a token byte
    (high nibble literal length, low nibble match length - 4), optional
    extended length bytes, literal bytes, a two-byte little-endian match
    offset, and optional match-length extension bytes. This is a
    correctness-first encoder; it does not optimize the compression ratio.
    """
    data = bytes(data)
    out = bytearray()
    size = len(data)
    position = 0
    literal_start = 0

    def emit_literal_extension(length):
        remaining = length - 15
        while remaining >= 255:
            out.append(255)
            remaining -= 255
        out.append(remaining)

    while position < size:
        match = None
        if position + 4 <= size:
            candidate = data[position:position + 4]
            search_start = max(0, position - 65535)
            found = data.rfind(candidate, search_start, position)
            if found >= 0:
                match = position - found
        if match is None:
            position += 1
            continue
        literal_length = position - literal_start
        match_length = 4
        while position + match_length < size and \
                data[position + match_length] == data[position - match + match_length]:
            match_length += 1
        token = (min(literal_length, 15) << 4) | min(match_length - 4, 15)
        out.append(token)
        if literal_length >= 15:
            emit_literal_extension(literal_length)
        out.extend(data[literal_start:position])
        out.extend(match.to_bytes(2, "little"))
        if match_length - 4 >= 15:
            emit_literal_extension(match_length - 4)
        position += match_length
        literal_start = position
    if literal_start < size:
        literal_length = size - literal_start
        out.append(min(literal_length, 15) << 4)
        if literal_length >= 15:
            emit_literal_extension(literal_length)
        out.extend(data[literal_start:size])
    return bytes(out)


def compressed_messages(body):
    """Observed 0xffff + uint32 size + raw LZ4, containing ordinary wire frames."""
    reader = Reader(body)
    if reader.uint(2) != 0xFFFF:
        raise ValueError("Not an observed compressed envelope")
    expected = reader.uint(4)
    encoded = reader.take(len(body) - reader.offset)
    decoded = lz4_block(encoded, expected)
    frames, consumed = split_frames(decoded)
    if not frames or consumed != len(decoded):
        raise ValueError("Compressed envelope does not contain complete observed frames")
    tags = [int.from_bytes(frame.body[:2], "little") for frame in frames]
    inner = []
    for frame, tag in zip(frames, tags):
        item = {"offset": frame.offset, "wire_length": frame.wire_length, "header_u16_le": f"0x{tag:04x}"}
        if tag in (0x3615, 0x3634):
            try:
                item["decoded_fields"] = state_message_fields(frame.body)
            except ValueError as error:
                item["layout_error"] = str(error)
        inner.append(item)
    return {"codec": "raw LZ4 block", "compressed_bytes": len(encoded),
            "uncompressed_bytes": len(decoded), "inner_frame_count": len(frames),
            "inner_header_u16_le_counts": {f"0x{tag:04x}": count for tag, count in sorted(Counter(tags).items())},
            "inner_frames": inner,
            "note": "One envelope layer decoded; inner bodies are omitted and tag meanings remain provisional."}


def world_list(body):
    reader = Reader(body)
    if reader.uint(2) != 0x3909:
        raise ValueError("Not an observed world-list message")
    result_field = reader.uint(2)
    count = reader.uint(1)
    if count > 1024:
        raise ValueError("World count exceeds limit")
    worlds = []
    mask = 0
    for index in range(count):
        entry = {"id_candidate": reader.uint(2), "group": reader.uint(1),
                 "repeated_id_candidate": reader.uint(2), "unknown_u16": reader.uint(2)}
        if entry["repeated_id_candidate"] != entry["id_candidate"]:
            raise ValueError(f"World row {index} repeated ID differs from row ID")
        if entry["group"] not in (1, 2):
            raise ValueError(f"World row {index} group differs from observed 1 or 2")
        if entry["unknown_u16"] != 0:
            raise ValueError(f"World row {index} unknown u16 differs from observed zero")
        name_length = reader.uint(1)
        if name_length > 1024:
            raise ValueError("World name exceeds limit")
        entry["name"] = reader.take(name_length).decode("utf-8")
        if index % 4 == 0:
            mask = reader.uint(1)
            if (left := count - index) < 4 and mask >> (2 * left):
                raise ValueError(f"World row {index} group mask has bits past the last row")
        entry["group_mask_bits"] = (mask >> (2 * (index % 4))) & 3
        entry["status"] = reader.uint(1)
        entry["load"] = reader.uint(1)
        constant = reader.take(3)
        if constant != b"\x50\x01\x00":
            raise ValueError(f"World row {index} suffix constant differs from observed 50 01 00")
        worlds.append(entry)
    trailer = reader.take(len(body) - reader.offset)
    if trailer != b"\x01\x01":
        raise ValueError("World-list trailer differs from observed two-byte layout")
    return {"result_field": result_field, "count": count, "worlds": worlds,
            "trailer_hex": trailer.hex(),
            "layout_note": "Post-October-2026 layout: per-row status u8, load u8, and 50 01 00, with one mask byte before every fourth row. The pre-patch Players/Capacity form is not parsed."}


def der_tlv(data, offset):
    if offset + 2 > len(data):
        raise Incomplete("Truncated DER header")
    tag, length = data[offset:offset + 2]
    offset += 2
    if length & 128:
        width = length & 127
        if not 1 <= width <= 4 or offset + width > len(data):
            raise ValueError("Invalid DER length")
        encoded = data[offset:offset + width]
        length = int.from_bytes(encoded, "big")
        offset += width
        if encoded[0] == 0 or length < 128:
            raise ValueError("Noncanonical DER length")
    if offset + length > len(data):
        raise Incomplete("Truncated DER value")
    return tag, data[offset:offset + length], offset + length


def rsa_public_parameters(encoded):
    tag, sequence, end = der_tlv(encoded, 0)
    if tag != 0x30 or end != len(encoded):
        raise ValueError("Expected one DER sequence")
    values = []
    offset = 0
    for _ in range(2):
        tag, value, offset = der_tlv(sequence, offset)
        if tag != 2 or not value or value[0] & 128:
            raise ValueError("Expected positive DER integer")
        if len(value) > 1 and value[0] == 0 and not value[1] & 128:
            raise ValueError("Noncanonical DER integer")
        values.append(int.from_bytes(value, "big"))
    if offset != len(sequence):
        raise ValueError("Expected exactly two RSA public integers")
    modulus, exponent = values
    if not 1024 <= modulus.bit_length() <= 8192 or exponent < 3 or not exponent & 1 or exponent > 0xFFFFFFFF:
        raise ValueError("Unexpected RSA public parameters")
    return {"format": "DER RSAPublicKey", "encoded_bytes": len(encoded),
            "modulus_bits": modulus.bit_length(), "public_exponent": exponent}


def client_hello(body):
    reader = Reader(body)
    tag = reader.uint(2)
    if tag not in (0x3900, 0x3610):
        raise ValueError("Not the observed client hello")
    size = reader.varuint()
    if size > 2048:
        raise ValueError("Public key exceeds expected bounds")
    result = rsa_public_parameters(reader.take(size))
    result["tag_candidate"] = f"0x{tag:04x}"
    result["unparsed_trailer_bytes"] = len(body) - reader.offset
    return result


def server_hello(body):
    reader = Reader(body)
    tag = reader.uint(2)
    if tag not in (0x3901, 0x3611):
        raise ValueError("Not the observed server hello")
    result_field = reader.uint(2)
    parameter = reader.uint(2)
    extra = {"additional_unknown_u16": reader.uint(2)} if tag == 0x3611 else {}
    size = reader.varuint()
    if size > 8192:
        raise ValueError("Server hello blob exceeds bounds")
    reader.take(size)
    return {"tag_candidate": f"0x{tag:04x}", **extra,
            "result_field": result_field, "unknown_u16_parameter": parameter,
            "opaque_blob_bytes": size, "unparsed_trailer_bytes": len(body) - reader.offset,
            "note": "Blob size alone does not establish RSA encryption or its padding scheme."}


def handoff_response(body):
    """Observed IPv4 destination announcement; remaining field meanings provisional."""
    import ipaddress
    reader = Reader(body)
    if reader.uint(2) != 0x390F:
        raise ValueError("Not an observed handoff response")
    result = reader.uint(2)
    unknown = reader.uint(2)
    size = reader.varuint()
    if not 7 <= size <= 15:
        raise ValueError("Unexpected destination address length")
    address = str(ipaddress.IPv4Address(reader.take(size).decode("ascii")))
    port = reader.uint(2)
    if not port or reader.offset != len(body):
        raise ValueError("Unexpected handoff port or trailing data")
    return {"result_field": result, "unknown_u16": unknown,
            "destination_address": address, "destination_port": port}


def state_message_fields(body):
    """Limited observed prefixes; names remain candidates without field schemas."""
    tag = int.from_bytes(body[:2], "little")
    if tag == 0x3615:
        reader = Reader(body)
        reader.uint(2)
        result = reader.uint(2)
        primary, secondary = reader.uint(8), reader.uint(8)
        fields = {"result_field": result, "primary_unix_ms_candidate": primary,
                  "secondary_unix_ms_candidate": secondary,
                  "secondary_minus_primary_ms": secondary - primary,
                  "unparsed_body_bytes": len(body) - reader.offset,
                  "note": "Observed time prefix only; this does not identify the message as a character list."}
        tail = body[20:]
        if len(tail) == 824:
            fields["tail"] = _game_login_tail(tail)
        return fields
    if tag == 0x3634:
        if len(body) not in (57, 61) or body[10] != 0:
            raise ValueError("Entity-shaped message differs from the observed layout")
        values = struct.unpack_from("<5f", body, 11)
        if not all(math.isfinite(value) for value in values):
            raise ValueError("Nonfinite entity-shaped float fields")
        return {"entity_reference_sha256": hashlib.sha256(body[2:10]).hexdigest(),
                "unknown_u8": body[10], "position_xyz_candidate": list(values[:3]),
                "orientation_pair_candidate": list(values[3:]), "unparsed_body_bytes": len(body) - 31,
                "note": "Entity identity, coordinate axes, units, and orientation semantics are unverified; reference bytes are hashed."}
    raise ValueError("Unsupported state message prefix")


def _game_login_tail(tail):
    """Candidate fields inside the observed 824-byte 0x3615 tail (one sample).

    Reports hashed/labeled candidates only; none of these offsets is a
    confirmed schema, and the account/world strings are emitted as hashes.
    """
    if len(tail) != 824:
        raise ValueError("Unsupported 0x3615 tail size")
    fields = {"record_count_candidate": tail[0],
              "class_template_u32_candidate": int.from_bytes(tail[1:5], "little")}
    if 5 + tail[5] <= len(tail):
        name = tail[6:6 + tail[5]]
        if all(32 <= char < 127 for char in name):
            fields["character_name_candidate"] = name.decode("ascii")
        else:
            fields["character_name_candidate_sha256"] = hashlib.sha256(name).hexdigest()
    else:
        fields["name_length_error"] = "exceeds tail"
    def candidate_u64(offset):
        value = int.from_bytes(tail[offset:offset + 8], "little")
        plausible = 1700000000000 <= value <= 1900000000000
        return value if plausible else None
    primary = candidate_u64(57)
    if primary is not None:
        fields["tail_time_ms_candidate_a"] = primary
    secondary = candidate_u64(81)
    if secondary is not None:
        fields["tail_time_ms_candidate_b"] = secondary
    values = struct.unpack_from("<3f", tail, 65)
    if all(math.isfinite(value) for value in values):
        fields["position_xyz_candidate"] = list(values)
    fields["world_id_candidate"] = int.from_bytes(tail[372:376], "little")
    length = tail[767]
    if 768 + length <= len(tail):
        account = tail[768:768 + length]
        fields["account_string_sha256"] = hashlib.sha256(account).hexdigest()
    def four_char_id(offset):
        if offset + 5 > len(tail):
            return None
        id_length = tail[offset]
        value = tail[offset + 1:offset + 1 + id_length]
        if id_length == 4 and all(32 <= char < 127 for char in value):
            return value.decode("ascii")
        return None
    fields["digit_id_candidate_1"] = four_char_id(811)
    fields["digit_id_candidate_2"] = four_char_id(816)
    fields["note"] = ("Single-sample candidate offsets; character/world identity "
                      "strings are hashed. Record widths and field order remain unverified.")
    return fields


def short_server_message(body):
    """Decode fixed fields without claiming verified request names or error meanings."""
    reader = Reader(body)
    tag = reader.uint(2)
    if tag in (0x3903, 0x3603) and len(body) == 20:
        return {"interpretation_candidate": "ping/time response",
                "result_field": reader.uint(2),
                "counter_candidate": reader.uint(8),
                "unix_time_ms_candidate": reader.uint(8)}
    if tag == 0x3600 and len(body) == 10:
        return {"interpretation_candidate": "server clock notification",
                "unix_time_ms_candidate": reader.uint(8)}
    if tag == 0x390D and len(body) == 7:
        return {"interpretation_candidate": "server-selection response",
                "result_field": reader.uint(2), "unknown_u8": reader.uint(1),
                "world_id_candidate": reader.uint(2)}
    raise ValueError("Unsupported short server message layout")


def summarize_stream(data, direction):
    try:
        frames, consumed = split_frames(data)
    except ValueError:
        return None
    if len(frames) < 2 or consumed != len(data):
        return None
    result = {"classification": "observed_aion_varuint_length_candidate", "frame_count": len(frames),
              "consumed_bytes": consumed, "unconsumed_bytes": len(data) - consumed,
              "rule": "wire_length = uint32_uleb128_value - 4 + encoded_prefix_bytes",
              "wire_length_counts": dict(sorted(Counter(frame.wire_length for frame in frames).items())),
              "frames": [{"offset": frame.offset, "wire_length": frame.wire_length,
                          "logical_length": frame.logical_length, "prefix_bytes": frame.prefix_bytes} for frame in frames]}
    if direction == "client_to_server":
        try:
            result["opening_public_key"] = client_hello(frames[0].body)
        except ValueError as error:
            result["opening_layout_note"] = str(error)
    else:
        opcodes = [int.from_bytes(frame.body[:2], "little") for frame in frames]
        result["header_u16_le_counts"] = {f"0x{key:04x}": value for key, value in sorted(Counter(opcodes).items())}
        result["validated_world_list_messages"] = 0
        result["validated_compressed_envelopes"] = 0
        decoded_budget = 16 * 1024 * 1024
        inner_frame_budget = 16384
        for index, (frame, opcode) in enumerate(zip(frames, opcodes)):
            result["frames"][index]["header_u16_le"] = f"0x{opcode:04x}"
            try:
                if opcode == 0xFFFF:
                    if len(frame.body) < 6 or int.from_bytes(frame.body[2:6], "little") > decoded_budget:
                        raise ValueError("Compressed stream exceeds expansion budget")
                    envelope = compressed_messages(frame.body)
                    if envelope["inner_frame_count"] > inner_frame_budget:
                        raise ValueError("Compressed stream exceeds inner frame budget")
                    decoded_budget -= envelope["uncompressed_bytes"]
                    inner_frame_budget -= envelope["inner_frame_count"]
                    result["frames"][index]["compressed_envelope"] = envelope
                    result["validated_compressed_envelopes"] += 1
                if index == 0 and opcode in (0x3901, 0x3611):
                    result["opening_server_hello"] = server_hello(frame.body)
                if opcode == 0x390F:
                    result["frames"][index]["decoded_handoff"] = handoff_response(frame.body)
                if opcode in (0x3903, 0x390D, 0x3600, 0x3603):
                    result["frames"][index]["decoded_fields"] = short_server_message(frame.body)
                if opcode in (0x3615, 0x3634):
                    result["frames"][index]["decoded_fields"] = state_message_fields(frame.body)
                if opcode == 0x3909:
                    parsed = world_list(frame.body)
                    result["validated_world_list_messages"] += 1
                    if "first_world_list" not in result:
                        result["first_world_list"] = parsed
                    else:
                        before = result["first_world_list"]["worlds"]
                        current = parsed["worlds"]
                        stable = [(w["id_candidate"], w["group"], w["name"]) for w in before] == [(w["id_candidate"], w["group"], w["name"]) for w in current]
                        result["frames"][index]["same_world_directory_as_first"] = stable
            except ValueError as error:
                result["frames"][index]["layout_error"] = str(error)
    return result
