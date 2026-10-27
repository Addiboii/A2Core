from pathlib import Path
import struct
import tempfile
import unittest

from tools.index_iostore import build_index, lookup, read_tables


class IoStoreIndexTests(unittest.TestCase):
    def setUp(self):
        scratch = Path(__file__).resolve().parents[1] / "research/local/test-tmp"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.install = self.root / "install"
        self.install.mkdir()

    def fixture(self, name="fixture", offset=3, length=6, plain=16, encrypted=False):
        header = bytearray(144)
        header[:16] = b"-==--==--==--==-"
        header[16] = 5
        struct.pack_into("<9I", header, 20, 144, 1, 1, 12, 0, 32, 16, 0, 1)
        header[80] = 2 if encrypted else 0
        struct.pack_into("<Q", header, 88, 2**64 - 1)
        chunk = bytes.fromhex("010203040506070800000001")
        locations = offset.to_bytes(5, "big") + length.to_bytes(5, "big")
        block = (4).to_bytes(5, "little") + plain.to_bytes(3, "little") * 2 + b"\0"
        toc = self.install / (name + ".utoc")
        toc.write_bytes(header + chunk + locations + block)
        toc.with_suffix(".ucas").write_bytes(bytes(20))
        return toc, chunk.hex()

    def test_index_resolves_logical_slice_and_keeps_base_patch_copies(self):
        _, identifier = self.fixture("base")
        self.fixture("patch", offset=4, length=5)
        output = self.root / "index.sqlite"
        result = build_index(self.install, output)
        self.assertEqual(result["ids_in_multiple_containers"], 1)
        rows = lookup(output, identifier)
        self.assertEqual([r["logical_offset"] for r in rows], [3, 4])
        self.assertEqual(rows[0]["blocks"][0]["physical_offset"], 4)
        self.assertEqual(rows[0]["blocks"][0]["plain_size"], 16)
        with self.assertRaises(FileExistsError):
            build_index(self.install, output)
        self.assertEqual(len(lookup(output, identifier)), 2)

    def test_rejects_chunk_outside_table_or_inside_padding(self):
        for offset, length, plain in [(16, 1, 16), (7, 3, 8)]:
            path, _ = self.fixture(offset=offset, length=length, plain=plain)
            with self.assertRaises(ValueError):
                read_tables(path)

    def test_rejects_truncated_block_table(self):
        path, _ = self.fixture()
        path.write_bytes(path.read_bytes()[:-1])
        with self.assertRaisesRegex(ValueError, "bounds"):
            read_tables(path)

    def test_checks_encrypted_storage_padding_and_removes_failed_index(self):
        path, _ = self.fixture(length=5, plain=8, encrypted=True)
        path.with_suffix(".ucas").write_bytes(bytes(12))
        output = self.root / "failed.sqlite"
        with self.assertRaisesRegex(ValueError, "UCAS"):
            build_index(self.install, output)
        self.assertFalse(output.exists())

    def test_lookup_does_not_create_missing_database(self):
        import sqlite3
        output = self.root / "missing.sqlite"
        with self.assertRaises(sqlite3.OperationalError):
            lookup(output, "00" * 12)
        self.assertFalse(output.exists())
