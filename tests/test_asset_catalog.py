from pathlib import Path
import struct
import tempfile
import unittest

from tools.catalog_assets import NULL, directory_entries, read_directory


def string(value, wide=False):
    if wide:
        return struct.pack("<i", -len(value) - 1) + value.encode("utf-16le") + b"\0\0"
    encoded = value.encode("utf-8") + b"\0"
    return struct.pack("<i", len(encoded)) + encoded


def directory(user=0, sibling=NULL):
    data = string("../../../") + struct.pack("<I", 2)
    data += struct.pack("<4I", NULL, 1, NULL, NULL)
    data += struct.pack("<4I", 0, NULL, sibling, 0)
    data += struct.pack("<I3I", 1, 1, NULL, user)
    data += struct.pack("<I", 2) + string("Characters") + string("SK_Test.uasset", wide=True)
    return data


class AssetCatalogTests(unittest.TestCase):
    def test_resolves_names_to_chunk_ids_without_using_archive_paths_for_writes(self):
        chunk = bytes(11) + b"\x01"
        summary, rows = directory_entries(directory(), [chunk], False)
        self.assertEqual(summary["files"], 1)
        self.assertEqual(rows, [("../../../Characters/SK_Test.uasset", 0, chunk.hex(), 1)])

    def test_rejects_cycles_bad_chunk_ids_and_unexpected_trailing_data(self):
        for data in (directory(sibling=1), directory(user=2), directory() + b"extra"):
            with self.assertRaises(ValueError):
                directory_entries(data, [bytes(12)], False)
        summary, _ = directory_entries(directory() + bytes(7), [bytes(12)], True)
        self.assertEqual(summary["padding_bytes"], 7)
        with self.assertRaises(ValueError):
            directory_entries(directory() + bytes(16), [bytes(12)], True)

    def test_truncated_strings_and_invalid_component_names_fail(self):
        with self.assertRaises(ValueError):
            directory_entries(directory()[:-1], [bytes(12)], False)
        with self.assertRaises(ValueError):
            directory_entries(directory().replace(b"Characters", b"../escape/"), [bytes(12)], False)

    def test_signed_toc_skips_both_signatures_and_per_block_hashes(self):
        payload = directory()
        header = bytearray(144)
        header[:16] = b"-==--==--==--==-"
        header[16] = 5
        struct.pack_into("<9I", header, 20, 144, 1, 1, 12, 0, 32, 65536, len(payload), 1)
        header[80] = 4
        struct.pack_into("<I", header, 84, 1)
        tables = bytes(12 + 10 + 4 + 12)
        signatures = struct.pack("<I", 512) + bytes(512 * 2 + 20)
        scratch = Path(__file__).resolve().parents[1] / "research/local/test-tmp"
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temp:
            path = Path(temp) / "fixture.utoc"
            path.write_bytes(header + tables + signatures + payload)
            summary, rows = read_directory(path, bytes(32), None)
            self.assertEqual(summary["directory_offset"], len(header + tables + signatures))
            self.assertEqual(rows[0][0], "../../../Characters/SK_Test.uasset")
            path.write_bytes(path.read_bytes()[:-1])
            with self.assertRaises(ValueError):
                read_directory(path, bytes(32), None)
