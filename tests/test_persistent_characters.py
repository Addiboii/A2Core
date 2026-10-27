import base64
import struct
import unittest
import uuid
from unittest.mock import Mock
from tools.aion_protocol import encode_varint
from tools.persistent_characters import PersistentCharacters


class PersistentCharacterTests(unittest.TestCase):
    def setUp(self):
        self.store=Mock()
        self.account=uuid.uuid4()
        self.adapter=PersistentCharacters(self.store,self.account)
        self.state=dict(summary=dict(name='Saved',characterId=str(uuid.uuid4())),wireId=123,
            creationId=1,race=1,gender=1,voiceType=4,lobbySlot=2,appearance=base64.b64encode(b'\x00\xff').decode(),
            progress=dict(level=1,experience=0,mapId=1000,position=dict(x=4,y=5,z=6)))
        self.store.load.return_value=self.state
        self.store.select.return_value=self.state
        self.store.create.return_value=dict(characterId=self.state['summary']['characterId'])
        self.store.lobby.return_value=[]

    def creation(self):
        return b'\x1b\x36\x05Saved\x01'+struct.pack('<i',1)+b'\x02\x00\xff\x04\x02\x01'

    def test_creation_commits_before_reply_and_retries_keep_identity(self):
        first=self.adapter.create(self.creation(),now_ms=1)
        request=self.store.create.call_args.args[0]
        second=PersistentCharacters(self.store,self.account).create(self.creation(),now_ms=1)
        self.assertEqual(request,self.store.create.call_args.args[0])
        self.assertEqual(first,second)
        self.assertEqual(int.from_bytes(first[6:10],'little'),123)
        self.assertEqual(self.store.create.call_args.kwargs,dict(voice_type=4,lobby_slot=2))
        self.store.create.side_effect=ValueError('database unavailable')
        with self.assertRaises(ValueError): self.adapter.create(self.creation())

    def test_one_race_per_account(self):
        # Creation 1 is Light (race 1). A stored Dark character locks the account to Dark.
        self.store.lobby.return_value=[dict(self.state,race=2)]
        with self.assertRaisesRegex(ValueError,'other race'): self.adapter.create(self.creation())
        self.store.create.assert_not_called()
        self.store.lobby.return_value=[dict(self.state,race=1)]
        self.adapter.create(self.creation(),now_ms=1)
        self.store.create.assert_called_once()

    def test_login_reply_reports_the_account_race(self):
        from tools.game_server import parse_login_characters
        self.assertEqual(parse_login_characters(self.adapter.login_reply('1:g',now_ms=1))['server_race'],0)
        self.store.lobby.return_value=[self.state]
        self.assertEqual(parse_login_characters(self.adapter.login_reply('1:g',now_ms=1))['server_race'],1)

    def test_unknown_creation_tail_never_mutates(self):
        with self.assertRaises(ValueError): self.adapter.create(self.creation()+b'\x00')
        self.store.create.assert_not_called()

    def test_select_ignores_client_spawn_and_uses_durable_key(self):
        replies=self.adapter.select(b'\x1f\x36'+encode_varint(123)+struct.pack('<i3f',999,100,200,300))
        self.store.select.assert_called_once_with(123)
        # Official prefix: u16 result, varint key (123 is one byte), i32 last_using, i64 dbid.
        self.assertEqual(replies[0][4],123)
        self.assertEqual(int.from_bytes(replies[0][9:17],'little'),123)
        self.assertIn(struct.pack('<3f',4,5,6),replies[1])
        self.assertEqual(self.adapter.selected,self.state)

    def test_select_failure_does_not_install_character(self):
        self.store.select.side_effect=ValueError('not owned')
        with self.assertRaises(ValueError): self.adapter.select(b'\x1f\x36'+encode_varint(123)+bytes(16))
        self.assertIsNone(self.adapter.selected)

    def test_lobby_requires_assigned_slot_and_uses_saved_appearance(self):
        self.store.lobby.return_value=[self.state]
        record=self.adapter.lobby_records()[0]
        self.assertEqual(int.from_bytes(record[:4],'little'),123)
        self.assertIn(b'\x02\x00\xff\x04',record)
        self.state['lobbySlot']=None
        with self.assertRaises(ValueError): self.adapter.lobby_records()
