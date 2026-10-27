from io import BytesIO
from pathlib import Path
import struct
import unittest
from unittest.mock import patch

from tools.inspect_capture import classify_prefix, inspect, pcapng_packets, stream_prefix, tcp_packet


def block(kind, body, endian="<"):
    body += bytes((-len(body)) % 4)
    size = 12 + len(body)
    return struct.pack(endian + "II", kind, size) + body + struct.pack(endian + "I", size)


def section(endian="<"):
    return block(0x0A0D0D0A, struct.pack(endian + "IHHq", 0x1A2B3C4D, 1, 0, -1), endian)


def interface(endian="<", options=b""):
    return block(1, struct.pack(endian + "HHI", 1, 0, 65535) + options, endian)


def enhanced(frame, timestamp=1000000, endian="<"):
    return block(6, struct.pack(endian + "IIIII", 0, timestamp >> 32, timestamp & 0xFFFFFFFF, len(frame), len(frame)) + frame, endian)


def ethernet_tcp(sequence, payload=b"", flags=0x18, reverse=False):
    source, destination = (13700, 50000) if reverse else (50000, 13700)
    tcp = struct.pack("!HHIIBBHHH", source, destination, sequence, 0, 0x50, flags, 65535, 0, 0) + payload
    addresses = bytes([198, 51, 100, 2, 192, 0, 2, 1]) if reverse else bytes([192, 0, 2, 1, 198, 51, 100, 2])
    ipv4 = struct.pack("!BBHHHBBH", 0x45, 0, 20 + len(tcp), 0, 0, 64, 6, 0) + addresses
    return bytes(12) + b"\x08\x00" + ipv4 + tcp


class CaptureTests(unittest.TestCase):
    def test_pcapng_handles_both_endiannesses_and_new_sections(self):
        frame = ethernet_tcp(100)
        data = b"".join(section(e) + interface(e) + enhanced(frame, endian=e) for e in ("<", ">"))
        with patch.object(Path, "open", return_value=BytesIO(data)):
            packets = list(pcapng_packets(Path("fixture.pcapng")))
        self.assertEqual(len(packets), 2)
        self.assertEqual(packets[0]["timestamp"], 1.0)
        self.assertEqual(packets[1]["data"], frame)

    def test_interface_binary_timestamp_resolution(self):
        options = struct.pack("<HH", 9, 1) + bytes([0x80 | 10, 0, 0, 0])
        data = section() + interface(options=options) + enhanced(ethernet_tcp(1), timestamp=1024)
        with patch.object(Path, "open", return_value=BytesIO(data)):
            self.assertEqual(next(pcapng_packets(Path("fixture.pcapng")))["timestamp"], 1)

    def test_bad_block_trailer_and_truncation_fail(self):
        for data in (section()[:-1], section()[:-4] + bytes(4)):
            with patch.object(Path, "open", return_value=BytesIO(data)):
                with self.assertRaises(ValueError):
                    list(pcapng_packets(Path("fixture.pcapng")))

    def test_vlan_and_ip_fragment_handling(self):
        frame = ethernet_tcp(10, b"example")
        vlan = frame[:12] + b"\x81\x00\0\x01\x08\x00" + frame[14:]
        packet, reason = tcp_packet(vlan, 1)
        self.assertIsNone(reason)
        self.assertEqual(packet["payload"], b"example")
        fragmented = bytearray(frame)
        fragmented[20:22] = b"\x20\0"
        self.assertEqual(tcp_packet(fragmented, 1)[1], "fragmented_ipv4")

    def test_snaplen_truncation_is_reported(self):
        packet, reason = tcp_packet(ethernet_tcp(1, b"abcdefgh")[:-3], 1)
        self.assertIsNone(reason)
        self.assertTrue(packet["payload_truncated"])
        self.assertEqual(packet["payload"], b"abcde")

    def test_ipv6_with_hop_by_hop_header(self):
        tcp = ethernet_tcp(100, b"hello")[34:]
        header = struct.pack("!IHBB", 6 << 28, len(tcp) + 8, 0, 64) + bytes(15) + b"\x01" + bytes(15) + b"\x02"
        packet, reason = tcp_packet(header + b"\x06\x00" + bytes(6) + tcp, 101)
        self.assertIsNone(reason)
        self.assertEqual(packet["source"], "::1")
        self.assertEqual(packet["payload"], b"hello")

    def test_out_of_order_segments_and_identical_retransmissions(self):
        payload, diagnostics = stream_prefix([(104, b"def"), (101, b"abc"), (101, b"abc")], 100)
        self.assertEqual(payload, b"abcdef")
        self.assertEqual(diagnostics["conflicting_bytes"], 0)
        self.assertFalse(diagnostics["gap_after_prefix"])

    def test_gap_conflict_and_sequence_wrap_are_explicit(self):
        prefix, diagnostics = stream_prefix([(101, b"ab"), (105, b"f")], 100)
        self.assertEqual(prefix, b"ab")
        self.assertTrue(diagnostics["gap_after_prefix"])
        _, diagnostics = stream_prefix([(101, b"abc"), (102, b"X")], 100)
        self.assertEqual(diagnostics["conflicting_bytes"], 1)
        prefix, _ = stream_prefix([(0xFFFFFFFE, b"ab"), (0, b"cd")], 0xFFFFFFFD)
        self.assertEqual(prefix, b"abcd")

    def test_tls_headers_and_http_candidates(self):
        hello = b"\x01\x00\x00\x22" + bytes(34)
        record = b"\x16\x03\x01" + len(hello).to_bytes(2, "big") + hello
        result = classify_prefix(record)
        self.assertEqual(result["records"][0]["hello_candidate"], "client_hello")
        self.assertTrue(result["records"][0]["hello_complete_in_record"])
        self.assertFalse(classify_prefix(record[:-1])["records"][0]["complete"])
        self.assertEqual(classify_prefix(b"GET /private HTTP/1.1\r\n")["classification"], "http1_prefix_candidate")
        self.assertEqual(classify_prefix(b"\x16\x03")["classification"], "insufficient_contiguous_payload")

    def test_end_to_end_segmented_tls_without_payload_output(self):
        payload = b"\x17\x03\x03\x00\x0cPRIVATE_DATA"
        frames = [ethernet_tcp(100, flags=2), ethernet_tcp(800, flags=0x12, reverse=True),
                  ethernet_tcp(106, payload[5:]), ethernet_tcp(101, payload[:5])]
        data = section() + interface() + b"".join(enhanced(frame) for frame in frames)
        with patch.object(Path, "open", return_value=BytesIO(data)):
            report = inspect(Path("fixture.pcapng"), 13700)
        flow = report["flows"][0]
        self.assertTrue(flow["client_syn_observed"])
        direction = flow["directions"]["client_to_server"]
        self.assertEqual(direction["contiguous_prefix_bytes"], len(payload))
        self.assertEqual(direction["framing"]["classification"], "tls_record_headers_candidate")
        self.assertNotIn("PRIVATE_DATA", str(report))

    def test_reused_tuple_with_new_syn_creates_new_flow(self):
        frames = [ethernet_tcp(100, flags=2), ethernet_tcp(100, flags=2), ethernet_tcp(200, flags=2)]
        data = section() + interface() + b"".join(enhanced(frame) for frame in frames)
        with patch.object(Path, "open", return_value=BytesIO(data)):
            self.assertEqual(len(inspect(Path("fixture.pcapng"), 13700)["flows"]), 2)


if __name__ == "__main__":
    unittest.main()
