"""Connection-bound persistence client for trusted protocol handlers.

No account UUID can be supplied to these calls. The server derives account/world
from the opaque connection token and checks its parent session on every request.
"""
import json
import uuid
from urllib.request import Request, build_opener, ProxyHandler
from urllib.error import HTTPError, URLError


class WorldStoreError(ValueError):
    def __init__(self, status=None):
        self.status = status
        super().__init__('World persistence request failed' + (f' (HTTP {status})' if status else ''))


class WorldStore:
    def __init__(self, port, secret, token):
        if not isinstance(port, int) or not 1024 <= port <= 65535:
            raise ValueError('Invalid account service port')
        if not isinstance(token, str) or len(token) != 64 or any(c not in '0123456789abcdefABCDEF' for c in token):
            raise ValueError('Invalid world session')
        self._base = f'http://127.0.0.1:{port}/internal/world/'
        self._secret, self._token = secret, token
        self._opener = build_opener(ProxyHandler({}))

    def __repr__(self):
        return 'WorldStore [credentials redacted]'

    def _request(self, route, method='GET', body=None):
        if self._token is None:
            raise WorldStoreError(401)
        headers = {'X-Aion-Bridge': self._secret, 'X-Aion-World': self._token}
        data = None
        if body is not None:
            headers['Content-Type'] = 'application/json'
            data = json.dumps(body, allow_nan=False).encode()
        try:
            request = Request(self._base + route, data=data, method=method, headers=headers)
            with self._opener.open(request, timeout=10) as response:
                raw = response.read(1024 * 1024 + 1)
                if len(raw) > 1024 * 1024:
                    raise WorldStoreError()
                return json.loads(raw) if raw else None
        except HTTPError as error:
            raise WorldStoreError(error.code) from None
        except (URLError, OSError, ValueError):
            raise WorldStoreError() from None

    def characters(self):
        return self._request('characters')

    def lobby(self):
        return self._request('lobby')

    def select(self, wire_id):
        if type(wire_id) is not int or not 0 < wire_id <= 2147483647:
            raise ValueError('Invalid character wire ID')
        return self._request('characters/by-wire/' + str(wire_id))

    def create(self, request_id, profile_id, name, appearance, *, voice_type=0, lobby_slot=None):
        import base64
        return self._request('characters', 'POST', dict(requestId=str(uuid.UUID(str(request_id))),
            profileId=profile_id, name=name, appearance=base64.b64encode(appearance).decode('ascii'),
            voiceType=voice_type, lobbySlot=lobby_slot))

    def load(self, character):
        return self._request('characters/' + str(uuid.UUID(str(character))))

    def inventory(self, character):
        return self._request('characters/' + str(uuid.UUID(str(character))) + '/inventory')

    def save(self, character, revision, progress):
        return self._request('characters/' + str(uuid.UUID(str(character))) + '/save', 'POST',
                             dict(revision=revision, progress=progress))

    def close(self):
        if self._token is None:
            return
        try:
            self._request('close', 'POST', {})
        finally:
            self._token = None
