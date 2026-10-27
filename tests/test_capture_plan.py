import json
import os
from pathlib import Path
import subprocess
import unittest


@unittest.skipUnless(os.name == "nt", "Windows capture script")
class CapturePlanTests(unittest.TestCase):
    def run_plan(self, address, *extra):
        script = Path(__file__).resolve().parents[1] / "tools/capture_port13700.ps1"
        return subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive",
                               "-ExecutionPolicy", "Bypass", "-File", str(script),
                               "-PlanOnly", "-ServerAddress", address, *extra], capture_output=True, text=True)

    def test_plan_owns_a_unique_session_and_scopes_capture_to_server(self):
        first = self.run_plan("198.51.100.25")
        second = self.run_plan("198.51.100.25")
        self.assertEqual(first.returncode, 0, first.stderr)
        self.assertEqual(second.returncode, 0, second.stderr)
        plan, other = json.loads(first.stdout), json.loads(second.stdout)
        self.assertNotEqual(plan["session_name"], other["session_name"])
        self.assertEqual(plan["stop_arguments"], ["trace", "stop", "sessionname=" + plan["session_name"]])
        self.assertIn("sessionname=" + plan["session_name"], plan["start_arguments"])
        for expected in ("Ethernet.Type=IPv4", "Protocol=TCP", "IPv4.Address=198.51.100.25", "persistent=no"):
            self.assertIn(expected, plan["start_arguments"])
        self.assertEqual(plan["analysis_port"], 13700)
        self.assertFalse(plan["capture_complete"])
        trace_file = next(arg.split("=", 1)[1] for arg in plan["start_arguments"] if arg.startswith("tracefile="))
        self.assertFalse(Path(trace_file).parent.exists(), "Preview must not create capture files")

    def test_loopback_and_ipv6_are_rejected_before_capture(self):
        for address in ("127.0.0.1", "::1", "198.51.100.25,127.0.0.1"):
            result = self.run_plan(address)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("ServerAddress must contain non-loopback IPv4", result.stderr)

    def test_multiple_observed_peers_use_one_list_filter(self):
        result = self.run_plan("198.51.100.26,198.51.100.25,198.51.100.26")
        self.assertEqual(result.returncode, 0, result.stderr)
        plan = json.loads(result.stdout)
        filters = [arg for arg in plan["start_arguments"] if arg.startswith("IPv4.Address=")]
        self.assertEqual(filters, ["IPv4.Address=(198.51.100.25,198.51.100.26)"])
        self.assertEqual(plan["server_addresses"], ["198.51.100.25", "198.51.100.26"])
        self.assertEqual(plan["suggested_analysis_ports"], [13700, 13328])

    def test_movement_cue_plan_has_baselines_and_stops_and_does_not_start_capture(self):
        result = self.run_plan("198.51.100.25", "-MovementCues", "-DurationSeconds", "120")
        self.assertEqual(result.returncode, 0, result.stderr)
        plan = json.loads(result.stdout)
        schedule = plan["movement_cue_schedule"]
        self.assertEqual([(s["at_seconds"], s["action"]) for s in schedule],
                         [(0, "stand_still"), (20, "walk_forward"), (23, "stop"),
                          (43, "walk_backward"), (46, "stop"), (66, "sequence_complete_stay_still")])
        self.assertIsNone(plan["started_utc"])
        self.assertIn("actual key presses", plan["movement_cue_note"])
        trace = next(s.split("=", 1)[1] for s in plan["start_arguments"] if s.startswith("tracefile="))
        self.assertFalse(Path(trace).parent.exists())
        too_short = self.run_plan("198.51.100.25", "-MovementCues", "-DurationSeconds", "30")
        self.assertNotEqual(too_short.returncode, 0)
        self.assertIn("at least 80 seconds", too_short.stderr)


if __name__ == "__main__":
    unittest.main()
