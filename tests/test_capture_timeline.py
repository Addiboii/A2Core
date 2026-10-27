"""capture_timeline: both directions of a game-port stream, LZ4 envelopes, first-arrival times, gaps."""
from pathlib import Path
import json
import struct
import tempfile
import unittest

from tools import capture_timeline
from tools.aion_protocol import encode_varint, lz4_block_encode
from tests.test_inspect_capture import block, enhanced, interface, section


def frame(body):
    """A wire frame: varint logical length (body + 4) then the body (opcode u16 + fields)."""
    return encode_varint(len(body) + 4) + body


def envelope(*bodies):
    inner = b''.join(frame(b) for b in bodies)
    return frame(b'\xff\xff' + struct.pack('<I', len(inner)) + lz4_block_encode(inner))


def tcp(sequence, payload=b'', flags=0x18, server_to_client=False, port=13328):
    source, destination = (port, 50000) if server_to_client else (50000, port)
    header = struct.pack('!HHIIBBHHH', source, destination, sequence, 0, 0x50, flags, 65535, 0, 0)
    addresses = bytes([198, 51, 100, 2, 192, 0, 2, 1]) if server_to_client else bytes([192, 0, 2, 1, 198, 51, 100, 2])
    ipv4 = struct.pack('!BBHHHBBH', 0x45, 0, 20 + len(header) + len(payload), 0, 0, 64, 6, 0) + addresses
    return bytes(12) + b'\x08\x00' + ipv4 + header + payload


def pcapng(packets):
    """packets: (frame, timestamp in seconds)."""
    return section() + interface() + b''.join(enhanced(f, timestamp=int(t * 1_000_000)) for f, t in packets)


class TimelineTests(unittest.TestCase):
    def decode(self, packets):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'capture.pcapng'
            path.write_bytes(pcapng(packets))
            summary, out = capture_timeline.decode(path, body_hex=16)
            lines = [json.loads(line) for line in (out / 'timeline.jsonl').read_text(encoding='utf-8').splitlines()]
            large = {p.name: p.read_bytes() for p in (out / 'large').iterdir()}
        return summary, lines, large

    def test_both_directions_envelopes_times_and_order(self):
        hello = b'\x15\x36' + b'\x01\x02\x03'                     # c2s 0x3615
        points = b'\x00\x8d' + bytes(range(10))                   # s2c 0x8d00
        appear = b'\x41\x36' + bytes(40)                          # s2c 0x3641, above the 16-byte hex limit
        server_stream = envelope(points, appear) + frame(points)
        packets = [
            (tcp(1000, flags=0x02), 10.0),                        # client SYN
            (tcp(5000, flags=0x12, server_to_client=True), 10.0),  # server SYN/ACK
            (tcp(1001, frame(hello)), 11.0),
            (tcp(5001, server_stream[:7], server_to_client=True), 12.0),   # the envelope split over two segments
            (tcp(5001 + 7, server_stream[7:], server_to_client=True), 13.5),
            (tcp(5001, server_stream[:7], server_to_client=True), 14.0),   # a retransmission: does not move times later
            (tcp(1001 + len(frame(hello)), frame(b'\x34\xe3' + b'\x00' * 9), flags=0x19), 15.0),   # c2s 0xe334, FIN
        ]
        summary, lines, large = self.decode(packets)
        self.assertEqual(summary['messages'], 5)
        self.assertEqual([(l['dir'], l['op'], l['ms']) for l in lines],
                         [('c2s', '0x3615', 0), ('s2c', '0x8d00', 2500), ('s2c', '0x3641', 2500), ('s2c', '0x8d00', 2500), ('c2s', '0xe334', 4000)])
        self.assertEqual(lines[0]['t'], '1970-01-01T00:00:11.000+00:00')
        self.assertEqual(lines[1]['body'], points.hex())
        self.assertEqual(lines[1]['name'], 'CharPointUpdate_NT')
        self.assertNotIn('body', lines[2])
        self.assertEqual(large[Path(lines[2]['file']).name], appear)
        flow = summary['flows'][0]
        self.assertEqual(flow['client_port'], 50000)
        s2c = flow['directions']['s2c']
        self.assertEqual((s2c['syn_observed'], s2c['conflicting_bytes'], s2c['gap_at'], s2c['trailing_bytes'], s2c['messages']), (True, 0, None, 0, 3))
        self.assertEqual(s2c['opcodes'], {'0x3641': 1, '0x8d00': 2})
        self.assertEqual(flow['directions']['c2s']['opcodes'], {'0x3615': 1, '0xe334': 1})

    def test_gap_and_trailing_partial_frame_are_reported(self):
        a, b = frame(b'\x00\x8d' + bytes(4)), frame(b'\x42\x36' + bytes(5))
        packets = [
            (tcp(5000, a, server_to_client=True), 1.0),
            (tcp(5000 + len(a) + 3, b, server_to_client=True), 2.0),   # 3 bytes missing before b
        ]
        summary, lines, _ = self.decode(packets)
        s2c = summary['flows'][0]['directions']['s2c']
        self.assertEqual([l['op'] for l in lines], ['0x8d00'])
        self.assertEqual((s2c['syn_observed'], s2c['gap_at'], s2c['bytes_after_gap']), (False, len(a), 3 + len(b)))
        packets = [(tcp(5000, a + b[:4], server_to_client=True), 1.0)]
        summary, lines, _ = self.decode(packets)
        self.assertEqual(summary['flows'][0]['directions']['s2c']['trailing_bytes'], 4)
        self.assertEqual(len(lines), 1)

    def test_wifi_frames_are_decapsulated(self):
        inner = tcp(5000, frame(b'\x00\x8d' + bytes(4)), server_to_client=True)[14:]   # the IPv4 packet
        wifi = struct.pack('<H', 0x0008) + bytes(22) + b'\xaa\xaa\x03\x00\x00\x00\x08\x00' + inner
        data = section() + block(1, struct.pack('<HHI', 105, 0, 65535)) + enhanced(wifi, timestamp=1_000_000)
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'capture.pcapng'
            path.write_bytes(data)
            summary, _ = capture_timeline.decode(path)
        self.assertEqual(summary['messages'], 1)
        self.assertEqual(summary['skipped_packets'], {})


if __name__ == '__main__':
    unittest.main()
