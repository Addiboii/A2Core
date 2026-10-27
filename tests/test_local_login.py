import json
import socket
import tempfile
import threading
import unittest
import uuid
from pathlib import Path

from tools.local_login import LoginError, frame, login, normalize_name, read_frame, serve, world_entry


def write_manifest(root, name, packages, placements, spawns):
    (root / name).write_text(json.dumps({
        "packages": list(range(packages)),
        "placements": list(range(placements)),
        "spawns": list(range(spawns)),
    }), encoding="utf-8")


class LocalLoginTests(unittest.TestCase):
    def test_name_is_collapsed_and_bounded(self):
        self.assertEqual(normalize_name("  Potato   Farmer "), "Potato Farmer")
        with self.assertRaises(LoginError):
            normalize_name("   ")
        with self.assertRaises(LoginError):
            normalize_name("x" * 33)

    def test_world_entry_counts_both_manifests_and_reports_a_missing_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            write_manifest(root, "sven-region-a-manifest.json", 2, 1, 3)
            entry = world_entry(root)
        self.assertEqual(entry["name"], "Potatoe World")
        self.assertEqual(entry["packages"], 2)
        self.assertEqual(entry["placements"], 1)
        self.assertEqual(entry["spawns"], 3)
        self.assertEqual(entry["missing_manifests"], [
            "research/local/world/sven-region-b-manifest.json",
        ])
        self.assertFalse(entry["simulation"])

    def test_login_returns_a_session_for_the_recovered_preview(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            write_manifest(root, "sven-region-a-manifest.json", 1, 2, 3)
            write_manifest(root, "sven-region-b-manifest.json", 4, 5, 6)
            ready = threading.Event()
            ports = []

            def on_ready(port):
                ports.append(port)
                ready.set()

            worker = threading.Thread(target=serve, args=(root, 0, on_ready))
            worker.start()
            self.assertTrue(ready.wait(2))
            reply = login(ports[0], "  Scout  ")
            worker.join(2)
        self.assertFalse(worker.is_alive())
        uuid.UUID(reply["session_id"])
        self.assertEqual(reply["op"], "welcome")
        self.assertEqual(reply["display_name"], "Scout")
        world = reply["worlds"][0]
        self.assertEqual(world["id"], "potatoe-world")
        self.assertEqual(world["packages"], 5)
        self.assertEqual(world["placements"], 7)
        self.assertEqual(world["spawns"], 9)
        self.assertFalse(world["simulation"])

    def test_rejected_request_returns_an_error_and_no_session(self):
        left, right = socket.socketpair()
        with left, right:
            right.sendall(frame({"op": "directory"}))
            with self.assertRaises(LoginError):
                from tools.local_login import handle
                handle(left, Path("."))
            error = read_frame(right)
        self.assertEqual(error["op"], "error")
        self.assertNotIn("session_id", error)
