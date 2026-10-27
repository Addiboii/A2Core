"""Strict identity parsing for observed original-client NPLogin request shapes.

Evidence:52 local login/game captures, research/local/auth-handoff-evidence.json.
Only the observed short UTF-8 strings and zero reconnect/teleport tail are supported.
Returned credentials are sensitive: never include them in diagnostics or captures.
"""
from dataclasses import dataclass
import uuid


@dataclass(repr=False, frozen=True)
class ClientIdentity:
    authn_token: str
    account_session: str = ''

    def __repr__(self):
        return 'ClientIdentity(credentials=<redacted>)'


def parse_identity(body, family):
    opcode, count = (b'\x05\x39', 6) if family == 'login' else (b'\x13\x36', 4)
    if family not in ('login', 'game') or body[:2] != opcode:
        raise ValueError('Unexpected identity opcode')
    offset, strings = 2, []
    for _ in range(count):
        if offset >= len(body):
            raise ValueError('Truncated identity')
        size = body[offset]
        offset += 1
        if size > 127 or offset + size > len(body):
            raise ValueError('Unsupported identity string length')
        strings.append(body[offset:offset + size].decode('utf-8', errors='strict'))
        offset += size
    if family == 'login':
        if offset != len(body) or strings[0] or strings[3] or strings[4]:
            raise ValueError('Unsupported login identity shape')
        return ClientIdentity(strings[1])
    if body[offset:] != bytes(9) or strings[1] or strings[3]:
        raise ValueError('Unsupported game identity shape')
    account, separator, grant = strings[0].partition(':')
    if not separator or not account.isascii() or not account.isdecimal() or len(account) > 16:
        raise ValueError('Invalid account session')
    if str(uuid.UUID(grant)) != grant.lower():
        raise ValueError('Invalid account grant')
    return ClientIdentity(strings[2], strings[0])
