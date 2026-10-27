import base64
import struct
import unittest
import uuid
from pathlib import Path
from unittest.mock import Mock

from tools.aion_protocol import encode_varint, lz4_block, split_frames
from tools.game_server import (_lobby_char_desc, PackedWriter, build_login_characters,
                               build_comm_server_key, COMM_SERVER_KEY,
                               game_followup_replies, parse_login_characters,
                               checkpoint_tick, parse_position_report, CHECKPOINT_MS)
from tools.world_store import WorldStoreError

OFFICIAL = Path(__file__).resolve().parents[1] / 'research/local/official-login/login-rs-20261013.bin'


def unwrap(envelope):
    """0xffff | size u32 | LZ4 block holding one framed body."""
    assert envelope[:2] == b'\xff\xff'
    frames, _ = split_frames(lz4_block(envelope[6:], int.from_bytes(envelope[2:6], 'little')))
    return frames[0].body


class LoginListTests(unittest.TestCase):
    def test_official_three_character_login_rebuilds_exactly(self):
        # The official reply holds a session token, so it stays in ignored research/local.
        if not OFFICIAL.exists():
            self.skipTest('local official login sample unavailable')
        body = OFFICIAL.read_bytes()
        values = parse_login_characters(body)
        self.assertEqual([c['level'] for c in values['characters']], [1, 1, 1])
        self.assertEqual({c['equips'][0][0] for c in values['characters']}, {110160001})
        self.assertEqual(build_login_characters(**values), body)

    def test_bools_share_a_byte_across_records(self):
        weapon = ((110160001, 0, 1),)
        writer = PackedWriter()
        for key in (1, 2, 3):
            _lobby_char_desc(key=key, nickname='A', pc_id=5, equips=weapon, writer=writer)
        single = _lobby_char_desc(key=1, nickname='A', pc_id=5, equips=weapon)
        # Expired/display/default/override/pre-generated: five bits per record, so
        # three records need two bool bytes, not three (as in the official 0x3615).
        self.assertEqual(len(writer.out), 3 * len(single) - 1)

    def test_comm_server_key_reply_matches_login(self):
        body = build_login_characters([], '1:guid', '1204', COMM_SERVER_KEY, now_ms=5)
        reply = build_comm_server_key()
        self.assertEqual(reply[:4], b'\x5a\x36\x00\x00')  # _result u16
        key = COMM_SERVER_KEY.encode('ascii')
        self.assertEqual(reply[4:], bytes([len(key)]) + key)
        self.assertEqual(parse_login_characters(body)['comm_server_key'], COMM_SERVER_KEY)

    def test_world_entry_queries_get_official_replies(self):
        # Bodyless requests after 0x3648; each reply's tag is the request opcode + 1.
        for request, tag in ((0x4300, 0x4301), (0x570e, 0x570f), (0x56a7, 0x56a8), (0x8a1c, 0x8a1d)):
            replies = game_followup_replies(request.to_bytes(2, 'little'), now_ms=5, state={})
            self.assertEqual(int.from_bytes(replies[0][:2], 'little'), tag)
            # The last query also carries the one-off Chat unlock update (0xe258).
            self.assertEqual(len(replies), 2 if request == 0x8a1c else 1)

    def test_unlock_update_is_sent_once(self):
        state = {}
        first = game_followup_replies(b'\x1c\x8a', now_ms=5, state=state)
        later = game_followup_replies(b'\x59\x36', now_ms=5, state=state)
        self.assertEqual(first[1][:2], b'\x58\xe2')
        self.assertEqual(len(later), 1)  # comm key only

    def test_comm_key_request_also_sends_unlock_update(self):
        # 0xe258 (not the 0xe257 list) raises System_ContentsUnlocked, which starts chat login.
        replies = game_followup_replies(b'\x59\x36', now_ms=5, state={})
        self.assertEqual(replies[0][:2], b'\x5a\x36')
        self.assertEqual(replies[1], b'\x58\xe2\x02\x2d\x2b')

    def test_empty_list_round_trips(self):
        body = build_login_characters([], '1:guid', '1204', '5201', now_ms=5)
        self.assertEqual(parse_login_characters(body)['characters'], [])


