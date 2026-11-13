import struct
import tempfile
import unittest
from pathlib import Path

from tools import usmap_diff
from tools.usmap_dump import PROPERTY_TYPES, UsmapParser

GAME = 'DevPacketData_GameServer_'


def usmap(path, enums, structs):
    """A minimal uncompressed version-4 usmap. enums {name: {value: entry}}; structs [(name, super, [(property, type)])],
    simple property types only."""
    names = []

    def name(text):
        if text is None:
            return -1
        if text not in names:
            names.append(text)
        return names.index(text)

    enum_bytes = b''.join(struct.pack('<iH', name(e), len(values))
                          + b''.join(struct.pack('<Qi', v, name(entry)) for v, entry in values.items())
                          for e, values in enums.items())
    struct_bytes = b''
    for s, parent, props in structs:
        struct_bytes += struct.pack('<iiHH', name(s), name(parent), len(props), len(props))
        for i, (prop, kind) in enumerate(props):
            struct_bytes += struct.pack('<HBiB', i, 1, name(prop), PROPERTY_TYPES.index(kind))
    body = (struct.pack('<I', len(names)) + b''.join(struct.pack('<H', len(n)) + n.encode() for n in names)
            + struct.pack('<I', len(enums)) + enum_bytes + struct.pack('<I', len(structs)) + struct_bytes)
    Path(path).write_bytes(struct.pack('<HBiBII', 0x30C4, 4, 0, 0, len(body), len(body)) + body)


class UsmapDiffTests(unittest.TestCase):
    def test_an_added_message_moves_the_numbers_after_it(self):
        with tempfile.TemporaryDirectory() as folder:
            old, new = Path(folder) / 'old.usmap', Path(folder) / 'new.usmap'
            first = (GAME + 'A_RQ', None, [('_a', 'IntProperty')])
            usmap(old, {'EReason': {0: 'None', 1: 'Use'}}, [first, (GAME + 'B_NT', None, [('_b', 'IntProperty')])])
            usmap(new, {'EReason': {0: 'None', 1: 'Use', 2: 'Recharge'}},
                  [first, (GAME + 'Added_NT', None, []), (GAME + 'B_NT', None, [('_b', 'Int64Property')])])
            result = usmap_diff.compare(UsmapParser(str(old)), UsmapParser(str(new)))
            game = result['families']['DevPacketData_GameServer']
            self.assertEqual((game['added'], game['first_difference'], game['moved']), ([GAME + 'Added_NT'], 1, 1))
            self.assertEqual(list(result['structs_changed']), [GAME + 'B_NT'])   # its field became 64-bit
            self.assertEqual(result['enums_changed']['EReason'], {'added': {'2': 'Recharge'}, 'removed': {}})
            self.assertEqual(usmap_diff.opcode_changes(old, new), {GAME + 'B_NT': ['0x3601', '0x3602']})
            self.assertIn('derived opcodes that change: 1', usmap_diff.summary(result, usmap_diff.opcode_changes(old, new)))

    def test_same_mappings_have_no_difference(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'm.usmap'
            usmap(path, {}, [(GAME + 'A_RQ', None, [('_a', 'IntProperty')])])
            result = usmap_diff.compare(UsmapParser(str(path)), UsmapParser(str(path)))
            self.assertIsNone(result['families']['DevPacketData_GameServer']['first_difference'])
            self.assertEqual(result['structs_changed'], {})


if __name__ == '__main__':
    unittest.main()
