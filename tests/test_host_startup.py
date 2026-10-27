import unittest
from tools.probe_host_startup import launch_arguments
from tools.inspect_bootstrap import argument_summary


class HostStartupArgumentsTests(unittest.TestCase):
    def test_preserves_opaque_argument_and_changes_only_suffix(self):
        source = '-steam -lp:U0VDUkVU -saveddirsuffix=Steam -NoDebugExecBindings'
        result = launch_arguments(source)
        self.assertEqual(result, ['-steam', '-lp:U0VDUkVU', '-saveddirsuffix=AionDev', '-NoDebugExecBindings'])
        self.assertNotIn('U0VDUkVU', str(argument_summary(' '.join(result))))

    def test_rejects_unknown_duplicate_and_incomplete_switches(self):
        for source in ['-steam -steam -saveddirsuffix=Steam -lp:QQ==',
                       '-steam -lp:QQ== -saveddirsuffix=Steam -Other',
                       '-steam -lp:QQ==',
                       '-steam -lp:QQ==&calc -saveddirsuffix=Steam -NoDebugExecBindings']:
            with self.subTest(source=source), self.assertRaises(ValueError):
                launch_arguments(source)
