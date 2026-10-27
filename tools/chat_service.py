"""Transport-independent chat/GM core; not an implementation of Purple's API.

Only trusted server code may register sessions, assign roles or change rooms.
Run on the world thread. A future network adapter must authenticate before
registering and must deliver returned messages/packets to the indicated session.
No database, network listener, client opcode guesses or client-provided roles.
"""
from collections import deque
from dataclasses import dataclass
import json
import shlex
import time
import unicodedata

from .starter_casts import build_resource_update


@dataclass(frozen=True)
class Delivery:
    recipient: str
    text: str
    sender: str = 'Server'
    channel: str = 'system'
    packets: tuple = ()


@dataclass
class _Session:
    account: str
    name: str
    room: tuple
    state: dict
    gm: bool


class ItemCatalog:
    def __init__(self, rows=()):
        self.items = {}
        for row in rows:
            identity = row.get('ID')
            identity = identity.get('Value') if isinstance(identity, dict) else identity
            if isinstance(identity, int):
                self.items[identity] = str(row.get('Name', identity))

    @classmethod
    def load(cls, path):
        data = json.loads(path.read_text(encoding='utf-8'))['Properties']['Data']
        return cls(data if isinstance(data, list) else [data])

    def search(self, query):
        return [(key, name) for key, name in self.items.items()
                if str(key) == query or query.casefold() in name.casefold()][:5]


class ChatService:
    """Room chat plus private GM commands, with server-owned session bindings.

    The default grant policy denies everyone. gm_accounts must come from trusted
    configuration and account IDs must come from authentication, never chat JSON.
    Current loopback game login is not production account authentication.
    """
    def __init__(self, *, gm_accounts=(), catalog=None, audit=None, clock=time.monotonic):
        self._gm_accounts = frozenset(gm_accounts)
        self._sessions = {}
        self._recent = {}
        self.catalog = catalog or ItemCatalog()
        self.audit = audit
        self.clock = clock

    def register(self, session, *, account, name, world, map_id, state):
        if session in self._sessions:
            raise ValueError('Session already registered')
        self._sessions[session] = _Session(account, name, (world, map_id), state,
                                            account in self._gm_accounts)

    def move(self, session, *, world, map_id):
        self._sessions[session].room = (world, map_id)

    def disconnect(self, session):
        self._sessions.pop(session, None)

    def receive(self, session, text):
        actor = self._sessions.get(session)
        if actor is None:
            raise PermissionError('Chat session is not authenticated')
        private = lambda message: [Delivery(session, message)]
        if not isinstance(text, str) or not 1 <= len(text) <= 100:
            return private('Chat must contain 1–100 characters.')
        if any(unicodedata.category(c).startswith('C') for c in text):
            return private('Control characters are not allowed.')
        text = text.strip()
        if not text:
            return private('Enter a message.')
        now = self.clock()
        # Account scope prevents opening another session to bypass the limit.
        for key in list(self._recent):
            history = self._recent[key]
            while history and history[0] <= now - 5:
                history.popleft()
            if not history:
                del self._recent[key]
        recent = self._recent.setdefault(actor.account, deque())
        if len(recent) >= 5:
            return private('Please wait before sending another message.')
        recent.append(now)
        if text.startswith('/'):
            return self._command(session, actor, text)
        return [Delivery(key, text, actor.name, 'local')
                for key, other in self._sessions.items() if other.room == actor.room]

    def _command(self, session, actor, text):
        result = 'rejected'
        command = 'unknown'
        try:
            words = shlex.split(text)
            if len(words) < 2 or words[0].lower() != '/gm':
                return [Delivery(session, 'Unknown command. Use /gm help.')]
            command = words[1].lower()
            if command not in ('help', 'item', 'restore', 'level', 'give', 'equip', 'teleport'):
                command = 'unknown'
            if not actor.gm:
                result = 'denied'
                return [Delivery(session, 'GM permission required.')]
            if self.audit is None:
                return [Delivery(session, 'GM commands require an audit sink.')]
            # Fail closed if auditing is unavailable, before any mutation.
            self.audit({'account': actor.account, 'command': command, 'result': 'requested'})
            args = words[2:]
            if command == 'help' and not args:
                response = 'Available: /gm help; /gm item search <name or ID>; /gm restore'
            elif command == 'item' and len(args) >= 2 and args[0] == 'search':
                rows = self.catalog.search(' '.join(args[1:]))
                response = '\n'.join(f'{key}: {name}' for key, name in rows) or 'No matching items.'
            elif command == 'restore' and not args:
                resources = actor.state.get('resources')
                if resources is None:
                    return [Delivery(session, 'Enter the world first.')]
                restored = dict(resources)
                for resource in ('hp', 'mp', 'sp'):
                    restored[resource] = restored[resource + '_max']
                packet = build_resource_update(restored)
                resources.update(restored)
                result = 'applied'
                return [Delivery(session, 'HP, MP and stamina restored.', packets=(packet,))]
            elif command in ('level', 'give', 'equip', 'teleport'):
                response = 'Unavailable: the game update format is not verified yet.'
            else:
                response = 'Invalid command or arguments. Use /gm help.'
            result = 'answered'
            return [Delivery(session, response)]
        except ValueError:
            return [Delivery(session, 'Invalid command syntax.')]
        finally:
            if self.audit is not None:
                # No chat contents, tokens or complete incoming command strings.
                self.audit({'account': actor.account, 'command': command, 'result': result})
