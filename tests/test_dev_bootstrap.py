from pathlib import Path
import unittest
from tools.probe_dev_bootstrap import development_config, recovery_directory, ROOT


class DevelopmentBootstrapTests(unittest.TestCase):
    def test_only_development_saved_config_selected(self):
        result = development_config(Path('example'))
        self.assertEqual(result, Path('example/AION2/Saved_AionDev/Config/Windows/GamePlatform.ini'))
        self.assertNotIn('Saved_Steam', str(result))

    def test_recovery_excludes_steam_and_arbitrary_locations(self):
        good = ROOT / 'research/local/dev-bootstrap-example'
        self.assertEqual(recovery_directory(good), good.resolve())
        for bad in [ROOT / 'research/local/local-bootstrap-example',
                    ROOT / 'dev-bootstrap-example',
                    ROOT / 'research/local/dev-bootstrap-example/nested']:
            with self.subTest(path=bad), self.assertRaises(ValueError):
                recovery_directory(bad)
