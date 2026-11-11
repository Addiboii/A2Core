import tempfile
import unittest
from pathlib import Path

from tools import list_markers


class ListMarkersTests(unittest.TestCase):
    def test_markers_are_listed_by_kind_with_file_and_line(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / 'server/Aion.World/Sample.cs'
            source.parent.mkdir(parents=True)
            source.write_text('class Sample {\n'
                              '    /// <summary>OURS: listings last 7 days.</summary>\n'
                              '    int days = 7; // CANDIDATE until a recording shows it\n'
                              '    int observed = 1; // OBSERVED 2026-10-19\n'
                              '}\n', encoding='utf-8')
            (root / 'server/Aion.World/obj').mkdir()
            (root / 'server/Aion.World/obj/Generated.cs').write_text('// OURS', encoding='utf-8')
            found = list_markers.markers(root)
        self.assertEqual(found['OURS'], [('server/Aion.World/Sample.cs', 2, 'OURS: listings last 7 days.')])
        self.assertEqual(found['CANDIDATE'], [('server/Aion.World/Sample.cs', 3, 'CANDIDATE until a recording shows it')])
        page = list_markers.markdown(found)
        self.assertIn('**CANDIDATE** (1)', page)
        self.assertIn('- line 2: OURS: listings last 7 days.', page)

    def test_the_real_tree_has_markers(self):
        found = list_markers.markers()
        self.assertGreater(len(found['CANDIDATE']), 50)
        self.assertGreater(len(found['OURS']), 50)


if __name__ == '__main__':
    unittest.main()
