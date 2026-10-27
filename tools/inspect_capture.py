"""Inspect TCP framing in PCAPNG without emitting application payloads."""

import argparse
from collections import Counter
from datetime import datetime, timezone
import ipaddress
import json
from pathlib import Path
import struct

try:
    from .aion_protocol import summarize_stream
except ImportError:
    from aion_protocol import summarize_stream

PREFIX_LIMIT = 262144
SEGMENT_BUDGET = 524288
SEGMENT_COUNT_LIMIT = 16384


def pcapng_packets(path):
    """Yield enhanced packets from bounded PCAPNG blocks and section interfaces."""
    endian = None
    interfaces = []
    with path.open("rb") as stream:
        while True:
            header = stream.read(8)
            if not header:
                return
            if len(header) != 8:
                raise ValueError("Truncated PCAPNG block header")
            is_section = header[:4] == b"\x0a\x0d\x0d\x0a"
            prefix = b""
            if is_section:
                prefix = stream.read(4)
                if prefix == b"\x4d\x3c\x2b\x1a":
                    endian = "<"
                elif prefix == b"\x1a\x2b\x3c\x4d":
                    endian = ">"
                else:
                    raise ValueError("Invalid PCAPNG byte-order magic")
                interfaces = []
            if endian is None:
                raise ValueError("Expected a PCAPNG section header")
            kind, length = struct.unpack(endian + "II", header)
            if length < (28 if is_section else 12) or length % 4 or length > 16 * 1024 * 1024:
                raise ValueError("Invalid or excessively large PCAPNG block")
            rest = stream.read(length - 8 - len(prefix))
            if len(rest) != length - 8 - len(prefix):
                raise ValueError("Truncated PCAPNG block")
            if struct.unpack(endian + "I", rest[-4:])[0] != length:
                raise ValueError("PCAPNG block length trailer mismatch")
            body = prefix + rest[:-4]
            if is_section:
                if struct.unpack_from(endian + "HH", body, 4) != (1, 0):
                    raise ValueError("Unsupported PCAPNG section version")
            elif kind == 1:
                if len(body) < 8:
                    raise ValueError("Truncated PCAPNG interface")
                linktype, _, snaplen = struct.unpack_from(endian + "HHI", body)
                interface = {"linktype": linktype, "resolution": 1e-6, "offset": 0, "snaplen": snaplen}
                offset = 8
                while offset < len(body):
                    if offset + 4 > len(body):
                        raise ValueError("Truncated interface option")
                    code, size = struct.unpack_from(endian + "HH", body, offset)
                    offset += 4
                    if offset + size > len(body):
                        raise ValueError("Invalid interface option length")
                    value = body[offset:offset + size]
                    offset += (size + 3) & ~3
                    if code == 0:
                        break
                    if code == 9 and size == 1:
                        exponent = value[0]
                        interface["resolution"] = 2 ** -(exponent & 127) if exponent & 128 else 10 ** -exponent
                    if code == 14 and size == 8:
                        interface["offset"] = struct.unpack(endian + "q", value)[0]
                interfaces.append(interface)
            elif kind == 6:
                if len(body) < 20:
                    raise ValueError("Truncated enhanced packet")
                index, high, low, captured, original = struct.unpack_from(endian + "IIIII", body)
                if index >= len(interfaces) or captured > original or 20 + ((captured + 3) & ~3) > len(body):
                    raise ValueError("Invalid enhanced-packet bounds or interface")
                interface = interfaces[index]
                timestamp = ((high << 32) | low) * interface["resolution"] + interface["offset"]
                yield {"timestamp": timestamp, "linktype": interface["linktype"],
                       "captured_length": captured, "original_length": original,
                       "data": body[20:20 + captured]}
            elif kind in (2, 3):
                raise ValueError("Legacy/simple packet blocks are unsupported; use enhanced PCAPNG packets")


