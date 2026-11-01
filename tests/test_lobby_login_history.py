import unittest
from unittest import mock

from tools.aion_protocol import encode_varint, lz4_block, split_frames
from tools.game_server import build_login_characters, parse_login_characters, game_followup_replies
from tools.login_server import build_characters
from tools.persistent_characters import PersistentCharacters


def unwrap(envelope):
    assert envelope[:2] == b'\xff\xff'
    frames, _ = split_frames(lz4_block(envelope[6:], int.from_bytes(envelope[2:6], 'little')))
    return frames[0].body


class LobbyLoginHistoryTests(unittest.TestCase):
    """Game 0x3615/0x3626 LobbyCharDesc and login 0x390b last_played_unix_ms."""
    def test_login_port_last_played_round_trips(self):
        from tools.aion_protocol import Reader
        played = 1_700_000_000_123
        body = build_characters([dict(server_id=1361, name='Hero', level=10, last_played_unix_ms=played)])
        reader = Reader(body)
        self.assertEqual(reader.uint(2), 0x390b)
        reader.uint(2)
        reader.uint(1)
        reader.uint(2)
        reader.take(reader.uint(1))
        reader.uint(4)
        reader.uint(4)
        reader.uint(4)
        reader.uint(4)
        reader.uint(1)
        self.assertEqual(reader.uint(8), played)

    def test_lobby_char_desc_includes_logout_and_reg_time(self):
        now = 1_700_000_000_000
        body = build_login_characters([dict(
            key=7, nickname='A', pc_id=5, logout_time=now, reg_time=now - 86_400_000,
            equips=((110160001, 0, 1),))], '1:guid', '1204', '5201', now_ms=now)
        parsed = parse_login_characters(body)
        (character,) = parsed['characters']
        self.assertEqual(character['logout_time'], now)
        self.assertEqual(character['reg_time'], now - 86_400_000)

    def test_persistent_adapter_sorts_by_lobby_slot(self):
        class Store:
            def lobby(self):
                return [
                    dict(summary=dict(name='Old'), wireId=1, creationId=1, race=1, gender=1, voiceType=0,
                         lobbySlot=1, appearance='AA==', progress=dict(level=1, experience=0, mapId=1000,
                         position=dict(x=0.0, y=0.0, z=0.0)), lastLogoutUnixMs=100, registeredUnixMs=50),
                    dict(summary=dict(name='New'), wireId=2, creationId=1, race=1, gender=1, voiceType=0,
                         lobbySlot=0, appearance='AA==', progress=dict(level=1, experience=0, mapId=1000,
                         position=dict(x=0.0, y=0.0, z=0.0)), lastLogoutUnixMs=200, registeredUnixMs=50),
                ]

        records = PersistentCharacters(Store(), '00000000-0000-0000-0000-000000000001').lobby_characters()
        self.assertEqual([row['nickname'] for row in records], ['New', 'Old'])
        self.assertEqual([row['slot_no'] for row in records], [0, 1])

    def test_mock_store_login_reply_carries_logout_time(self):
        saved = dict(summary=dict(name='Kept'), wireId=77, creationId=1, race=1, gender=2, voiceType=1,
                     lobbySlot=0, appearance='AQI=', progress=dict(level=40, experience=0, mapId=1000,
                     position=dict(x=10.0, y=20.0, z=30.0)), lastLogoutUnixMs=9_000, registeredUnixMs=1_000)
        store = mock.Mock()
        store.lobby.return_value = [saved]
        state = {'world_store': store, 'authenticated_account_id': '00000000-0000-0000-0000-000000000002'}
        identity = b'1000001:00000000-0000-0000-0000-000000000000'
        replies = game_followup_replies(b'\x13\x36' + bytes([len(identity)]) + identity + b'\x00',
                                        state=state, now_ms=1000)
        values = parse_login_characters(unwrap(replies[0]))
        self.assertEqual(values['characters'][0]['logout_time'], 9_000)


if __name__ == '__main__':
    unittest.main()
