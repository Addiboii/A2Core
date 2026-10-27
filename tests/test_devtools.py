import json
from pathlib import Path
import struct
import tempfile
import threading
import unittest
from unittest.mock import patch, Mock

from tools.devtools import decode_snapshot, diagnose, summarize_bodies, compare_reports, table_rows, resolve_chain
from tools.game_server import frame, handle
from tools.probe_login_reply import RC4
from tools.protocol_knowledge import summarize_body, describe
from tools.run_diagnostics import record_exchange, write_manifest
from tools.run_game_chain import run_server


class DiagnosticTests(unittest.TestCase):
    def test_live_handler_append_capture_and_record_actual_replies(self):
        key = bytes(range(32))
        cipher = RC4(key)
        load = bytes.fromhex('22360700000000') + bytes(20)
        combat = b'\x3a\x38'
        raw = b''.join(frame(cipher.crypt(b)) for b in (load, combat))
        connection = Mock()
        connection.getpeername.return_value = ('127.0.0.1', 5000)
        connection.recv.side_effect = [raw[:4], raw[4:9], raw[9:], OSError('test disconnect')]
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp)
            with patch('tools.game_server.read_greeting', return_value=frame(b'\x10\x36')), \
                 patch('tools.game_server.rsa_public_from_hello', return_value=(17, 3)), \
                 patch('tools.game_server.build_game_reply', return_value=b'test-handshake'):
                result = handle(connection, key, path)
            self.assertEqual((path / 'followup-5000.bin').read_bytes(), raw)
            self.assertEqual((path / 'headers-5000.txt').read_text().splitlines(), ['0x3622', '0x383a'])
            events = [json.loads(line) for line in (path / 'events-5000.jsonl').read_text().splitlines()]
            self.assertEqual(len(events), 2)
            self.assertIn('0x3633', events[0]['reply_opcodes'])
            self.assertEqual(events[1]['reply_opcodes'], ['0x383b', '0x8d21'])
            self.assertEqual(result['raw_followup_bytes'], len(raw))

    def test_latest_selects_run_directories_not_chain_log_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'chain-run-log.txt.err').write_text('old error')
            (root / 'chain-20260101-010000-abc').mkdir()
            newest = root / 'chain-20260101-020000-def'
            newest.mkdir()
            with patch('tools.devtools.LOCAL', root):
                self.assertEqual(resolve_chain('latest'), newest)
    def test_continuous_cipher_and_partial_tail(self):
        key = bytes(range(32))
        cipher = RC4(key)
        bodies = [b'\x01\x36' + bytes(8), b'\x3a\x38']
        raw = b''.join(frame(cipher.crypt(b)) for b in bodies)
        decoded, tail = decode_snapshot(raw + b'\x80', key)
        self.assertEqual(decoded, bodies)
        self.assertEqual(tail, 1)
        self.assertEqual(decode_snapshot(raw[:-1], key), (bodies[:1], len(frame(bodies[1])) - 1))

    def test_long_sessions_and_malformed_lengths(self):
        key = bytes(32)
        cipher = RC4(key)
        raw = b''.join(frame(cipher.crypt(b'\x3a\x38')) for _ in range(16385))
        self.assertEqual(len(decode_snapshot(raw, key)[0]), 16385)
        with self.assertRaises(ValueError):
            decode_snapshot(b'\x01', key)
        with self.assertRaises(ValueError):
            decode_snapshot(b'', b'wrong-length')

    def test_summary_uses_observed_names_and_omits_identity(self):
        body = b'\x1b\x36PRIVATE_CHARACTER_CUSTOMIZATION'
        self.assertNotIn('PRIVATE', json.dumps(summarize_body(body)))
        self.assertEqual(describe(0x3741)['name'], 'Unknown')
        self.assertEqual(describe(0x3800)['name'], 'Skill start request')
        position = b'\x18\x37\x00\x00' + struct.pack('<4f', 1, 2, 3, 0) + bytes(8)
        report = summarize_bodies([body, b'\x22\x36', position, b'\x00\x38'])
        self.assertEqual(report['last_stationary_position_candidate'], [1, 2, 3])
        self.assertEqual(report['last_observed_stage'], 'movement_reports_received')
        self.assertEqual(report['unsupported_skill_shapes'], 1)

    def test_historical_without_journal_does_not_claim_missing_replies(self):
        with tempfile.TemporaryDirectory() as tmp:
            chain = Path(tmp)
            game = chain / 'game'
            game.mkdir()
            key = bytes(range(32))
            (game / 'injected-key-1.bin').write_bytes(key)
            (game / 'followup-1.bin').write_bytes(frame(RC4(key).crypt(b'\x3a\x38')))
            row = diagnose(chain)['connections'][0]
            self.assertEqual(row['reply_evidence'], 'unavailable_for_this_run')
            self.assertEqual(row['reply_contract_gaps'], {})
            record_exchange(game / 'events-1.jsonl', b'\x3a\x38', [], {}, 1)
            with (game / 'events-1.jsonl').open('a') as f:
                f.write('{')  # concurrently appended partial journal line
            row = diagnose(chain)['connections'][0]
            self.assertEqual(row['reply_contract_gaps'], {'0x383a': 1})
            self.assertEqual(row['incomplete_journal_lines'], 1)

    def test_durable_error_and_manifest(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            def fail(**kwargs):
                raise TypeError('test resource clock failure')
            stop = threading.Event()
            run_server(fail, {'save_dir': root}, stop)
            self.assertTrue(stop.is_set())
            saved = json.loads((root / 'error.json').read_text())
            self.assertEqual(saved['type'], 'TypeError')
            self.assertEqual(saved['stack'][-1]['function'], 'fail')
            write_manifest(root, root, {'mp': 200})
            self.assertEqual(json.loads((root / 'manifest.json').read_text())['settings'], {'mp': 200})

    def test_query_table_and_comparison(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'table.json'
            path.write_text(json.dumps({'Properties': {'Data': [
                {'ID': {'Value': 1}, 'Name': 'Greatsword', 'Other': 99},
                {'ID': {'Value': 2}, 'Name': 'Sword'}]}}))
            found = table_rows(path, row_id=1, name='great', fields=['ID', 'Name'])
            self.assertEqual(found['matches'], 1)
            self.assertNotIn('Other', found['rows'][0])
        a = {'chain': 'a', 'connections': [{'opcode_counts': {'0x3800': 3}}]}
        b = {'chain': 'b', 'connections': [{'opcode_counts': {'0x3800': 5}}]}
        self.assertEqual(compare_reports(a, b)['opcode_count_deltas'], {'0x3800': 2})


if __name__ == '__main__':
    unittest.main()