def tcp_packet(frame, linktype):
    """Return a TCP segment or a reason for skipping an unsupported packet."""
    if linktype == 1:
        if len(frame) < 14:
            return None, "short_ethernet"
        protocol = int.from_bytes(frame[12:14], "big")
        offset = 14
        while protocol in (0x8100, 0x88A8):
            if len(frame) < offset + 4:
                return None, "short_vlan"
            protocol = int.from_bytes(frame[offset + 2:offset + 4], "big")
            offset += 4
        if protocol not in (0x0800, 0x86DD):
            return None, "non_ip_ethernet"
        packet = frame[offset:]
    elif linktype in (101, 228, 229):
        packet = frame
    else:
        return None, f"unsupported_linktype_{linktype}"
    if not packet:
        return None, "empty_ip"
    version = packet[0] >> 4
    if version == 4:
        if len(packet) < 20:
            return None, "short_ipv4"
        offset = (packet[0] & 15) * 4
        total = int.from_bytes(packet[2:4], "big")
        if offset < 20 or total < offset or len(packet) < offset:
            return None, "invalid_ipv4_lengths"
        if int.from_bytes(packet[6:8], "big") & 0x3FFF:
            return None, "fragmented_ipv4"
        protocol = packet[9]
        source = str(ipaddress.ip_address(packet[12:16]))
        destination = str(ipaddress.ip_address(packet[16:20]))
    elif version == 6:
        if len(packet) < 40:
            return None, "short_ipv6"
        total = 40 + int.from_bytes(packet[4:6], "big")
        if total == 40:
            return None, "ipv6_jumbogram_or_empty"
        source = str(ipaddress.ip_address(packet[8:24]))
        destination = str(ipaddress.ip_address(packet[24:40]))
        protocol, offset = packet[6], 40
        for _ in range(16):
            if protocol not in (0, 43, 60, 51):
                break
            if offset + 2 > min(len(packet), total):
                return None, "short_ipv6_extension"
            size = (packet[offset + 1] + 2) * 4 if protocol == 51 else (packet[offset + 1] + 1) * 8
            protocol, offset = packet[offset], offset + size
        if protocol == 44:
            return None, "fragmented_ipv6"
    else:
        return None, "unsupported_ip_version"
    if protocol != 6:
        return None, "non_tcp"
    packet = packet[:total]
    if offset + 20 > len(packet):
        return None, "short_tcp"
    source_port, destination_port, sequence, acknowledgement = struct.unpack_from("!HHII", packet, offset)
    header_size = (packet[offset + 12] >> 4) * 4
    if header_size < 20 or offset + header_size > len(packet):
        return None, "invalid_tcp_header"
    return {"source": source, "destination": destination, "source_port": source_port,
            "destination_port": destination_port, "sequence": sequence, "acknowledgement": acknowledgement,
            "flags": packet[offset + 13], "payload": packet[offset + header_size:],
            "payload_truncated": len(packet) < total}, None


def serial_delta(sequence, reference):
    return (sequence - reference + 2 ** 31) % 2 ** 32 - 2 ** 31


def stream_prefix(segments, syn_sequence=None):
    if not segments:
        return b"", {"conflicting_bytes": 0, "gap_after_prefix": False}
    reference = (syn_sequence + 1) % 2 ** 32 if syn_sequence is not None else segments[0][0]
    positions = [(serial_delta(sequence, reference), payload) for sequence, payload in segments]
    base = 0 if syn_sequence is not None else min(position for position, _ in positions)
    data, present = bytearray(PREFIX_LIMIT), bytearray(PREFIX_LIMIT)
    conflicts = 0
    for position, payload in positions:
        start = position - base
        for index in range(max(0, -start), min(len(payload), PREFIX_LIMIT - start)):
            target = start + index
            if present[target] and data[target] != payload[index]:
                conflicts += 1
            data[target], present[target] = payload[index], 1
    end = present.find(0)
    if end < 0:
        end = PREFIX_LIMIT
    return bytes(data[:end]), {"conflicting_bytes": conflicts, "gap_after_prefix": any(present[end:]),
                              "prefix_limit_reached": end == PREFIX_LIMIT}


