import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class TotpTests(unittest.TestCase):
    def test_core_totp_vectors(self):
        project = ROOT / 'tests/Aion.Core.Tests/Aion.Core.Tests.csproj'
        subprocess.run(['dotnet', 'run', '--project', str(project)], check=True, cwd=ROOT)


if __name__ == '__main__':
    unittest.main()
