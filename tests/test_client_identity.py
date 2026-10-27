import unittest
from pathlib import Path
import tempfile
from unittest.mock import Mock, patch
from tools import login_server, game_server
from tools.probe_login_reply import RC4
from tools.client_identity import parse_identity


def packet(opcode, fields, tail=b''):
    return opcode + b''.join(bytes([len(value.encode())]) + value.encode() for value in fields) + tail


class IdentityTests(unittest.TestCase):
    def test_login_shape_and_redaction(self):
        body = packet(b'\x05\x39', ['', 'A' * 64, 'secondary', '', '', 'en'])
        identity = parse_identity(body, 'login')
        self.assertEqual(identity.authn_token, 'A' * 64)
        self.assertNotIn('A' * 64, repr(identity))

    def test_game_account_binding(self):
        account = '1000001:12345678-1234-4234-8234-123456789abc'
        body = packet(b'\x13\x36', [account, '', 'B' * 64, ''], bytes(9))
        self.assertEqual(parse_identity(body, 'game').account_session, account)
        for malformed in (body[:-1], body + b'\0', body[:-1] + b'\1'):
            with self.assertRaises(ValueError):
                parse_identity(malformed, 'game')

    def test_no_placeholder_fallback(self):
        bodies = [b'', b'\x05\x39\xff', packet(b'\x05\x39', ['provider', 'token', '', '', '', 'en']),
                  packet(b'\x13\x36', ['1000001:not-a-guid', '', 'token', ''], bytes(9))]
        for body in bodies:
            with self.assertRaises(ValueError):
                parse_identity(body, 'game' if body[:2] == b'\x13\x36' else 'login')


class AuthenticatedListenerTests(unittest.TestCase):
    def exercise(self, family, bodies, accepts=True):
        module = login_server if family == 'login' else game_server
        key = bytes(range(32))
        cipher = RC4(key)
        raw = b''.join(module.frame(cipher.crypt(body)) for body in bodies)
        connection = Mock()
        connection.getpeername.return_value = ('127.0.0.1', 5100)
        connection.recv.side_effect = [raw[:3], raw[3:], OSError('test disconnect')]
        identity = {'account_id': '1000042', 'guid': '12345678-1234-4234-8234-123456789abc',
                    'authenticated_account_id': '12345678-1234-4234-8234-123456789abc'}
        if family == 'game':
            identity['world_store'] = Mock()
        authenticate = Mock(return_value=identity if accepts else None)
        greeting = b'\x00\x39' if family == 'login' else b'\x10\x36'
        reply_name = 'build_reply' if family == 'login' else 'build_game_reply'
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(module, 'read_greeting', return_value=module.frame(greeting)), \
                 patch.object(module, 'rsa_public_from_hello', return_value=(17,3)), \
                 patch.object(module, reply_name, return_value=b'handshake'):
                if family == 'login':
                    report = module.handle(connection, [{'id': 1204, 'group': 1, 'name': 'Test'}], 'untrusted-default',
                                           key=key, save_dir=Path(directory), authenticate=authenticate)
                else:
                    with patch.object(module, 'game_followup_replies', return_value=[]) as dispatch:
                        report = module.handle(connection, key, Path(directory), authenticate=authenticate)
                        if report['authentication'] in ('rejected', 'required'):
                            dispatch.assert_not_called()
            self.assertEqual(list(Path(directory).iterdir()), [], 'Authenticated runs must not save raw credentials or keys')
            if family == 'game' and accepts and authenticate.called:
                identity['world_store'].close.assert_called_once()
        return report, authenticate, connection

    def test_both_listeners_accept_verified_identity_without_captures(self):
        for family, header in [('login', b'\x05\x39'), ('game', b'\x13\x36')]:
            report, authenticate, _ = self.exercise(family, [header])
            self.assertEqual(report['authentication'], 'accepted')
            authenticate.assert_called_once_with(header)

    def test_no_fallback_for_rejected_identity(self):
        for family, header in [('login', b'\x05\x39'), ('game', b'\x13\x36')]:
            report, _, connection = self.exercise(family, [header], False)
            self.assertEqual(report['authentication'], 'rejected')
            self.assertEqual(connection.sendall.call_args_list[0].args, (b'handshake',))
            self.assertEqual(connection.sendall.call_count, 1)

    def test_login_lists_stored_characters_on_the_first_server(self):
        key = bytes(range(32))
        cipher = RC4(key)
        raw = b''.join(login_server.frame(cipher.crypt(body)) for body in (b'\x05\x39', b'\x0a\x39'))
        connection = Mock()
        connection.getpeername.return_value = ('127.0.0.1', 5100)
        connection.recv.side_effect = [raw, OSError('test disconnect')]
        identity = {'account_id': '1000042', 'guid': '12345678-1234-4234-8234-123456789abc',
                    'authenticated_account_id': '12345678-1234-4234-8234-123456789abc',
                    'characters': [{'name': 'Player1', 'level': 7}]}
        worlds = [{'id': 1361, 'group': 1, 'name': 'Test'}, {'id': 2361, 'group': 2, 'name': 'Test'}]
        with patch.object(login_server, 'read_greeting', return_value=login_server.frame(b'\x00\x39')), \
             patch.object(login_server, 'rsa_public_from_hello', return_value=(17, 3)), \
             patch.object(login_server, 'build_reply', return_value=b'handshake'), \
             patch.object(login_server, 'build_characters', wraps=login_server.build_characters) as listed:
            login_server.handle(connection, worlds, 'untrusted-default', key=key,
                                authenticate=Mock(return_value=identity))
        listed.assert_called_once_with([{'server_id': 1361, 'name': 'Player1', 'level': 7}])

    def test_requests_before_auth_and_replays_are_closed(self):
        for family, login, other in [('login', b'\x05\x39', b'\x0a\x39'), ('game', b'\x13\x36', b'\x22\x36')]:
            report, auth, _ = self.exercise(family, [other])
            self.assertEqual(report['authentication'], 'required')
            auth.assert_not_called()
            report, auth, _ = self.exercise(family, [login, login])
            self.assertEqual(report['authentication'], 'replay_rejected')
            self.assertEqual(auth.call_count, 1)
