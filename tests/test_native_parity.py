import json
import unittest

from tools import native_parity


class NativeParityFixtureTests(unittest.TestCase):
    def test_fixture_matches_the_python_builders(self):
        # tests/Aion.Native.Checks compares the .NET builders against this file; regenerate it
        # with `python tools/native_parity.py` after a deliberate builder change.
        stored = json.loads(native_parity.OUTPUT.read_text(encoding='utf-8'))
        self.assertEqual(stored, native_parity.cases())


if __name__ == '__main__':
    unittest.main()
