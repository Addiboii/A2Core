import json
import sys
import threading
import types
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from unittest.mock import patch

from tools import account_login
from tools.account_login import AccountLoginError, AccountSession, sign_in_for_launch


class FakeAccountService(BaseHTTPRequestHandler):
    calls = []

    def log_message(self, *args):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers['Content-Length'])) or b'{}')
        type(self).calls.append((self.path, self.headers.get('Authorization'), body))
        if self.path == '/api/accounts/login':
            if body == {'username': 'hero', 'password': 'correct horse'}:
                return self.reply(200, {'token': 'T' * 64})
            return self.reply(401, None)
        if self.path == '/api/client-launches' and self.headers.get('Authorization') == 'Bearer ' + 'T' * 64:
            return self.reply(200, {'authorization': 'A' * 64, 'worldId': 1204})
        if self.path == '/api/accounts/logout':
            return self.reply(204, None)
        return self.reply(401, None)

    def reply(self, status, payload):
        data = json.dumps(payload).encode() if payload is not None else b''
        self.send_response(status)
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)


class AccountLoginTests(unittest.TestCase):
    def setUp(self):
        FakeAccountService.calls = []
        self.server = HTTPServer(('127.0.0.1', 0), FakeAccountService)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.session = AccountSession(self.server.server_address[1])

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()

    def test_sign_in_requests_the_launch_authorization_and_signs_out(self):
        self.session.sign_in('hero', 'correct horse')
        self.assertEqual(self.session.launch_authorization(), {'authorization': 'A' * 64, 'worldId': 1204})
        self.session.sign_out()
        self.assertIsNone(self.session.token)
        self.assertEqual([path for path, _, _ in FakeAccountService.calls],
                         ['/api/accounts/login', '/api/client-launches', '/api/accounts/logout'])
        self.assertNotIn('correct horse', repr(self.session))

    def test_wrong_password_and_missing_service_are_readable(self):
        with self.assertRaisesRegex(AccountLoginError, 'Wrong username or password'):
            self.session.sign_in('hero', 'nope')
        with self.assertRaisesRegex(AccountLoginError, 'not running'):
            AccountSession(65000).sign_in('hero', 'x')   # nothing listens there

    def _fake_tkinter(self):
        """Headless Linux CI has no python3-tk; stub tkinter so login_window patches apply."""
        module = types.ModuleType('tkinter')
        module.TclError = Exception
        return patch.dict(sys.modules, {'tkinter': module})

    def test_window_retries_until_success_and_returns_authorization(self):
        def window(attempt, username=''):
            self.assertEqual(attempt('hero', 'wrong'), 'Wrong username or password.')
            self.assertEqual(attempt('', ''), 'Enter your username and password.')
            return attempt('hero', 'correct horse') is None
        with self._fake_tkinter(), patch.object(account_login, 'login_window', window):
            self.assertEqual(sign_in_for_launch(self.session)['authorization'], 'A' * 64)

    def test_cancelled_window_launches_nothing(self):
        with self._fake_tkinter(), patch.object(account_login, 'login_window', lambda attempt, username='': False):
            self.assertIsNone(sign_in_for_launch(self.session))


if __name__ == '__main__':
    unittest.main()
