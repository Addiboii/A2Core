import http.client
import json
from pathlib import Path
import tempfile
import threading
import unittest
from http.server import HTTPServer

from tools.probe_local_bootstrap import candidate, digest, handler, process_image_present, restore, save_state


class LocalBootstrapTests(unittest.TestCase):
    def test_process_listing_accepts_legacy_and_utf16_bytes(self):
        self.assertTrue(process_image_present(b'"AION2.exe","13084","Console","1","100 K"\r\n', 'AION2.exe'))
        self.assertTrue(process_image_present('"AION2.exe","13084","Console","1","100 K"\r\n'.encode('utf-16'), 'AION2.exe'))
        self.assertFalse(process_image_present(b'INFO: no tasks \xff running\r\n', 'AION2.exe'))
        self.assertFalse(process_image_present(b'', 'AION2.exe'))

    def test_preserves_existing_sections_and_utf16(self):
        original = '[UserSettings]\r\nName=example\r\n'.encode('utf-16')
        text = candidate(original, 8087).decode('utf-16')
        self.assertTrue(text.startswith(original.decode('utf-16')))
        self.assertIn('http://127.0.0.1:8087', text)
        # The updater profile points at loopback instead of NCSoft's update service.
        self.assertIn('NcUpdaterConfigType=LOCAL\r\n', text)
        self.assertIn('("LOCAL",(ServerUrl="127.0.0.1"', text)

    def test_refuses_existing_target_section(self):
        with self.assertRaises(ValueError):
            candidate(b'[/Script/Aion2.AionGamePlatformSettings]\nExisting=value', 8087)

    def test_restore_preserves_game_writes_and_original_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            target = directory / 'GamePlatform.ini'
            original = b'original\r\n'
            (directory / 'original.ini').write_bytes(original)
            target.write_bytes(b'game-written-content')
            save_state(directory, {'target': str(target), 'original_sha256': digest(original), 'status': 'active'})
            restore(directory, target)
            self.assertEqual(target.read_bytes(), original)
            saved = list(directory.glob('before-restore-*.ini'))
            self.assertEqual(saved[0].read_bytes(), b'game-written-content')
            restore(directory, target)
            self.assertEqual(len(list(directory.glob('before-restore-*.ini'))), 1)

    def test_corrupt_backup_does_not_replace_current_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            target = directory / 'GamePlatform.ini'
            target.write_bytes(b'current')
            (directory / 'original.ini').write_bytes(b'corrupt')
            save_state(directory, {'target': str(target), 'original_sha256': digest(b'original'), 'status': 'active'})
            with self.assertRaises(ValueError):
                restore(directory, target)
            self.assertEqual(target.read_bytes(), b'current')

    def test_listener_redacts_query_headers_and_body(self):
        events = []
        with HTTPServer(('127.0.0.1', 0), handler(events)) as server:
            worker = threading.Thread(target=server.handle_request)
            worker.start()
            connection = http.client.HTTPConnection('127.0.0.1', server.server_port, timeout=3)
            connection.request('POST', '/MobileAppConfigSdk/app/key?token=secret', b'private-body',
                               {'Authorization': 'private-header'})
            response = connection.getresponse()
            self.assertEqual(response.status, 503)
            response.read()
            connection.close()
            worker.join(timeout=3)
        self.assertEqual(events[0]['path'], '/MobileAppConfigSdk/app/key')
        self.assertEqual(events[0]['method'], 'POST')
        self.assertNotIn('secret', json.dumps(events))
        self.assertNotIn('private', json.dumps(events))

    def test_route_recording_keeps_config_caller_but_not_secrets(self):
        events = []
        with HTTPServer(('127.0.0.1', 0), handler(events, record_route_paths=True)) as server:
            worker = threading.Thread(target=server.handle_request)
            worker.start()
            connection = http.client.HTTPConnection('127.0.0.1', server.server_port, timeout=3)
            connection.request('GET', '/MobileAppConfigSdk/app/key?app_id=lime-app', b'private-body',
                               {'Authorization': 'private-header', 'Lime-Sdk-Version': '9.9',
                                'User-Agent': 'probe-agent'})
            connection.getresponse().read()
            connection.close()
            worker.join(timeout=3)
        event = events[0]
        self.assertEqual(event['query'], 'app_id=lime-app')
        self.assertEqual(event['caller_headers'], {'lime-sdk-version': '9.9', 'user-agent': 'probe-agent'})
        self.assertIn('authorization', event['header_names'])
        self.assertNotIn('private', json.dumps(events))


    def test_local_lime_routes_keep_headers_and_body(self):
        events = []
        with HTTPServer(('127.0.0.1', 0), handler(events, record_route_paths=True)) as server:
            worker = threading.Thread(target=server.handle_request)
            worker.start()
            connection = http.client.HTTPConnection('127.0.0.1', server.server_port, timeout=3)
            connection.request('POST', '/local-lime/gameLogin/loginWithToken', b'{"a":1}',
                               {'Content-Type': 'application/json', 'Lime-Sdk-Version': '2.2.0'})
            connection.getresponse().read()
            connection.close()
            worker.join(timeout=3)
        self.assertEqual(events[0]['route_path'], '/local-lime/gameLogin/loginWithToken')
        self.assertEqual(events[0]['body'], '{"a":1}')
        self.assertEqual(events[0]['headers']['Lime-Sdk-Version'], '2.2.0')


if __name__ == '__main__':
    unittest.main()
