import struct
import unittest

from tools.inspect_global_names import global_chunk, name_batch, script_objects


def batch():
    # Two hashes, two big-endian headers; UTF-16 length counts code units.
    strings = b"Example" + "\U0001f30d".encode("utf-16le")
    return struct.pack("<IIQ", 2, len(strings), 123) + bytes(16) + b"\0\x07\x80\x02" + strings


def container():
    toc = bytearray(190)
    toc[:16] = b"-==--==--==--==-"
    toc[16] = 5
    struct.pack_into("<5I", toc, 20, 144, 1, 2, 12, 0)
    struct.pack_into("<I", toc, 52, 1)
    toc[80] = 4  # Signed; no signature verification claimed.
    toc[156:161] = (1).to_bytes(5, "big")
    toc[161:166] = (5).to_bytes(5, "big")
    for index, offset in enumerate((0, 8)):
        start = 166 + index * 12
        toc[start:start + 12] = offset.to_bytes(5, "little") + (4).to_bytes(3, "little") * 2 + b"\0"
    return toc, b"ABCDpad!EFGHpadding"


class GlobalNamesTests(unittest.TestCase):
    def test_script_table_resolves_packet_parent_without_guessing_wire_tags(self):
        root, packet, child = [(1 << 62) + index for index in (1, 2, 3)]
        null = 2**64 - 1
        rows = [(0x80000000, 0, root, null, null), (0x80000001, 0, packet, root, null),
                (0x80000002, 0, child, packet, null)]
        encoded = struct.pack("<I", 3) + b"".join(struct.pack("<IIQQQ", *row) for row in rows)
        result = script_objects(encoded, 0, ["/Script/Synthetic", "DevPacketData_Test", "Child"])
        self.assertEqual(result["object_count"], 3)
        self.assertEqual(result["packet_parent_counts"], {"/Script/Synthetic": 1})
        self.assertEqual(result["packet_objects_with_children"], [{"name": "DevPacketData_Test", "child_count": 1}])

    def test_script_table_rejects_missing_names_parents_duplicates_and_trailing_data(self):
        ref, null = (1 << 62) + 1, 2**64 - 1
        valid = struct.pack("<IIQQQ", 0x80000000, 0, ref, null, null)
        malformed = [struct.pack("<I", 1) + valid[:-1], struct.pack("<I", 1) + valid + b"x",
                     struct.pack("<I", 2) + valid * 2,
                     struct.pack("<I", 1) + struct.pack("<IIQQQ", 0x80000001, 0, ref, null, null),
                     struct.pack("<I", 1) + struct.pack("<IIQQQ", 0x80000000, 0, ref, ref + 1, null)]
        for data in malformed:
            with self.assertRaises(ValueError):
                script_objects(data, 0, ["Root"])

    def test_names_use_lengths_and_wide_code_units(self):
        encoded = batch()
        result = name_batch(encoded + b"script objects")
        self.assertEqual(result["names"], ["Example", "\U0001f30d"])
        self.assertEqual(result["bytes_consumed"], len(encoded))

    def test_name_bounds_and_invalid_encoding(self):
        original = batch()
        for size in (0, 15, 32, len(original) - 1):
            with self.assertRaises(ValueError):
                name_batch(original[:size])
        for declared in (10, 12):
            invalid = bytearray(original + b"x")
            struct.pack_into("<I", invalid, 4, declared)
            with self.assertRaises(ValueError):
                name_batch(invalid)
        invalid = bytearray(original)
        invalid[36] = 255
        with self.assertRaises(ValueError):
            name_batch(invalid)

    def test_chunk_reassembles_physical_blocks_and_slices_logical_range(self):
        toc, data = container()
        self.assertEqual(global_chunk(toc, data), b"BCDEF")

    def test_rejects_encryption_compression_and_out_of_bounds_blocks(self):
        toc, data = container()
        for position, value in ((80, 6), (177, 1), (178, 250)):
            invalid = bytearray(toc)
            invalid[position] = value
            with self.assertRaises(ValueError):
                global_chunk(invalid, data)
        with self.assertRaises(ValueError):
            global_chunk(toc[:-1], data)
        with self.assertRaises(ValueError):
            global_chunk(toc, data[:10])


if __name__ == "__main__":
    unittest.main()