class StoredCharacterFlowTests(unittest.TestCase):
    def setUp(self):
        self.saved = dict(summary=dict(name='Kept', characterId=str(uuid.uuid4())), wireId=77,
                          creationId=1, race=1, gender=2, voiceType=1, lobbySlot=0,
                          appearance=base64.b64encode(b'\x01\x02').decode(),
                          progress=dict(level=40, experience=0, mapId=1000,
                                        position=dict(x=10.0, y=20.0, z=30.0)))
        self.store = Mock()
        self.store.lobby.return_value = [self.saved]
        self.store.select.return_value = self.saved
        self.state = {'world_store': self.store, 'authenticated_account_id': str(uuid.uuid4())}

    def login(self):
        identity = b'1000001:00000000-0000-0000-0000-000000000000'
        return game_followup_replies(b'\x13\x36' + bytes([len(identity)]) + identity + b'\x00',
                                     state=self.state, now_ms=1000)

    def test_login_lists_stored_characters(self):
        replies = self.login()
        values = parse_login_characters(unwrap(replies[0]))
        (character,) = values['characters']
        self.assertEqual((character['nickname'], character['key'], character['level']), ('Kept', 77, 40))
        self.assertEqual(character['customizing_data'], b'\x01\x02')
        self.assertEqual(values['account_session'], '1000001:00000000-0000-0000-0000-000000000000')

    def test_select_then_load_uses_the_stored_character(self):
        self.login()
        select = b'\x1f\x36' + encode_varint(77) + struct.pack('<i3f', 0, 0, 0, 0)
        replies = game_followup_replies(select, state=self.state, now_ms=1000)
        self.store.select.assert_called_once_with(77)
        self.assertEqual(len(replies), 2)
        self.assertEqual(self.state['level'], 40)
        load = b'\x22\x36' + struct.pack('<i', 1) + bytes(21)
        burst = game_followup_replies(load, state=self.state, now_ms=1000)
        self.assertEqual(struct.unpack('<3f', burst[0][8:20]), (10.0, 20.0, 30.0))
        self.assertIn(b'\x04Kept', burst[1])

    def test_select_accepts_the_shifted_client_key(self):
        # Dev runs: key 1 in 0x361c, then the client selected 65536 (varint 80 80 04).
        self.store.select.side_effect = lambda key: self.saved if key == 77 else (_ for _ in ()).throw(ValueError('404'))
        select = b'\x1f\x36' + encode_varint(77 << 16) + struct.pack('<i3f', 0, 0, 0, 0)
        replies = game_followup_replies(select, state=self.state, now_ms=1000)
        self.assertEqual(len(replies), 2)
        # Official prefix: u16 result, varint key (echoed), i32 last_using, i64 dbid.
        key = encode_varint(77 << 16)
        self.assertEqual(replies[0][4:4 + len(key)], key)
        self.assertEqual(int.from_bytes(replies[0][8 + len(key):16 + len(key)], 'little'), 77)
        self.assertEqual(self.state['select_keys'], [77 << 16])

    def selected(self):
        self.login()
        game_followup_replies(b'\x1f\x36' + encode_varint(77) + bytes(16), state=self.state, now_ms=1000)
        self.store.save.side_effect = lambda character, revision, progress: dict(
            self.saved, progress=progress, revision=revision + 1)
        self.saved['revision'] = 5

    def test_position_reports_are_checkpointed_and_saved_on_disconnect(self):
        self.selected()
        # 0x3701 from a dev run: 02, then x y z at byte 3.
        report = bytes.fromhex('013702ca94e245d35a8ec6b707af463477a4a02743012b956e35a1010000')
        self.assertEqual(game_followup_replies(report, state=self.state, now_ms=1000), [])
        position = self.state['position']
        self.assertAlmostEqual(position[0], 7250.5986328125)
        checkpoint_tick(self.state, 1000)
        (character, revision, progress), _ = self.store.save.call_args
        self.assertEqual((character, revision), (self.saved['summary']['characterId'], 5))
        self.assertEqual(progress['position'], dict(zip('xyz', position)))
        self.assertEqual(progress['level'], 40)
        # Unchanged position: nothing; moved but before the interval: nothing; forced: saved.
        checkpoint_tick(self.state, 1000 + CHECKPOINT_MS)
        self.state['position'] = (1.0, 2.0, 3.0)
        checkpoint_tick(self.state, 1001)
        self.assertEqual(self.store.save.call_count, 1)
        checkpoint_tick(self.state, 1001, force=True)
        self.assertEqual(self.store.save.call_count, 2)
        self.assertEqual(self.store.save.call_args.args[1], 6)   # follows the new revision
        stationary = b'\x18\x37\x00\x00' + struct.pack('<3f', 4, 5, 6) + bytes(12)
        self.assertEqual(parse_position_report(stationary), (4.0, 5.0, 6.0))

    def test_logout_saves_then_replies(self):
        self.selected()
        self.state['position'] = (7.0, 8.0, 9.0)
        self.assertEqual(game_followup_replies(b'\x30\x36', state=self.state, now_ms=1000), [b'\x31\x36\x00\x00'])
        self.assertEqual(self.store.save.call_args.args[2]['position'], dict(x=7.0, y=8.0, z=9.0))
        # Dev launches (no stored character) still get the reply.
        self.assertEqual(game_followup_replies(b'\x30\x36', state={}, now_ms=1000), [b'\x31\x36\x00\x00'])

    def test_stale_save_keeps_other_edits_and_retries_once(self):
        self.selected()
        newer = dict(self.saved, revision=9, progress=dict(self.saved['progress'], level=80))
        calls = []

        def save(character, revision, progress):
            calls.append((revision, progress['level']))
            if revision != 9:
                raise WorldStoreError(409)
            return dict(newer, progress=progress, revision=10)
        self.store.save.side_effect = save
        self.store.load.return_value = newer
        self.state['position'] = (1.0, 2.0, 3.0)
        checkpoint_tick(self.state, 1000)
        self.assertEqual(calls, [(5, 40), (9, 80)])   # the dashboard's level 80 is kept
        self.assertEqual(self.state['checkpoints'], 1)

    def test_storage_failure_sends_nothing(self):
        self.store.lobby.side_effect = ValueError('database unavailable')
        self.assertEqual(self.login(), [])
        self.assertEqual(self.state['persistence_errors'], ['ValueError'])
        self.store.create.side_effect = ValueError('database unavailable')
        creation = b'\x1b\x36\x05Saved\x01' + struct.pack('<i', 1) + b'\x02\x00\xff\x04\x02\x01'
        self.assertEqual(game_followup_replies(creation, state=self.state, now_ms=1000), [])


if __name__ == '__main__':
    unittest.main()
