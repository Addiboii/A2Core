import json
from pathlib import Path
import unittest
from unittest.mock import patch

from tools.summarize_startup import summarize


class SummaryTests(unittest.TestCase):
    def test_repeated_rows_and_loopback_are_not_extra_remote_peers(self):
        remote = {"protocol": "TCP", "pid": 12, "local_endpoint": "192.0.2.1:50000",
                  "remote_endpoint": "198.51.100.1:13700", "state": "ESTABLISHED"}
        loopback = {**remote, "remote_endpoint": "[::1]:50001"}
        process = {"pid": 12, "name": "AION2", "started_utc": "2026-01-01T00:00:00Z",
                   "match_basis": "process_name_only", "installed_modules": []}
        sample = {"kind": "sample", "collected_utc": "2026-01-01T00:00:01Z",
                  "processes": [process], "connections": [remote, remote, loopback],
                  "issues": ["Path unavailable"]}
        records = [{"kind": "session"}, sample,
                   {**sample, "collected_utc": "2026-01-01T00:00:03Z"}, {"kind": "summary"}]
        with patch.object(Path, "read_text", return_value="\n".join(map(json.dumps, records))):
            report = summarize(Path("fixture.jsonl"))
        self.assertEqual(report["non_loopback_tcp_peers"], ["198.51.100.1:13700"])
        self.assertEqual(report["endpoint_states"][0]["sample_observations"], 2)
        self.assertEqual(report["processes"][0]["match_bases"], ["process_name_only"])
        self.assertEqual(report["issues"], ["Path unavailable"])
        self.assertTrue(report["complete"])


if __name__ == "__main__":
    unittest.main()
