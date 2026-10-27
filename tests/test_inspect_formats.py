"""Small, independently constructed fixtures for binary-format boundary cases."""

from pathlib import Path
import struct
import unittest
import uuid

from tools.inspect_formats import pak_metadata, pe_metadata, utoc_metadata


class FormatTests(unittest.TestCase):
    def setUp(self):
        workspace = Path(__file__).resolve().parents[1]
        scratch = workspace / "research/local/test-tmp"
        scratch.mkdir(parents=True, exist_ok=True)
        # Python 3.13's Windows TemporaryDirectory ACL excludes some sandbox
        # identities. Use an ordinary directory with inherited workspace ACLs.
        self.root = scratch / uuid.uuid4().hex
        self.root.mkdir()
        self.addCleanup(self.cleanup)

    def cleanup(self):
        for path in self.root.iterdir():
            path.unlink()
        self.root.rmdir()

    def write(self, name, data):
        path = self.root / name
        path.write_bytes(data)
        return path

    def test_utoc_perfect_hash_tables_precede_compression_methods(self):
        header = bytearray(144)
        header[:16] = b"-==--==--==--==-"
        header[16] = 5
        struct.pack_into("<9I", header, 20, 144, 2, 1, 12, 1, 32, 65536, 0, 1)
        header[80] = 7
        struct.pack_into("<I", header, 84, 2)
        struct.pack_into("<I", header, 96, 1)
        chunk_ids = bytes(24)
        offsets = bytes(20)
        seeds = struct.pack("<ii", -1, 4)
        overflow = struct.pack("<I", 1)
        blocks = bytes(12)
        methods = b"Oodle" + bytes(27)
        path = self.write("sample.utoc", header + chunk_ids + offsets + seeds + overflow + blocks + methods)
        result = utoc_metadata(path)
        self.assertEqual(result["compression_methods"], ["Oodle"])
        self.assertTrue(result["encrypted"])
        self.assertTrue(result["signed"])
        # A partial table must fail rather than produce a guessed method name.
        path.write_bytes(path.read_bytes()[:-1])
        with self.assertRaisesRegex(ValueError, "Truncated"):
            utoc_metadata(path)

    def test_unknown_utoc_version_is_not_guessed(self):
        header = b"-==--==--==--==-" + bytes([99]) + bytes(127)
        result = utoc_metadata(self.write("unknown.utoc", header))
        self.assertFalse(result["parsed"])
        self.assertEqual(result["version"], 99)

    def pak(self, index_offset=0, index_size=16):
        footer = bytes(16) + b"\x01"
        footer += struct.pack("<IIqq", 0x5A6F12E1, 11, index_offset, index_size)
        footer += bytes(20) + bytes(160)
        return self.write("sample.pak", bytes(16) + footer)

    def test_pak_encryption_and_sidecar_are_separate(self):
        path = self.pak()
        result = pak_metadata(path)
        self.assertEqual(result["version"], 11)
        self.assertTrue(result["index_encrypted"])
        self.assertFalse(result["signature_sidecar_present"])
        path.with_suffix(".sig").write_bytes(b"fixture")
        self.assertTrue(pak_metadata(path)["signature_sidecar_present"])

    def test_pak_index_must_not_overlap_footer(self):
        with self.assertRaisesRegex(ValueError, "bounds"):
            pak_metadata(self.pak(index_size=17))

    def test_non_pe_input_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "DOS signature"):
            pe_metadata(self.write("invalid.exe", b"not an executable"))

    def test_pe64_maps_import_rvas_and_handles_ordinals(self):
        data = bytearray(1024)
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 0x3C, 128)
        data[128:132] = b"PE\0\0"
        struct.pack_into("<HHIIIHH", data, 132, 0x8664, 1, 0, 0, 0, 240, 0x22)
        optional = 152
        struct.pack_into("<H", data, optional, 0x20B)
        struct.pack_into("<Q", data, optional + 24, 0x140000000)
        struct.pack_into("<I", data, optional + 60, 512)
        struct.pack_into("<I", data, optional + 108, 16)
        struct.pack_into("<II", data, optional + 120, 0x1000, 40)
        section = optional + 240
        data[section:section + 8] = b".rdata\0\0"
        struct.pack_into("<IIII", data, section + 8, 512, 0x1000, 512, 512)
        struct.pack_into("<IIIII", data, 0x200, 0x1060, 0, 0, 0x1040, 0x1060)
        data[0x240:0x24B] = b"WS2_32.dll\0"
        struct.pack_into("<QQQ", data, 0x260, 0x1080, (1 << 63) | 7, 0)
        data[0x282:0x28A] = b"connect\0"
        result = pe_metadata(self.write("fixture.exe", data))
        self.assertEqual(result["imports"], [{"dll": "WS2_32.dll", "symbols": ["connect", "ordinal:7"]}])
        self.assertEqual(result["image_base"], "0x140000000")


if __name__ == "__main__":
    unittest.main()
