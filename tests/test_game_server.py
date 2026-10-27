from spawn_decoder import decode_spawn
import struct
from tools import world_npcs
import unittest
from pathlib import Path

from tools.game_server import (    build_character_created, combat_state_tick, COMBAT_TIMEOUT_MS, build_clock, build_game_login,
    build_game_login_wire, build_game_reply, build_load_complete,
    build_name_check, build_ping_reply, build_prepare_enter_map,
    build_select_character, drain_frames, frame,
    game_followup_replies, _game_char_create_request,
    _game_load_complete_request,
    _game_select_character_request, _lobby_char_desc,
    build_my_char_appear, build_char_barrier,
    build_char_stats_update, build_mentoring_role_update)
from tools.aion_protocol import Reader, split_frames, compressed_messages


def parse_one(wire):
    frames, consumed = split_frames(wire)
    assert consumed == len(wire) and len(frames) == 1, "expected exactly one frame"
    return frames[0]


class GameServerReplyTests(unittest.TestCase):
    def test_game_reply_layout(self):
        modulus = (1 << 2048) - 159  # dummy 2048-bit modulus; only layout is checked
        exponent = 3
        reply = build_game_reply(modulus, exponent, b"\x00" * 18, parameter=60000)
        frame = parse_one(reply)
        body = frame.body
        self.assertEqual(int.from_bytes(body[:2], "little"), 0x3611)
        reader = Reader(body)
        self.assertEqual(reader.uint(2), 0x3611)
        self.assertEqual(reader.uint(2), 0)        # result field
        self.assertEqual(reader.uint(2), 60000)    # parameter
        self.assertEqual(reader.uint(2), 0)        # extra u16
        blob = reader.take(reader.varuint())
        self.assertEqual(len(blob), (modulus.bit_length() + 7) // 8)
        self.assertEqual(len(body) - reader.offset, 12)  # trailer bytes

    def test_ping_reply_echoes_counter_with_clock(self):
        body = build_ping_reply(0x1122334455667788, now_ms=1234567890123)
        self.assertEqual(body[:2], b"\x03\x36")
        self.assertEqual(int.from_bytes(body[2:4], "little"), 0)
        self.assertEqual(int.from_bytes(body[4:12], "little"), 0x1122334455667788)
        self.assertEqual(int.from_bytes(body[12:20], "little"), 1234567890123)

    def test_clock_layout(self):
        body = build_clock(now_ms=1791465861135)
        self.assertEqual(body, b"\x00\x36" + (1791465861135).to_bytes(8, "little"))

    def test_frame_prefix_is_plaintext_varint(self):
        wire = frame(build_clock(now_ms=5))
        parsed = parse_one(wire)
        self.assertEqual(parsed.body, b"\x00\x36" + (5).to_bytes(8, "little"))

    def test_followup_routes_ping_only(self):
        # 0x3602 Ping_RQ with a counter -> 0x3603 echo.
        request = b"\x02\x36" + (0xAABBCCDDEEFF0011).to_bytes(8, "little")
        replies = game_followup_replies(request, now_ms=1000)
        self.assertEqual(len(replies), 1)
        self.assertEqual(replies[0][:2], b"\x03\x36")
        self.assertEqual(int.from_bytes(replies[0][4:12], "little"), 0xAABBCCDDEEFF0011)
        # Unknown headers are not answered.
        self.assertEqual(game_followup_replies(b"\x20\x36" + b"\x00" * 3), [])

    def test_followup_routes_game_login_to_candidate_envelope(self):
        request = b"\x13\x36" + b"\x00" * 10
        replies = game_followup_replies(request, now_ms=1791465861099)
        self.assertEqual(len(replies), 1)
        envelope = replies[0]
        self.assertEqual(envelope[:2], b"\xff\xff")
        report = compressed_messages(envelope)
        self.assertEqual(report["uncompressed_bytes"], int.from_bytes(envelope[2:6], "little"))
        self.assertEqual(report["inner_frame_count"], 1)
        self.assertEqual(report["inner_frames"][0]["header_u16_le"], "0x3615")

    def test_intro_login_uses_placeholder_and_spawn(self):
        intro = {"name": "$AbcdEfghijk", "server_id": 1361, "code": 42}
        request = b"\x13\x36" + b"\x00" * 10
        replies = game_followup_replies(request, now_ms=1791465861099,
                                        state={"intro": intro})
        fields = compressed_messages(replies[0])["inner_frames"][0]["decoded_fields"]
        tail = fields["tail"]
        self.assertEqual(tail["character_name_candidate"], "$AbcdEfghijk")
        self.assertEqual(tail["world_id_candidate"], 1361)
        pos = tail["position_xyz_candidate"]
        self.assertAlmostEqual(pos[0], 7259.90771484375, places=2)
        self.assertAlmostEqual(pos[1], -18223.455078125, places=2)
        self.assertAlmostEqual(pos[2], 22404.498046875, places=2)
        body = build_game_login(
            "$AbcdEfghijk", (-100938.4765625, 54664.25, 24089.0),
            world_id=1361, name_code=42, race=1, gender=2, detail_flag=1,
            equip_level=12, level=1, zone_id=3016, now_ms=1791465861099)
        tail_bytes = body[20:]
        self.assertEqual(int.from_bytes(tail_bytes[18:22], "little"), 42)
        self.assertEqual(tail_bytes[22:25], b"\x01\x02\x01")
        self.assertEqual(int.from_bytes(tail_bytes[32:36], "little"), 12)
        self.assertEqual(int.from_bytes(tail_bytes[44:48], "little"), 1)
        self.assertEqual(int.from_bytes(tail_bytes[77:79], "little"), 3016)
        self.assertEqual(tail_bytes[812:816], b"1361")

    def test_followup_appear_uses_loaded_map(self):
        body = b"\x22\x36" + (7).to_bytes(4, "little", signed=True) + b"\x00" + b"\x00" * 20
        replies = game_followup_replies(
            body, now_ms=1, state={"intro": {"name": "$AbcdEfghijk"}})
        appear = replies[1]
        decoded = decode_spawn(appear)
        self.assertEqual(decoded['map_id'], 1000)
        self.assertEqual(decoded['reason'], 0)
        self.assertEqual(decoded['equips'][0][:2], (110160001, 1))

    def test_game_login_body_layout(self):
        body = build_game_login("$Hero", (1.5, 2.5, 3.5), now_ms=1791465861099)
        self.assertEqual(body[:2], b"\x15\x36")
        self.assertEqual(int.from_bytes(body[2:4], "little"), 0)
        self.assertEqual(int.from_bytes(body[4:12], "little"), 1791465861099)
        self.assertEqual(int.from_bytes(body[12:20], "little"), 1791465861099 - 25200000)
        tail = body[20:]
        self.assertEqual(len(tail), 824)
        self.assertEqual(tail[0], 1)
        self.assertEqual(tail[1:5], (0x2A14).to_bytes(4, "little"))
        self.assertEqual(tail[5], 5)
        self.assertEqual(tail[6:11], b"$Hero")
        pos = struct.unpack("<3f", tail[65:77])
        self.assertAlmostEqual(pos[0], 1.5, places=2)
        self.assertEqual(int.from_bytes(tail[372:376], "little"), 1204)
        self.assertEqual(tail[812:816], b"1204")
        self.assertEqual(tail[817:821], b"5201")

    def test_drain_frames_consumes_complete_frames(self):
        from tools.aion_protocol import encode_varint
        body = b"\x11\x36" + b"\x00" * 8
        wire = encode_varint(len(body) + 4) + body + b"\x99\x02"
        buffer = bytearray(wire)
        frames = drain_frames(buffer)
        self.assertEqual(len(frames), 1)
        self.assertEqual(bytes(buffer), b"\x99\x02")  # partial tail retained

    def test_name_check_available_layout(self):
        body = build_name_check("Player1")
        self.assertEqual(body[:2], b"\x1a\x36")
        self.assertEqual(int.from_bytes(body[2:4], "little"), 0)  # available
        self.assertEqual(body[4], 7)
        self.assertEqual(body[5:12], b"Player1")
        self.assertEqual(body[12], 1)

    def test_name_check_taken_code(self):
        self.assertEqual(build_name_check("x", taken=True)[2:4], b"\x01\x00")

    def test_followup_routes_name_check(self):
        request = b"\x19\x36\x07Player1\x01"
        replies = game_followup_replies(request, now_ms=1000)
        self.assertEqual(len(replies), 1)
        self.assertEqual(replies[0], build_name_check("Player1"))

    def test_followup_ignores_malformed_name_check(self):
        replies = game_followup_replies(b"\x19\x36\x05Addi", now_ms=1000)
        self.assertEqual(replies, [])

    def test_char_create_request_parses_schema(self):
        body = (b"\x1b\x36\x07Player1\x01"
                + (1).to_bytes(4, "little")          # _pc_creation_id
                + b"\x02" + b"\xaa\xbb"              # customizing count=2
                + b"\x03"                            # _voice_type
                + b"\x00"                            # _slot_no
                + b"\x01")                           # _skip_tutorial
        request = _game_char_create_request(body)
        self.assertIsNotNone(request)
        self.assertEqual(request["name"], "Player1")
        self.assertEqual(request["gender"], 1)
        self.assertEqual(request["pc_creation_id"], 1)
        self.assertEqual(request["customizing_data"], b"\xaa\xbb")
        self.assertEqual(request["voice_type"], 3)
        self.assertEqual(request["slot_no"], 0)

    def test_char_create_request_rejects_truncated(self):
        self.assertIsNone(_game_char_create_request(b"\x1b\x36\x07Player1"))

    def test_followup_routes_char_create(self):
        body = (b"\x1b\x36\x07Player1\x01"
                + (1).to_bytes(4, "little")
                + b"\x02" + b"\xaa\xbb"
                + b"\x03\x00\x01")
        replies = game_followup_replies(body, now_ms=1791465861099)
        self.assertEqual(len(replies), 1)
        reply = replies[0]
        self.assertEqual(reply[:2], b"\x1c\x36")
        self.assertEqual(int.from_bytes(reply[2:6], "little", signed=True), 0)

    def test_lobby_char_desc_round_trips_name_and_pos(self):
        record = _lobby_char_desc(key=1, nickname="Player1", pc_id=7,
                                  gender=2, position=(1.5, -2.5, 3.0),
                                  zone_id=1204)
        from tools.aion_protocol import Reader, Incomplete
        reader = Reader(record)
        self.assertEqual(reader.uint(4), 1)           # _key
        self.assertEqual(reader.take(reader.uint(1)), b"Player1")
        self.assertEqual(reader.uint(4), 7)           # _pc_id
        self.assertEqual(reader.uint(1), 1)           # _race
        self.assertEqual(reader.uint(1), 2)           # _gender
        self.assertEqual(reader.uint(4), 1)           # _level
        self.assertEqual(reader.uint(4), 0)           # _conqueror_level
        self.assertEqual(reader.uint(4), 0)           # _equip_item_level
        self.assertEqual(reader.uint(8), 0)           # _exp
        self.assertEqual(reader.uint(4), 1)           # _combat_level
        self.assertEqual(reader.uint(8), 0)           # _combat_exp
        self.assertEqual(reader.take(reader.uint(1)), b"")  # _guild_name
        self.assertEqual(reader.uint(8), 0)           # _logout_time
        pos = tuple(reader.uint(4) for _ in range(3))  # raw floats, bytes
        self.assertEqual(reader.uint(4), 1204)        # _zone_id

    def test_character_created_layout(self):
        body = build_character_created("Player1", gender=1, now_ms=1791465861099,
                                       customizing_data=b"\xaa\xbb")
        self.assertEqual(body[:2], b"\x1c\x36")
        self.assertEqual(int.from_bytes(body[2:6], "little", signed=True), 0)
        # nickname follows the character's _key int32 at offset 6
        self.assertEqual(body[10], 7)
        self.assertEqual(body[11:18], b"Player1")

    def test_select_character_request_parses_observed_layout(self):
        # Live 0x361f body: tag u16 | LEB128 key 0x80 0x80 0x04 (65536)
        # | i32 map_id | Vec3 f32 x3, all zero.
        body = (b"\x1f\x36\x80\x80\x04"
                + (0).to_bytes(4, "little")
                + struct.pack("<3f", 0.0, 0.0, 0.0))
        request = _game_select_character_request(body)
        self.assertIsNotNone(request)
        self.assertEqual(request["key"], 65536)
        self.assertEqual(request["map_id"], 0)
        self.assertEqual(request["position"], (0.0, 0.0, 0.0))

    def test_select_character_request_rejects_truncated(self):
        self.assertIsNone(_game_select_character_request(b"\x1f\x36\x80"))

    def test_followup_routes_select_character(self):
        body = (b"\x1f\x36\x80\x80\x04"
                + (0).to_bytes(4, "little")
                + struct.pack("<3f", 0.0, 0.0, 0.0))
        replies = game_followup_replies(body, now_ms=1791465861099)
        self.assertEqual(len(replies), 2)
        reply = replies[0]
        self.assertEqual(reply[:2], b"\x20\x36")
        self.assertEqual(int.from_bytes(reply[2:4], "little"), 0)  # _result u16
        self.assertEqual(reply[4:7], b"\x80\x80\x04")  # _key echo, varint

    def test_select_character_layout(self):
        body = build_select_character(key=65536, dbid=7, char_key=9)
        self.assertEqual(body[:2], b"\x20\x36")
        self.assertEqual(int.from_bytes(body[2:4], "little"), 0)  # _result u16
        self.assertEqual(body[4:7], b"\x80\x80\x04")  # _key varint 65536
        self.assertEqual(int.from_bytes(body[7:11], "little", signed=True), 1)  # _last_using_pc_id
        self.assertEqual(int.from_bytes(body[11:19], "little", signed=True), 7)  # _dbid
        self.assertEqual(int.from_bytes(body[24:28], "little", signed=True), 9)  # older char key after 5 zero bytes

    def test_select_preserves_created_character_customization(self):
        customization = bytes(range(256)) + bytes(73)
        state = {"character": {"pc_creation_id": 1, "gender": 1,
                               "customizing_data": customization}}
        replies = game_followup_replies(b"\x1f\x36\x01" + bytes(16), state=state)
        body = replies[0]
        self.assertEqual(int.from_bytes(body[5:9], "little"), 5)
        reader = Reader(body)
        reader.take(26)
        self.assertEqual(reader.varuint(), 0)  # options
        self.assertEqual(reader.varuint(), 0)  # skill customization
        self.assertEqual(reader.take(reader.varuint()), customization)

    def test_prepare_enter_map_layout(self):
        body = build_prepare_enter_map(
            enter_map_ticket=1, map_id=100001, game_time=1461135,
            position=(-100938.4765625, 54664.25, 24089.0),
            direction=-1.3943467820354272e-06, cutscene_name="Cutscene_L_A_QM_1001")
        self.assertEqual(body[:2], b"\x21\x36")
        self.assertEqual(int.from_bytes(body[2:6], "little", signed=True), 1)    # ticket
        self.assertEqual(int.from_bytes(body[6:10], "little", signed=True), 100001)  # map_id
        self.assertEqual(int.from_bytes(body[10:18], "little", signed=True), 1461135)
        pos = struct.unpack("<3f", body[18:30])
        self.assertAlmostEqual(pos[0], -100938.4765625, places=2)
        self.assertEqual(body[34], 0)   # reason u8
        self.assertEqual(body[35], 0)   # env_state u8
        self.assertEqual(body[44], 0)   # empty group_env_states count
        self.assertEqual(body[45], 0)   # teleport_effect_id u8
        self.assertEqual(body[46], 20)  # cutscene FString length
        self.assertEqual(body[47:67], b"Cutscene_L_A_QM_1001")
        self.assertEqual(body[67], 0)   # inform_type u8

    def test_prepare_enter_map_matches_official_wire(self):
        body = build_prepare_enter_map(
            enter_map_ticket=1, map_id=100001, game_time=1461135,
            position=(-100938.4765625, 54664.25, 24089.0),
            direction=-1.3943467820354272e-06, cutscene_name="Cutscene_L_A_QM_1001")
        self.assertEqual(body.hex(),
                         "213601000000a18601008f4b1600000000003d25c5c740885547"
                         "0032bc466425bbb5000000000000000000000000144375747363"
                         "656e655f4c5f415f514d5f3130303100")

    def test_load_complete_layout(self):
        body = build_load_complete(enter_map_ticket=0,
                                   position=(-100938.4765625, 54664.25, 24089.0),
                                   frame=2, char_key=13701)
        self.assertEqual(body[:2], b"\x23\x36")
        self.assertEqual(int.from_bytes(body[2:4], "little"), 0)       # _result u16
        self.assertEqual(int.from_bytes(body[4:8], "little", signed=True), 0)   # ticket i32
        pos = struct.unpack("<3f", body[8:20])
        self.assertAlmostEqual(pos[0], -100938.4765625, places=2)
        self.assertEqual(int.from_bytes(body[20:24], "little", signed=True), 2)  # frame i32
        self.assertEqual(int.from_bytes(body[24:26], "little"), 13701)  # char_key u16

    def test_load_complete_matches_official_wire(self):
        body = build_load_complete(enter_map_ticket=0,
                                   position=(-100938.4765625, 54664.25, 24089.0),
                                   frame=2, char_key=13701)
        self.assertEqual(body.hex(), "23360000000000003d25c5c7408855470032bc46020000008535")

    def test_load_complete_request_parses_ticket(self):
        # 0x3622 LoadComplete_RQ: tag | ticket i32 | bool u8 | Vec3 | ...
        body = b"\x22\x36" + (1).to_bytes(4, "little", signed=True) + b"\x00" + b"\x00" * 20
        request = _game_load_complete_request(body)
        self.assertIsNotNone(request)
        self.assertEqual(request["enter_map_ticket"], 1)

    def test_followup_routes_load_complete(self):
        body = b"\x22\x36" + (7).to_bytes(4, "little", signed=True) + b"\x00" + b"\x00" * 20
        replies = game_followup_replies(body, now_ms=1791465861099)
        self.assertEqual(len(replies), 9 + len(world_npcs.data()['maps']['1000']))
        self.assertEqual(replies[0][:2], b"\x23\x36")
        self.assertEqual(int.from_bytes(replies[0][4:8], "little", signed=True), 7)

    def test_followup_select_character_sends_prepare_enter_map(self):
        body = (b"\x1f\x36\x80\x80\x04"
                + (0).to_bytes(4, "little")
                + struct.pack("<3f", 0.0, 0.0, 0.0))
        replies = game_followup_replies(body, now_ms=1791465861099)
        self.assertEqual(len(replies), 2)
        self.assertEqual(replies[0][:2], b"\x20\x36")
        prepare = replies[1]
        self.assertEqual(prepare[:2], b"\x21\x36")
        self.assertEqual(int.from_bytes(prepare[6:10], "little", signed=True), 1000)
        # Empty FString is one zero length byte, then inform_type 0.
        self.assertEqual(prepare[46], 0)
        self.assertEqual(prepare[47], 0)
        self.assertEqual(len(prepare), 48)
        pos = struct.unpack("<3f", prepare[18:30])
        self.assertAlmostEqual(pos[0], 7259.90771484375, places=2)
        self.assertAlmostEqual(pos[1], -18223.455078125, places=2)
        self.assertAlmostEqual(pos[2], 22404.498046875, places=2)

    def test_my_char_appear_decodes_entire_packet(self):
        for weapon in (None, 110160001, 110160008):
            for hp in (119, 1500, 100000):
                custom = bytes(range(256)) + bytes(73)
                body = build_my_char_appear(name='Hero', weapon_id=weapon,
                                            hp=hp, hp_max=hp, customizing_data=custom,
                                            voice_type=2, combat_power=876032)
                decoded = decode_spawn(body)
                self.assertEqual(decoded['hp'], hp)
                self.assertEqual(decoded['hp_max'], hp)
                self.assertEqual(decoded['live'], 1)
                self.assertEqual((decoded['mp'], decoded['mp_max']), (200, 200))
                self.assertEqual(decoded['map_id'], 1000)
                self.assertEqual(decoded['customizing'], custom)
                self.assertEqual(decoded['voice'], 2)
                self.assertFalse(decoded['purchased'])
                self.assertEqual(decoded['power'], (876032, 876032))
                self.assertEqual(decoded['equips'],
                                 [(weapon, 1, False, True, True, True)] if weapon else [])

    def test_spawn_routes_created_customization_and_weapon(self):
        custom = bytes(range(256)) + bytes(73)
        state = {'character': {'name': 'Hero', 'gender': 1,
                              'pc_creation_id': 1, 'customizing_data': custom}}
        request = bytes.fromhex('22360700000000') + bytes(20)
        decoded = decode_spawn(game_followup_replies(request, now_ms=1, state=state)[1])
        self.assertEqual(decoded['customizing'], custom)
        self.assertEqual(decoded['equips'][0][0], state['equipped_weapon'])

    def test_combat_requests_receive_result_acknowledgements(self):
        state = {}
        self.assertEqual(game_followup_replies(b'\x3a\x38', state=state, now_ms=1000),
                         [b'\x3b\x38\x00\x00', b'\x21\x8d\x01\x00\x01'])
        self.assertTrue(state['combat_requested'])
        # 0x383c only acknowledges; leaving combat waits for the timeout.
        self.assertEqual(game_followup_replies(b'\x3c\x38', state=state, now_ms=1010),
                         [b'\x3d\x38\x00\x00'])
        self.assertFalse(state['combat_requested'])
        # A later swing extends combat without re-entering it.
        self.assertEqual(game_followup_replies(b'\x3a\x38', state=state, now_ms=3000),
                         [b'\x3b\x38\x00\x00'])
        self.assertEqual(combat_state_tick(state, 3000 + COMBAT_TIMEOUT_MS - 1), [])
        self.assertEqual(combat_state_tick(state, 3000 + COMBAT_TIMEOUT_MS), [b'\x21\x8d\x01\x00\x00'])
        self.assertEqual(combat_state_tick(state, 9999), [])

    def test_live_load_then_regeneration_without_explicit_timestamp(self):
        from unittest.mock import patch
        from tools.starter_casts import regenerate_resources, cast_control_replies
        state = {}
        request = bytes.fromhex('22360700000000') + bytes(20)
        # handle() omits now_ms. Exercise that path rather than supplying the
        # deterministic timestamp used by the packet-layout tests.
        with patch('tools.game_server.time.time', return_value=1234.5):
            replies = game_followup_replies(request, state=state)
        self.assertEqual(replies[1][:2], b'\x33\x36')
        self.assertEqual(state['resource_tick_ms'], 1234500)
        self.assertEqual(regenerate_resources(state, 1234550), [])
        skill = (bytes.fromhex('00380101') + struct.pack('<i', 11010000)
                 + bytes.fromhex('0201') + struct.pack('<4f', 0, 1, 2, 3)
                 + b'\x02' + struct.pack('<q', 1234500))
        cast_control_replies(skill, state, 1234600)
        self.assertEqual(state['resources']['mp'], 80)
        self.assertEqual(len(regenerate_resources(state, 1235500)), 1)
        self.assertEqual(state['resources']['mp'], 105)

    def test_spawn_detail_matches_official_structure(self):
        sample = Path(__file__).resolve().parents[1] / "research/local/tooling/mycharappear-official.bin"
        if not sample.exists():
            self.skipTest("official local research fixture unavailable")
        official = sample.read_bytes()
        # Compare actual evidence, including counts/markers, rather than only
        # checking that padding places position at the desired byte offset.
        for key, name, detail_start in ((15590, "abcdefghijkl", 66), (1, "", 53)):
            with self.subTest(key=key, name=name):
                body = build_my_char_appear(key=key, name=name)
                self.assertEqual(body[detail_start:detail_start + 118], official[66:184])
                self.assertEqual(struct.unpack_from("<3f", body, detail_start + 120),
                                 (7259.90771484375, -18223.455078125, 22404.498046875))

    def test_char_barrier_layout(self):
        body = build_char_barrier()
        self.assertEqual(body, b"\x46\x36\x01\x00")

    def test_char_stats_update_layout(self):
        body = build_char_stats_update()
        self.assertEqual(body, b"\x4a\x36\x01\x00" + b"\x00" * 8)

    def test_mentoring_role_update_layout(self):
        body = build_mentoring_role_update()
        self.assertEqual(body, b"\x57\x36\x01" + b"\x00" * 8)

    def test_contents_unlock_list_matches_official_body(self):
        # Official recording 2026-10-13, message 215 (fresh Light character): 8 types incl. Chat (45).
        from tools.game_server import build_contents_unlock_list
        self.assertEqual(build_contents_unlock_list((17, 42, 45, 50, 52, 56, 61, 62)).hex(),
                         "57e208112a2d3234383d3e")

    def test_followup_load_complete_sends_appear_burst(self):
        body = b"\x22\x36" + (7).to_bytes(4, "little", signed=True) + b"\x00" + b"\x00" * 20
        replies = game_followup_replies(body, now_ms=1791465861099)
        self.assertEqual([r[:2] for r in replies[:7]],
                         [b"\x23\x36", b"\x33\x36", b"\x46\x36",
                          b"\x4a\x36", b"\x57\x36", b"\x00\x51", b"\x00\x56"])
        # Then the content-unlock list (chat etc.), the training dummy and every
        # NPC spawn of the map (0x3641).
        self.assertEqual(replies[7][:2], b"\x57\xe2")
        self.assertEqual({r[:2] for r in replies[8:]}, {b"\x41\x36"})
        self.assertEqual(len(replies) - 8, 1 + len(world_npcs.data()['maps']['1000']))
        self.assertEqual(int.from_bytes(replies[0][4:8], "little", signed=True), 7)
        # Load-complete position is the same spawn point as prepare and appear.
        pos = struct.unpack("<3f", replies[0][8:20])
        self.assertAlmostEqual(pos[0], 7259.90771484375, places=2)
        self.assertAlmostEqual(pos[1], -18223.455078125, places=2)
        self.assertAlmostEqual(pos[2], 22404.498046875, places=2)
        # 0x364a ceiling matches appear HP. A zero ceiling clears the bar.
        self.assertEqual(int.from_bytes(replies[3][-8:], "little", signed=True), 1500)

    def test_followup_char_all_stats_uses_spawn_hp(self):
        replies = game_followup_replies(b"\x48\x36", now_ms=1)
        self.assertEqual(len(replies), 1)
        body = replies[0]
        self.assertEqual(body[:2], b"\x49\x36")
        self.assertEqual(int.from_bytes(body[2:4], "little"), 0)
        count = body[4]
        stats = {}
        for index in range(count):
            entry = body[5 + index * 6:11 + index * 6]
            stat_type = int.from_bytes(entry[:2], "little")
            stat_value = int.from_bytes(entry[2:], "little", signed=True)
            stats[stat_type] = stat_value
        self.assertEqual(stats[93], 1500)
        self.assertEqual(stats[193], 1500)
        self.assertEqual(int.from_bytes(body[-8:], "little", signed=True), 1500)


if __name__ == "__main__":
    unittest.main()
