import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WWWROOT = ROOT / 'server/Aion.Host/wwwroot'


@unittest.skipUnless(shutil.which('node'), 'node is not installed')
class WebScriptSyntaxTests(unittest.TestCase):
    """A syntax error in a page module stops the whole page (the dashboard sign-in did nothing)."""

    def test_page_scripts_parse_as_modules(self):
        with tempfile.TemporaryDirectory() as tmp:
            for script in sorted(WWWROOT.glob('*.js')):
                copy = Path(tmp) / (script.stem + '.mjs')
                copy.write_bytes(script.read_bytes())
                result = subprocess.run(['node', '--check', str(copy)], capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, f'{script.name}: {result.stderr.strip()}')


if __name__ == '__main__':
    unittest.main()
