import unittest

from tools.login_server import (DEFAULT_WORLDS, LoginServerError, build_account,
                                build_characters, build_ping, build_pre_generated_intro,
                                build_redirect, build_select_result,
                                build_world_directory, followup_replies, frame,
                                online_group_mask)
from tools.probe_login_reply import RC4
from tools.aion_protocol import Reader, split_frames, world_list


def parse_one(wire):
    frames, consumed = split_frames(wire)
    assert consumed == len(wire) and len(frames) == 1, "expected exactly one frame"
    return frames[0]


class LoginServerFrameTests(unittest.TestCase):
    def test_frame_prefix_is_plaintext_canonical_varint(self):
        body = b"\x03\x39\x00\x00" + b"\x01" * 16
        wire = frame(body)
        self.assertEqual(wire[0], len(body) + 4)
        self.assertEqual(wire[1:], body)

    def test_account_round_trips_header_and_sizes(self):
        body = build_account("123456", "123e4567-e89b-12d3-a456-426614174000", 1301)
        self.assertEqual(len(body), 101)
        self.assertEqual(int.from_bytes(body[:2], "little"), 0x3906)
        reader = Reader(body)
        self.assertEqual(reader.uint(2), 0x3906)
        self.assertEqual(reader.uint(2), 0)
        self.assertEqual(reader.uint(1), 0)
        self.assertEqual(reader.take(reader.uint(1)).decode(), "123e4567-e89b-12d3-a456-426614174000")
        self.assertEqual(reader.take(3), b"\x01\x00\x00")
        combined = reader.take(reader.uint(1)).decode()
        self.assertTrue(combined.startswith("123456:123e4567-e89b-12d3-a456-426614174000"))
        server = reader.uint(2)
        self.assertEqual(server, 1301)
        self.assertEqual(reader.take(5), b"\x01\x00\x00\x00\x00")
        self.assertEqual(reader.uint(2), 1301)
        self.assertEqual(reader.take(3), b"\x03\x09\x03")
        self.assertEqual(reader.offset, len(body))

    def test_account_rejects_bad_inputs(self):
        with self.assertRaises(LoginServerError):
            build_account("not-a-number", "123e4567-e89b-12d3-a456-426614174000", 1)
        with self.assertRaises(LoginServerError):
            build_account("1", "not-a-guid", 1)
        with self.assertRaises(LoginServerError):
            build_account("1", "123e4567-e89b-12d3-a456-426614174000", 0x10000)

    def test_directory_builder_round_trips_through_parser(self):
        entries = [
            {"id": 1301, "group": 1, "name": "AionDev_Light_001", "status": 0, "load": 42},
            {"id": 2307, "group": 2, "name": "AionDev_Dark_007", "status": 4, "load": 5},
            {"id": 1302, "group": 1, "name": "AionDev_Light_002"},
            {"id": 2308, "group": 2, "name": "AionDev_Dark_008"},
        ]
        body = build_world_directory(entries)
        parsed = world_list(body)
        self.assertEqual(parsed["count"], 4)
        self.assertEqual([w["name"] for w in parsed["worlds"]],
                         [e["name"] for e in entries])
        self.assertEqual(parsed["worlds"][0]["load"], 42)
        self.assertEqual(parsed["worlds"][1]["status"], 4)
        self.assertEqual(parsed["trailer_hex"], "0101")

    def test_directory_mask_byte_marks_every_present_row(self):
        entries = [{"id": i + 1, "group": 1, "name": f"W{i}"} for i in range(9)]
        body = build_world_directory(entries)
        parsed = world_list(body)
        self.assertEqual(parsed["count"], 9)
        self.assertTrue(all(w["group_mask_bits"] == 1 for w in parsed["worlds"]))
        self.assertEqual(online_group_mask(4, 0), 0x55)
        self.assertEqual(online_group_mask(2, 0), 0x05)
        self.assertEqual(online_group_mask(9, 8), 0x01)

    def test_default_worlds_use_unnamed_europe_ids(self):
        body = build_world_directory(DEFAULT_WORLDS)
        parsed = world_list(body)
        self.assertEqual([(w["id_candidate"], w["name"], w["group_mask_bits"])
                          for w in parsed["worlds"]],
                         [(1361, "PotatoeWorld", 1), (2361, "PotatoeWorld", 1)])

    def test_directory_rejects_bad_rows(self):
        with self.assertRaises(LoginServerError):
            build_world_directory([{"id": 1, "group": 3, "name": "W"}])
        with self.assertRaises(LoginServerError):
            build_world_directory([{"id": 0x10000, "group": 1, "name": "W"}])
        with self.assertRaises(LoginServerError):
            build_world_directory([])

    def test_characters_empty_list_header_and_trailer(self):
        body = build_characters([])
        self.assertEqual(len(body), 7)
        reader = Reader(body)
        self.assertEqual(reader.uint(2), 0x390b)
        self.assertEqual(reader.uint(2), 0)
        self.assertEqual(reader.uint(1), 0)
        self.assertEqual(reader.take(2), b"\x01\x01")
        self.assertEqual(reader.offset, len(body))

    def test_characters_rejects_bad_rows(self):
        with self.assertRaises(LoginServerError):
            build_characters([{"server_id": 0x10000, "name": "X"}])
        with self.assertRaises(LoginServerError):
            build_characters([{"server_id": 1, "name": ""}])
        with self.assertRaises(LoginServerError):
            build_characters([{"server_id": 1, "name": "X", "level": 2 ** 32}])

    def test_characters_row_includes_zero_u32_between_exp_and_code(self):
        # Canonical a2kit layout: u16 server | name | u32 level | u32 exp |
        # u32 0 | u32 code | u8 flags | u64 played | 8 zeros | mask byte.
        body = build_characters([{"server_id": 1204, "name": "Abc", "level": 3,
                                  "exp": 0, "code": 6, "flags": 1,
                                  "last_played_unix_ms": 1700000000000}])
        reader = Reader(body)
        self.assertEqual(reader.uint(2), 0x390b)
        self.assertEqual(reader.uint(2), 0)        # result
        self.assertEqual(reader.uint(1), 1)        # count
        self.assertEqual(reader.uint(2), 1204)     # server id
        self.assertEqual(reader.take(reader.uint(1)).decode(), "Abc")
        self.assertEqual(reader.uint(4), 3)        # level
        self.assertEqual(reader.uint(4), 0)        # exp
        self.assertEqual(reader.uint(4), 0)        # zero u32 before code
        self.assertEqual(reader.uint(4), 6)        # code
        self.assertEqual(reader.uint(1), 1)        # flags
        self.assertEqual(reader.uint(8), 1700000000000)
        self.assertEqual(reader.take(8), b"\x00" * 8)
        self.assertEqual(reader.uint(1), 0)        # group mask byte
        self.assertEqual(reader.take(2), b"\x01\x01")
        self.assertEqual(reader.offset, len(body))

    def test_pre_generated_intro_is_dollar_placeholder(self):
        rows = build_pre_generated_intro(1204, now_ms=1700000000000)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["server_id"], 1204)
        self.assertEqual(len(rows[0]["name"]), 12)
        self.assertTrue(rows[0]["name"].startswith("$"))
        self.assertEqual(rows[0]["level"], 1)
        self.assertEqual(rows[0]["flags"], 2)
        self.assertEqual(rows[0]["last_played_unix_ms"], 1700000000000)

    def test_followup_replies_select_request(self):
        worlds = [{"id": 1, "group": 1, "name": "W"}]
        body = b"\x0c\x39\x01\x00"  # 0x390c SelectServer_RQ, world id 1
        replies, directory = followup_replies(
            body, worlds, "42", "123e4567-e89b-12d3-a456-426614174000", 1)
        self.assertFalse(directory)
        self.assertEqual([int.from_bytes(r[:2], "little") for r in replies],
                         [0x390d])
        reader = Reader(replies[0])
        self.assertEqual(reader.uint(2), 0x390d)
        self.assertEqual(reader.uint(2), 0)     # result field
        self.assertEqual(reader.uint(1), 0)     # zero byte
        self.assertEqual(reader.uint(2), 1)     # echoed world id

    def test_followup_replies_move_request(self):
        worlds = [{"id": 1, "group": 1, "name": "W"}]
        body = b"\x0e\x39\x01\x00"  # 0x390e MoveToServer_RQ, world id 1
        replies, directory = followup_replies(
            body, worlds, "42", "123e4567-e89b-12d3-a456-426614174000", 1,
            redirect_host="127.0.0.1", redirect_port=13328)
        self.assertFalse(directory)
        self.assertEqual([int.from_bytes(r[:2], "little") for r in replies],
                         [0x390f])
        reader = Reader(replies[0])
        self.assertEqual(reader.uint(2), 0x390f)
        self.assertEqual(reader.uint(2), 0)              # result field
        self.assertEqual(reader.uint(2), 1)              # world id
        size = reader.varuint()
        self.assertEqual(reader.take(size).decode("ascii"), "127.0.0.1")
        self.assertEqual(reader.uint(2), 13328)

    def test_redirect_builder_round_trips_through_parser(self):
        from tools.aion_protocol import handoff_response
        body = build_redirect(1204, "193.202.112.51", 13328)
        parsed = handoff_response(body)
        self.assertEqual(parsed["result_field"], 0)
        self.assertEqual(parsed["unknown_u16"], 1204)
        self.assertEqual(parsed["destination_address"], "193.202.112.51")
        self.assertEqual(parsed["destination_port"], 13328)

    def test_redirect_rejects_bad_inputs(self):
        with self.assertRaises(LoginServerError):
            build_redirect(1, "short", 13328)   # 5 bytes, below observed bounds
        with self.assertRaises(LoginServerError):
            build_redirect(1, "127.0.0.1", 0)   # port 0

    def test_ping_echoes_counter(self):
        body = build_ping(0x1122334455667788, now_ms=1700000000000)
        self.assertEqual(len(body), 20)
        reader = Reader(body)
        self.assertEqual(reader.uint(2), 0x3903)
        self.assertEqual(reader.uint(2), 0)
        self.assertEqual(reader.uint(8), 0x1122334455667788)
        self.assertEqual(reader.uint(8), 1700000000000)

    def test_followup_replies_login_request(self):
        worlds = [{"id": 5, "group": 1, "name": "W"}]
        body = b"\x05\x39" + b"\x00" * 20  # 0x3905 login request
        replies, directory = followup_replies(
            body, worlds, "42", "123e4567-e89b-12d3-a456-426614174000", 5)
        self.assertFalse(directory)
        self.assertEqual([int.from_bytes(r[:2], "little") for r in replies],
                         [0x3906])

    def test_followup_replies_characters_request(self):
        worlds = [{"id": 5, "group": 1, "name": "W"}]
        body = b"\x0a\x39"  # 0x390a ListMyCharacters_RQ, minimal body
        replies, directory = followup_replies(
            body, worlds, "42", "123e4567-e89b-12d3-a456-426614174000", 5,
            characters=[{"server_id": 5, "name": "Dev_01", "level": 1, "exp": 0,
                         "code": 5, "flags": 1, "last_played_unix_ms": 0}])
        self.assertFalse(directory)
        self.assertEqual([int.from_bytes(r[:2], "little") for r in replies],
                         [0x390b])
        reader = Reader(replies[0])
        self.assertEqual(reader.uint(2), 0x390b)
        self.assertEqual(reader.uint(2), 0)        # result field
        self.assertEqual(reader.uint(1), 1)        # character count
        self.assertEqual(reader.uint(2), 5)        # server id
        self.assertEqual(reader.take(reader.uint(1)).decode(), "Dev_01")
        self.assertEqual(reader.uint(4), 1)        # level
        self.assertEqual(reader.uint(4), 0)        # exp
        self.assertEqual(reader.uint(4), 0)        # zero u32 between exp and code
        self.assertEqual(reader.uint(4), 5)        # code
        self.assertEqual(reader.uint(1), 1)        # flags
        self.assertEqual(reader.uint(8), 0)        # last played
        self.assertEqual(reader.take(8), b"\x00" * 8)
        self.assertEqual(reader.uint(1), 0)        # group mask byte
        self.assertEqual(reader.take(2), b"\x01\x01")

    def test_followup_replies_directory_request(self):
        worlds = [{"id": 5, "group": 1, "name": "W"}]
        body = b"\x08\x39" + b"\x00" * 4  # 0x3908 ListServer_RQ
        replies, directory = followup_replies(
            body, worlds, "42", "123e4567-e89b-12d3-a456-426614174000", 5)
        self.assertTrue(directory)
        self.assertEqual([int.from_bytes(r[:2], "little") for r in replies],
                         [0x3909])

    def test_followup_replies_heartbeat(self):
        worlds = [{"id": 5, "group": 1, "name": "W"}]
        # 0x3902 heartbeat: u16 header + u64 counter (10-byte body)
        body = b"\x02\x39" + (0xDEAD).to_bytes(8, "little")
        replies, directory = followup_replies(
            body, worlds, "42", "123e4567-e89b-12d3-a456-426614174000", 5,
            now_ms=1700000000000)
        self.assertFalse(directory)
        self.assertEqual(len(replies), 1)
        reader = Reader(replies[0])
        self.assertEqual(reader.uint(2), 0x3903)
        self.assertEqual(reader.uint(2), 0)          # result field
        self.assertEqual(reader.uint(8), 0xDEAD)     # echoed counter
        self.assertEqual(reader.uint(8), 1700000000000)

    def test_followup_replies_unknown_header_returns_nothing(self):
        worlds = [{"id": 5, "group": 1, "name": "W"}]
        replies, directory = followup_replies(
            b"\x39\x00", worlds, "42",
            "123e4567-e89b-12d3-a456-426614174000", 5)
        self.assertEqual(replies, [])
        self.assertFalse(directory)


class LoginServerStreamTests(unittest.TestCase):
    def test_server_reply_round_trips_rc4_body_stream(self):
        # Simulate the continuing body-only RC4 alignment end to end.
        key = b"local-login-server-key-32-bytes-0000000"
        worlds = [{"id": 1, "group": 1, "name": "W"}]
        plain_login = b"\x05\x39" + b"\x00" * 18
        plain_heartbeat = b"\x02\x39" + (7).to_bytes(8, "little")
        cipher = RC4(key)
        wire = b""
        for body in (plain_login, plain_heartbeat):
            encrypted = cipher.crypt(body)
            wire += frame(encrypted)
        # Decrypt server-side exactly as handle() does.
        server_cipher = RC4(key)
        decrypted = []
        for inbound in split_frames(wire)[0]:
            decrypted.append(server_cipher.crypt(inbound.body))
        self.assertEqual(decrypted, [plain_login, plain_heartbeat])
        # And the decrypted headers should route to replies.
        self.assertEqual([int.from_bytes(d[:2], "little") for d in decrypted],
                         [0x3905, 0x3902])


if __name__ == "__main__":
    unittest.main()