def classify_prefix(data):
    """Recognize plausible record boundaries, not decrypted message semantics."""
    records = []
    if len(data) < 5:
        return {"classification": "insufficient_contiguous_payload" if data else "no_contiguous_payload"}
    offset = 0
    names = {20: "change_cipher_spec", 21: "alert", 22: "handshake", 23: "application_data", 24: "heartbeat"}
    while len(records) < 16 and offset + 5 <= len(data):
        kind, major, minor = data[offset:offset + 3]
        size = int.from_bytes(data[offset + 3:offset + 5], "big")
        if kind not in names or major != 3 or minor > 4 or not 0 < size <= 18432:
            break
        record = {"offset": offset, "type": names[kind], "legacy_record_version": f"{major}.{minor}",
                  "declared_length": size, "complete": offset + 5 + size <= len(data)}
        if kind == 22 and record["complete"] and size >= 4:
            handshake = data[offset + 5]
            handshake_size = int.from_bytes(data[offset + 6:offset + 9], "big")
            if handshake in (1, 2) and handshake_size >= 34:
                record["hello_candidate"] = "client_hello" if handshake == 1 else "server_hello"
                record["hello_complete_in_record"] = handshake_size + 4 <= size
        records.append(record)
        if not record["complete"]:
            break
        offset += 5 + size
    if records:
        return {"classification": "tls_record_headers_candidate", "records": records,
                "note": "Record versions do not identify the negotiated TLS version; payloads are not decrypted."}
    for start in (b"GET ", b"POST ", b"HEAD ", b"HTTP/1.", b"PUT ", b"OPTIONS "):
        if data.startswith(start):
            return {"classification": "http1_prefix_candidate"}
    return {"classification": "unrecognized" if data else "no_contiguous_payload"}


def frame_capture_times(segments, syn_sequence, frames):
    """Earliest captured availability of each frame and all preceding bytes.

    Segments contain (sequence, payload, capture_timestamp). Retransmissions do
    not move availability later; out-of-order arrivals cannot make later frames
    available before the missing prefix arrives. This is not application timing.
    """
    if not frames or not segments:
        return []
    size = frames[-1]["offset"] + frames[-1]["wire_length"]
    if size > PREFIX_LIMIT:
        raise ValueError("Frame timing exceeds prefix bound")
    reference = (syn_sequence + 1) % 2 ** 32 if syn_sequence is not None else segments[0][0]
    base = 0 if syn_sequence is not None else min(serial_delta(s, reference) for s, _, _ in segments)
    arrival = [None] * size
    for sequence, payload, timestamp in segments:
        start = serial_delta(sequence, reference) - base
        for index in range(max(0, -start), min(len(payload), size - start)):
            target = start + index
            if arrival[target] is None or timestamp < arrival[target]:
                arrival[target] = timestamp
    if any(value is None for value in arrival):
        raise ValueError("Cannot time frames across missing bytes")
    result = []
    latest = float("-inf")
    cursor = 0
    for frame in frames:
        end = frame["offset"] + frame["wire_length"]
        latest = max(latest, max(arrival[cursor:end]))
        result.append(datetime.fromtimestamp(latest, timezone.utc).isoformat())
        cursor = end
    return result


