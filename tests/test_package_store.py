import struct
import unittest
from tools.inspect_package_store import parse


class PackageStoreTests(unittest.TestCase):
    def fixture(self):
        # Two entries; references are relative to each array-view field.
        buf = struct.pack('<8I', 1, 32, 1, 32, 1, 44, 0, 0)
        buf += b'import01' + b's' * 20 + b'import02'
        return (b'nCoI' + struct.pack('<I', 3) + bytes(8) + struct.pack('<I', 2)
                + b'package1package2' + struct.pack('<I', len(buf)) + buf + b'tail')

    def test_relative_arrays_and_opaque_tail(self):
        report = parse(self.fixture())
        self.assertEqual(report['packages'][1]['imported_package_ids'], [b'import02'.hex()])
        self.assertEqual(report['packages'][0]['candidate_shader_hashes'], [(b's' * 20).hex()])
        self.assertEqual(report['unparsed_tail_bytes'], 4)

    def test_rejects_truncation_and_wrong_version(self):
        with self.assertRaises(ValueError):
            parse(self.fixture()[:60])
        data = bytearray(self.fixture())
        struct.pack_into('<I', data, 4, 2)
        with self.assertRaises(ValueError):
            parse(data)

    def test_rejects_reference_into_entry_table(self):
        data = bytearray(self.fixture())
        struct.pack_into('<I', data, 44, 1)
        with self.assertRaisesRegex(ValueError, 'outside'):
            parse(data)

    def test_duplicate_package_ids(self):
        data = bytearray(self.fixture())
        data[28:36] = data[20:28]
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            parse(data)
