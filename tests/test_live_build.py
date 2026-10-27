import os
from pathlib import Path
import sys
import unittest

from tools.inspect_live_build import inspect, markers


class LiveBuildTests(unittest.TestCase):
    def test_retains_only_release_marker_not_adjacent_private_text(self):
        data = b"secret\0++UE5+Release-5.4\0AuthToken=private"
        self.assertEqual(markers(data, 100), [
            {"rva": 107, "encoding": "ascii", "marker": "++UE5+Release-5.4"}])
        wide = ("++UE4+Release-4.27.2\0private").encode("utf-16le")
        self.assertEqual(markers(wide)[0]["marker"], "++UE4+Release-4.27.2")
        self.assertEqual(markers(b"Unreal unrelated text"), [])

    @unittest.skipUnless(os.name == "nt", "Windows process API")
    def test_rejects_different_process_image_before_scanning(self):
        with self.assertRaisesRegex(ValueError, "does not match"):
            inspect(os.getpid(), Path(sys.executable).with_name("AION2.exe"))