def inspect(path, port, stream_observer=None):
    flows, active, skipped = [], {}, Counter()
    packet_count = 0
    for packet in pcapng_packets(path):
        packet_count += 1
        segment, reason = tcp_packet(packet["data"], packet["linktype"])
        if reason:
            skipped[reason] += 1
            continue
        if segment["destination_port"] == port:
            key = (segment["source"], segment["source_port"], segment["destination"], port)
            direction = "client_to_server"
        elif segment["source_port"] == port:
            key = (segment["destination"], segment["destination_port"], segment["source"], port)
            direction = "server_to_client"
        else:
            skipped["other_port"] += 1
            continue
        initial_syn = direction == "client_to_server" and segment["flags"] & 2 and not segment["flags"] & 16
        flow = active.get(key)
        if flow is None or (initial_syn and flow.get("initial_syn_sequence") != segment["sequence"]):
            if len(flows) >= 128:
                raise ValueError("Flow limit exceeded; use a narrower capture")
            flow = {"client_address": key[0], "client_port": key[1], "server_address": key[2], "server_port": key[3],
                    "first_packet_utc": datetime.fromtimestamp(packet["timestamp"], timezone.utc).isoformat(),
                    "initial_syn_sequence": segment["sequence"] if initial_syn else None, "directions": {}}
            active[key] = flow
            flows.append(flow)
        flow["last_packet_utc"] = datetime.fromtimestamp(packet["timestamp"], timezone.utc).isoformat()
        stream = flow["directions"].setdefault(direction, {"packets": 0, "payload_bytes_including_retransmissions": 0,
            "syn_sequence": None, "fin_seen": False, "rst_seen": False, "truncated_payload_packets": 0,
            "retained_bytes": 0, "retention_limit_reached": False, "segments": []})
        stream["packets"] += 1
        if segment["flags"] & 2:
            stream["syn_sequence"] = segment["sequence"]
        stream["fin_seen"] |= bool(segment["flags"] & 1)
        stream["rst_seen"] |= bool(segment["flags"] & 4)
        stream["truncated_payload_packets"] += segment["payload_truncated"]
        payload = segment["payload"]
        stream["payload_bytes_including_retransmissions"] += len(payload)
        if payload:
            remaining = SEGMENT_BUDGET - stream["retained_bytes"]
            if remaining > 0 and len(stream["segments"]) < SEGMENT_COUNT_LIMIT:
                retained = payload[:remaining]
                sequence = (segment["sequence"] + bool(segment["flags"] & 2)) % 2 ** 32
                stream["segments"].append((sequence, retained, packet["timestamp"]))
                stream["retained_bytes"] += len(retained)
            else:
                stream["retention_limit_reached"] = True
            if len(payload) > remaining:
                stream["retention_limit_reached"] = True
    for flow in flows:
        flow["client_syn_observed"] = flow.pop("initial_syn_sequence") is not None
        for direction, stream in flow["directions"].items():
            segments = stream.pop("segments")
            syn_sequence = stream.pop("syn_sequence")
            prefix, diagnostics = stream_prefix([(s, p) for s, p, _ in segments], syn_sequence)
            stream["syn_observed"] = syn_sequence is not None
            stream["contiguous_prefix_bytes"] = len(prefix)
            stream.update(diagnostics)
            stream["framing"] = classify_prefix(prefix) if not diagnostics["conflicting_bytes"] else {"classification": "conflicting_retransmissions"}
            if not diagnostics["conflicting_bytes"]:
                application = summarize_stream(prefix, direction)
                if application:
                    times = frame_capture_times(segments, syn_sequence, application["frames"])
                    for frame, timestamp in zip(application["frames"], times):
                        frame["available_in_capture_utc"] = timestamp
                    stream["application_framing"] = application
            if not stream["syn_observed"]:
                stream["boundary_note"] = "SYN not captured; prefix may begin in the middle of an application record."
            if stream_observer is not None:
                # Optional in-process analysis hook. Raw prefixes are never
                # inserted into the returned JSON report.
                stream_observer(flow, direction, prefix)
    return {"schema_version": 1, "source": str(path), "target_port": port,
            "packet_count": packet_count, "skipped_packets": dict(skipped), "flows": flows,
            "limitations": ["Only retained contiguous TCP prefixes are classified.",
                "TLS/HTTP matches are framing candidates, not verified service identities.",
                "No opaque payload bytes, credentials, or cryptographic key values are emitted; decoded directory fields and public-key parameters may be reported.",
                "Frame availability uses captured TCP bytes, not application processing times; compressed inner frames inherit their envelope's availability.",
                "Packet checksums are not validated; IP fragments are skipped."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("--port", type=int, default=13700)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if not 1 <= args.port <= 65535:
        parser.error("Port must be between 1 and 65535")
    try:
        report = inspect(args.input, args.port)
    except (OSError, ValueError, struct.error) as error:
        parser.exit(1, f"Capture inspection failed: {error}\n")
    rendered = json.dumps(report, indent=2) + "\n"
    if args.output:
        if args.output.resolve() == args.input.resolve():
            parser.error("Output must differ from input")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8")
    print(rendered)


if __name__ == "__main__":
    main()
