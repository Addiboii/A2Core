import json
from pathlib import Path
import struct
import unittest
from unittest.mock import patch

from tools.inspect_bootstrap import BinaryKV, argument_summary, collect, process_history, read_appinfo


class BootstrapTests(unittest.TestCase):
    def test_split_library_reads_manifest_and_dependencies_from_library(self):
        steam, library = Path("steam-client"), Path("game-library")
        for override in (None, library):
            expected = override or steam
            with patch.object(Path, "read_text", autospec=True, return_value='"installdir" "AION2"') as read, \
                    patch("tools.inspect_bootstrap.read_appinfo", return_value={"appinfo": {}}) as cache, \
                    patch("tools.inspect_bootstrap.dependencies", return_value=[]) as deps, \
                    patch("tools.inspect_bootstrap.process_history", return_value=[]) as history:
                result = collect(steam, 3393110, library=override)
            read.assert_called_once_with(expected / "steamapps/appmanifest_3393110.acf", encoding="utf-8-sig")
            cache.assert_called_once_with(steam / "appcache/appinfo.vdf", 3393110)
            history.assert_called_once_with(steam / "logs/gameprocess_log.txt", 3393110)
            deps.assert_called_once_with(expected / "steamapps/common/AION2")
            self.assertEqual(result["source_paths"]["library"], str(expected))

    def test_arguments_never_include_opaque_values(self):
        result = argument_summary('-steam -lp:PRIVATE_TEST_PAYLOAD -saveddirsuffix=Steam -AuthToken=SECRET -NoDebugExecBindings')
        self.assertTrue(result["opaque_lp_argument_present"])
        self.assertEqual(result["saved_dir_suffix"], "Steam")
        self.assertIn("AuthToken", result["switches"])
        self.assertNotIn("SECRET", json.dumps(result))
        self.assertNotIn("PRIVATE_TEST_PAYLOAD", json.dumps(result))

    def test_history_filters_other_apps_and_omits_raw_command(self):
        lines = '\n'.join([
            '[2026-01-01 12:00:00] AppID 123 adding PID 99 as a tracked process',
            '[2026-01-01 12:00:01] AppID 3393110 adding PID 42 as a tracked process "C:\\AION2.exe -lp:PRIVATE -steam"',
            '[2026-01-01 12:00:05] AppID 3393110 no longer tracking PID 42, exit code 1',
            '[2026-01-01 12:00:06] Remove 3393110 from running list',
        ])
        with patch.object(Path, "read_text", return_value=lines):
            result = process_history(Path("fixture.log"), 3393110)
        self.assertEqual(len(result), 3)
        self.assertTrue(result[0]["aion2_executable_named"])
        self.assertEqual(result[1]["exit_code"], 1)
        self.assertNotIn("PRIVATE", json.dumps(result))
        self.assertNotIn("99", json.dumps(result))

    def appinfo_fixture(self):
        # One appinfo object, containing an integer appid and a string name.
        body = b"\x00" + struct.pack("<I", 0)
        body += b"\x02" + struct.pack("<Ii", 1, 3393110)
        body += b"\x01" + struct.pack("<I", 2) + b"Fixture Game\0\x08\x08"
        record = struct.pack("<II", 3393110, 60 + len(body)) + bytes(60) + body
        string_offset = 16 + len(record) + 4
        header = struct.pack("<IIQ", 0x07564429, 1, string_offset)
        strings = struct.pack("<I", 3) + b"appinfo\0appid\0name\0"
        return header + record + bytes(4) + strings

    def test_appinfo_v29_record_and_shared_string_table(self):
        with patch.object(Path, "read_bytes", return_value=self.appinfo_fixture()):
            result = read_appinfo(Path("fixture.vdf"), 3393110)
        self.assertEqual(result["appinfo"], {"appid": 3393110, "name": "Fixture Game"})

    def test_appinfo_rejects_record_overlapping_string_table(self):
        data = bytearray(self.appinfo_fixture())
        struct.pack_into("<I", data, 20, len(data))
        with patch.object(Path, "read_bytes", return_value=data):
            with self.assertRaisesRegex(ValueError, "record bounds"):
                read_appinfo(Path("fixture.vdf"), 3393110)

    def test_binary_kv_rejects_bad_keys_and_truncated_values(self):
        with self.assertRaisesRegex(ValueError, "string-table index"):
            BinaryKV(b"\x01\xff\xff\xff\xff", ["key"]).object()
        with self.assertRaisesRegex(ValueError, "Truncated"):
            BinaryKV(b"\x02\0\0\0\0\x01", ["key"]).object()


if __name__ == "__main__":
    unittest.main()
