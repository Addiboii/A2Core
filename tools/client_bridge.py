"""Loopback-only adapter between legacy Python listeners and account services.

No passwords, bearer credentials, response bodies or raw packets are logged.
The machine bridge secret is not exposed to the original client or account page.
"""
import json
from pathlib import Path
import uuid
from urllib.request import Request, build_opener, ProxyHandler
from urllib.error import HTTPError, URLError
try:
    from .client_identity import parse_identity
    from .run_account_server import bridge_secret
    from .world_store import WorldStore
except ImportError:
    from client_identity import parse_identity
    from run_account_server import bridge_secret
    from world_store import WorldStore


class ClientBridge:
    def __init__(self, port=8780):
        if not isinstance(port, int) or not 1024 <= port <= 65535:
            raise ValueError('Invalid account service port')
        self.base = f'http://127.0.0.1:{port}/internal/client/'
        self.port = port
        self.secret = bridge_secret()
        self.opener = build_opener(ProxyHandler({}))

    def call(self, route, payload):
        if route not in ('launch-token', 'login', 'game'):
            raise ValueError('Unknown client bridge operation')
        request = Request(self.base + route, data=json.dumps(payload).encode(), method='POST',
                          headers={'Content-Type': 'application/json', 'X-Aion-Bridge': self.secret})
        try:
            with self.opener.open(request, timeout=10) as response:
                return json.loads(response.read(8192))
        except (HTTPError, URLError, OSError, ValueError):
            raise ValueError('Account bridge rejected the request or is unavailable') from None

    def login(self, body):
        try:
            identity = parse_identity(body, 'login')
            result = self.call('login', {'ticket': identity.authn_token, 'worldId': 1204})
            characters = [{'name': str(c['name']), 'level': int(c['level'])}
                          for c in result.get('characters', [])[:255]]
            return {'account_id': str(result['wireAccountId']), 'guid': str(uuid.UUID(result['grant'])),
                    'authenticated_account_id': str(uuid.UUID(result['accountId'])),
                    'characters': characters}
        except (ValueError, KeyError, TypeError):
            return None

    def game(self, body):
        try:
            identity = parse_identity(body, 'game')
            wire, grant = identity.account_session.split(':')
            result = self.call('game', {'ticket': identity.authn_token, 'wireAccountId': int(wire), 'grant': grant, 'worldId': 1204})
            return {'authenticated_account_id': str(uuid.UUID(result['accountId'])),
                    'world_store': WorldStore(self.port, self.secret, result['token'])}
        except (ValueError, KeyError, TypeError):
            return None


class LaunchTokenProvider:
    def __init__(self, path, port=8780):
        raw = Path(path).read_bytes()
        if len(raw) > 4096:
            raise ValueError('Invalid launch authorization file')
        value = json.loads(raw)
        token = value.get('authorization')
        if not isinstance(token, str) or len(token) != 64 or any(c not in '0123456789abcdefABCDEF' for c in token) or value.get('worldId') != 1204:
            raise ValueError('Invalid launch authorization file')
        self.authorization = token
        self.bridge = ClientBridge(port)
        self.response = None

    def __call__(self):
        if self.response is None:
            result = self.bridge.call('launch-token', {'authorization': self.authorization})
            self.response = {'authn_token': result['token']}
            self.authorization = ''
        return self.response
