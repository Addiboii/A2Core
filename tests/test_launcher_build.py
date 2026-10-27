import struct
import unittest

from tools import launcher_build


def _varint(value):
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        out.append(byte | (0x80 if value else 0))
        if not value:
            return bytes(out)


def _field(number, value):
    if isinstance(value, int):
        return _varint(number << 3) + _varint(value)
    return _varint(number << 3 | 2) + _varint(len(value)) + value


def _manifest(entries, depot=3393111, manifest=1699273097574069541):
    payload = b''.join(_field(1, _field(1, name.encode()) + _field(2, size) + _field(3, flags))
                       for name, size, flags in entries)
    meta = _field(1, depot) + _field(2, manifest)
    return (struct.pack('<II', launcher_build.MANIFEST_PAYLOAD_MAGIC, len(payload)) + payload
            + struct.pack('<II', launcher_build.MANIFEST_METADATA_MAGIC, len(meta)) + meta)


EXE = 'Aion2/Binaries/Win64/AION2.exe'


def _verification(paths):
    return {'status': 'verified', 'errors': [], 'file_count': len(paths),
            'files': [{'path': p, 'bytes': size, 'sha256': 'AB' * 32} for p, size in paths]}


class LauncherBuildTests(unittest.TestCase):
    def test_depot_manifest_lists_files_not_directories(self):
        depot = launcher_build.depot_manifest(_manifest([('Aion2', 0, 0x40), ('Aion2\\Binaries\\Win64\\AION2.exe', 10, 0)]))
        self.assertEqual(depot, {'depotId': 3393111, 'manifestId': '1699273097574069541', 'files': {EXE: 10}})

    def test_runtime_files_are_left_out(self):
        depot = launcher_build.depot_manifest(_manifest([('Aion2\\Binaries\\Win64\\AION2.exe', 10, 0)]))
        result = launcher_build.build(_verification([(EXE, 10), ('Aion2/InstallLog.txt', 20),
                                                     ('Aion2/Binaries/Win64/NCGuard/bb64.dll', 30)]), depot)
        self.assertEqual([f['path'] for f in result['files']], [EXE])
        self.assertEqual((result['fileCount'], result['totalBytes']), (1, 10))

    def test_copy_must_hold_every_depot_file_at_its_size(self):
        for entries in ([('Aion2\\Binaries\\Win64\\AION2.exe', 10, 0), ('Engine\\x.dll', 5, 0)],
                        [('Aion2\\Binaries\\Win64\\AION2.exe', 11, 0)]):
            with self.assertRaises(ValueError):
                launcher_build.build(_verification([(EXE, 10)]), launcher_build.depot_manifest(_manifest(entries)))

    def test_other_manifest_rejected(self):
        depot = launcher_build.depot_manifest(_manifest([('Aion2\\Binaries\\Win64\\AION2.exe', 10, 0)], manifest=1))
        with self.assertRaises(ValueError):
            launcher_build.build(_verification([(EXE, 10)]), depot)


if __name__ == '__main__':
    unittest.main()
