"""Loopback game-server handshake probe for the original AION2 client.

The login server's 0x390f redirect hands the client to this listener. The
game opening exchange mirrors the login one but with distinct fields:

- Client 0x3610 greeting: same DER RSAPublicKey (2048-bit, e=3) plus an
  11-byte trailer (vs 5 on login).
- Server 0x3611 reply: tag u16 | result u16=0 | param u16=60000 | extra u16=0
  | varint len=256 | RSA-OAEP-SHA1(32-byte key) | 12 trailer bytes.

After the reply the client may transform its follow-up bodies with the same
continuing body-only RC4 as the login channel, or with a scheme not yet
established. This probe records raw follow-up and, because the injected key
is known, tries the login RC4 alignment and reports the decrypted headers.

Constraints: loopback only, never logs key material, never replays official
capture bytes. All key and reply data here is locally generated.
"""
import argparse
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import socket
import struct
import time

try:
    from .starter_skills import starter_profile, build_skill_list, build_skill_slots
    from .class_skills import learned_skills, clamp_level
    from .combat import TRAINING_DUMMY_KEY, training_dummy, build_npc_appear, build_combat_state, Target
    from .world_npcs import map_targets
    from .starter_equipment import starter_weapon, build_equipped_inventory
    from .starter_casts import cast_control_replies, regenerate_resources, respawn_targets
    from .npc_ai import npc_ai_tick
    from .run_diagnostics import record_exchange
    from .aion_protocol import Incomplete, Reader, encode_varint, split_frames, lz4_block_encode
    from .probe_login_reply import RC4, read_greeting, rsa_public_from_hello, rsa_pub_encrypt
except ImportError:
    from starter_skills import starter_profile, build_skill_list, build_skill_slots
    from class_skills import learned_skills, clamp_level
    from combat import TRAINING_DUMMY_KEY, training_dummy, build_npc_appear, build_combat_state, Target
    from world_npcs import map_targets
    from starter_equipment import starter_weapon, build_equipped_inventory
    from starter_casts import cast_control_replies, regenerate_resources, respawn_targets
    from npc_ai import npc_ai_tick
    from run_diagnostics import record_exchange
    from aion_protocol import Incomplete, Reader, encode_varint, split_frames, lz4_block_encode
    from probe_login_reply import RC4, read_greeting, rsa_public_from_hello, rsa_pub_encrypt

ROOT = Path(__file__).resolve().parents[1]
HOST = "127.0.0.1"
DEFAULT_PORT = 13328


def local_port():
    """Port the login 0x390f redirect points at, from AION_REDIRECT_PORT or 13328."""
    return int(os.environ.get("AION_REDIRECT_PORT", DEFAULT_PORT))


def build_game_reply(modulus, exponent, plaintext, parameter=60000):
    """0x3611 game-server handshake reply; one extra u16 and two extra trailer bytes."""
    cipher = rsa_pub_encrypt(plaintext, modulus, exponent)
    body = b"\x11\x36" + b"\x00\x00" + parameter.to_bytes(2, "little") \
        + b"\x00\x00" \
        + encode_varint(len(cipher)) + cipher \
        + b"\x00" * 12
    logical = len(body) + 4
    return encode_varint(logical) + body


def build_ping_reply(counter, now_ms=None):
    """0x3603 game ping reply: echo the client counter plus the server clock."""
    if now_ms is None:
        now_ms = int(time.time() * 1000)
    if not 0 <= counter <= 0xFFFFFFFFFFFFFFFF:
        raise ValueError("Counter outside uint64")
    return (b"\x03\x36\x00\x00"
            + counter.to_bytes(8, "little")
            + now_ms.to_bytes(8, "little"))


def build_clock(now_ms=None):
    """0x3600 server clock notification: tag plus uint64 Unix milliseconds."""
    if now_ms is None:
        now_ms = int(time.time() * 1000)
    return b"\x00\x36" + now_ms.to_bytes(8, "little")


def build_name_check(name, taken=False):
    """0x361a name-check reply for a 0x3619 request (a2kit NameCheck).

    Layout: tag u16 0x361a | code u16 (0=available, nonzero=taken) |
    name (u8 len + UTF-8 bytes) | u8 1.
    """
    encoded = name.encode("utf-8")
    if not 1 <= len(encoded) <= 255:
        raise ValueError("Character name must be 1..255 UTF-8 bytes")
    return (b"\x1a\x36"
            + (1 if taken else 0).to_bytes(2, "little")
            + bytes([len(encoded)]) + encoded
            + b"\x01")


def frame(body):
    """Plaintext varint length prefix + body, the observed game-port framing."""
    return encode_varint(len(body) + 4) + body


def _fstring(text):
    """FString wire form: uint8 length + UTF-8 bytes (no NUL terminator)."""
    encoded = text.encode("utf-8")
    if not 0 <= len(encoded) <= 255:
        raise ValueError("String longer than uint8 FString")
    return bytes([len(encoded)]) + encoded


class PackedWriter:
    """Message writer whose bools share bytes, as in the official 0x3615.

    A bool takes the next bit of the current bool byte; when none is open or
    all eight bits are used, it reserves a new byte at its own position. The
    open byte carries across records (three characters share two bytes there).
    """

    def __init__(self, data=b""):
        self.out = bytearray(data)
        self._bool_at = None
        self._bits = 8

    def raw(self, data):
        self.out += data

    def i32(self, value):
        self.out += int(value).to_bytes(4, "little", signed=True)

    def i64(self, value):
        self.out += int(value).to_bytes(8, "little", signed=True)

    def u8(self, value):
        self.out.append(int(value) & 0xFF)

    def varint(self, value):
        self.out += encode_varint(int(value))

    def string(self, text):
        self.out += _fstring(text)

    def boolean(self, value):
        if self._bits == 8:
            self._bool_at = len(self.out)
            self.out.append(0)
            self._bits = 0
        if value:
            self.out[self._bool_at] |= 1 << self._bits
        self._bits += 1


def _lobby_char_desc(key, nickname, pc_id, race=1, gender=1, level=1,
                     conqueror_level=0, equip_item_level=0, exp=0,
                     combat_level=1, combat_exp=0, guild_name="",
                     logout_time=0, position=(0.0, 0.0, 0.0), zone_id=3016,
                     reg_time=0, pending_delete_time=0, equips=(),
                     dbid=0, block_reason_no=0, block_end_time=0, wing_id=0,
                     wing_skin_id=0, customizing_data=b"", voice_type=0,
                     pre_generated=False, item_count_list=(),
                     last_nickname_change_time=0, slot_no=0, combat_power=0,
                     writer=None):
    """Serialize DevPacketData_Common_LobbyCharDesc on the wire.

    Usmap field order; int32/int64 little-endian, u8-prefixed FString, one-byte
    enums, LEB128 array counts, bools bit-packed (PackedWriter). Verified by
    rebuilding the three records of the official 0x3615 byte-for-byte. Returns
    the bytes, or appends to `writer` (sharing its bool byte) when given.
    """
    w = writer if writer is not None else PackedWriter()
    w.i32(key)
    w.string(nickname)
    w.i32(pc_id)
    w.u8(race)
    w.u8(gender)
    w.i32(level)
    w.i32(conqueror_level)
    w.i32(equip_item_level)
    w.i64(exp)
    w.i32(combat_level)
    w.i64(combat_exp)
    w.string(guild_name)
    w.i64(logout_time)
    w.raw(struct.pack("<3f", *position))
    w.i32(zone_id)
    w.i64(reg_time)
    w.i64(pending_delete_time)
    w.varint(len(equips))
    for equip in equips:
        _equip_broad(w, *equip)
    w.i64(dbid)
    w.i32(block_reason_no)
    w.i64(block_end_time)
    w.i32(wing_id)
    w.i32(wing_skin_id)
    w.varint(len(customizing_data))
    w.raw(bytes(customizing_data))
    w.u8(voice_type)
    w.boolean(pre_generated)
    w.varint(len(item_count_list))
    for item_id, item_count, is_locked, period_expire_time in item_count_list:
        w.i32(item_id)
        w.i64(item_count)
        w.boolean(is_locked)
        w.i64(period_expire_time)
    w.i64(last_nickname_change_time)
    w.u8(slot_no)
    w.i64(combat_power)
    return bytes(w.out) if writer is None else None


def _equip_broad(w, item_id, enchant_level=0, visual_equip_slot=0,
                 period_expire_time=0, is_period_expired=False, skin_id=0,
                 deco_slots=(), godstone_slots=(), display=True,
                 default_mesh=True, override=True, fx_bit=14):
    """DevPacketData_Common_EquipBroad into a PackedWriter.

    Defaults are the official lobby record's starter greatsword: three empty
    godstone slots, display/default_mesh/override true, fx_bit varint 14.
    """
    w.i32(item_id)
    w.u8(enchant_level)
    w.u8(visual_equip_slot)
    w.i64(period_expire_time)
    w.boolean(is_period_expired)
    w.i32(skin_id)
    w.varint(len(deco_slots))
    for deco in deco_slots:
        (slot, r, g, b, remove, material_id, pattern_id, pr, pg, pb,
         pattern_remove, pattern_color_opacity, pattern_opacity,
         pattern_size_h, pattern_size_v, pattern_type, pattern_additional,
         pattern_glossy, pattern_metallic) = deco
        w.raw(bytes([slot, r, g, b]))
        w.boolean(remove)
        w.i32(material_id)
        w.i32(pattern_id)
        w.raw(bytes([pr, pg, pb]))
        w.boolean(pattern_remove)
        w.i32(pattern_color_opacity)
        w.i32(pattern_opacity)
        w.i32(pattern_size_h)
        w.i32(pattern_size_v)
        w.u8(pattern_type)
        w.boolean(pattern_additional)
        w.i32(pattern_glossy)
        w.i32(pattern_metallic)
    w.varint(len(godstone_slots))
    for godstone in godstone_slots:
        w.i32(godstone)
    w.boolean(display)
    w.boolean(default_mesh)
    w.boolean(override)
    w.varint(fx_bit)


# Community server key sent in Login_RS and CommServerKey_RS. The official
# Login_RS (2026-10-13) carried a four-digit key ("5301") next to server key
# "1315"; ours is chosen locally in the same shape.
COMM_SERVER_KEY = "5201"


def build_login_characters(characters, account_session, server_key, comm_server_key,
                           now_ms=None, local_offset_ms=7 * 3600 * 1000,
                           extended_slot_count=0, is_born_server=True, server_race=1,
                           chat_enable_level=30, user_flag=0):
    """0x3615 Login_RS with a real character list (usmap order, official layout).

    `characters` are keyword dicts for _lobby_char_desc. Rebuilds the official
    sample byte-for-byte; the session token is our "account:grant" string.
    Observed: result is u16, chat level a varint; local time = now + 7 h there.
    """
    if now_ms is None:
        now_ms = int(time.time() * 1000)
    w = PackedWriter(b"\x15\x36\x00\x00")
    w.i64(now_ms)
    w.i64(now_ms + local_offset_ms)
    w.varint(len(characters))
    for character in characters:
        _lobby_char_desc(**character, writer=w)
    w.u8(extended_slot_count)
    w.boolean(False)  # _reconnect
    w.boolean(False)  # _reconnect_to_lobby
    w.string(account_session)
    w.string(server_key)
    w.string(comm_server_key)
    w.boolean(is_born_server)
    w.u8(server_race)
    w.boolean(False)  # _is_admin
    w.varint(chat_enable_level)
    w.boolean(False)  # _subscribe_type_chatrace_enable
    w.u8(user_flag)
    return bytes(w.out)


def parse_login_characters(body):
    """Strict decoder for build_login_characters bodies; returns its keyword args.

    Rejects unread tails and the flags the builder fixes (reconnect, admin, ...).
    """
    if body[:4] != b"\x15\x36\x00\x00":
        raise ValueError("Expected a successful 0x3615")
    reader = Reader(body)
    reader.offset = 4
    bits = {"at": None, "n": 8}

    def boolean():
        if bits["n"] == 8:
            bits["at"], bits["n"] = reader.offset, 0
            reader.take(1)
        value = (body[bits["at"]] >> bits["n"]) & 1
        bits["n"] += 1
        return bool(value)

    def i32():
        return int.from_bytes(reader.take(4), "little", signed=True)

    def i64():
        return int.from_bytes(reader.take(8), "little", signed=True)

    def string():
        return reader.take(reader.uint(1)).decode("utf-8")

    now_ms, local_ms = i64(), i64()
    characters = []
    for _ in range(reader.varuint()):
        c = dict(key=i32(), nickname=string(), pc_id=i32(), race=reader.uint(1),
                 gender=reader.uint(1), level=i32(), conqueror_level=i32(),
                 equip_item_level=i32(), exp=i64(), combat_level=i32(), combat_exp=i64(),
                 guild_name=string(), logout_time=i64(),
                 position=struct.unpack("<3f", reader.take(12)), zone_id=i32(),
                 reg_time=i64(), pending_delete_time=i64())
        equips = []
        for _ in range(reader.varuint()):
            item, enchant, slot, expire = i32(), reader.uint(1), reader.uint(1), i64()
            expired, skin = boolean(), i32()
            if reader.varuint():
                raise ValueError("Dye slots are not decoded")
            godstones = tuple(i32() for _ in range(reader.varuint()))
            display, default_mesh, override = boolean(), boolean(), boolean()
            equips.append((item, enchant, slot, expire, expired, skin, (), godstones,
                           display, default_mesh, override, reader.varuint()))
        c.update(equips=tuple(equips), dbid=i64(), block_reason_no=i32(), block_end_time=i64(),
                 wing_id=i32(), wing_skin_id=i32(),
                 customizing_data=reader.take(reader.varuint()), voice_type=reader.uint(1),
                 pre_generated=boolean())
        c["item_count_list"] = tuple((i32(), i64(), boolean(), i64()) for _ in range(reader.varuint()))
        c.update(last_nickname_change_time=i64(), slot_no=reader.uint(1), combat_power=i64())
        characters.append(c)
    extended = reader.uint(1)
    if boolean() or boolean():
        raise ValueError("Reconnect logins are not decoded")
    session, server_key, comm_key = string(), string(), string()
    born, race = boolean(), reader.uint(1)
    if boolean():
        raise ValueError("Admin logins are not decoded")
    chat_level = reader.varuint()
    if boolean():
        raise ValueError("Chat race subscription is not decoded")
    flag = reader.uint(1)
    if reader.offset != len(body):
        raise ValueError("Unread 0x3615 tail")
    return dict(characters=characters, account_session=session, server_key=server_key,
                comm_server_key=comm_key, now_ms=now_ms, local_offset_ms=local_ms - now_ms,
                extended_slot_count=extended, is_born_server=born, server_race=race,
                chat_enable_level=chat_level, user_flag=flag)


def build_select_character(key=1, dbid=1, char_key=1, last_using_pc_id=1,
                           option_data=b"", skill_customizing=(),
                           customizing_data=b"", server_start_time=None):
    """0x3620 SelectCharacter_RS reply body.

    Schema: DevPacketData_GameServer_SelectCharacter_RS (_result IntProperty,
    _key IntProperty, _last_using_pc_id IntProperty, _dbid Int64Property,
    _char_key IntProperty, _option_data array<ByteProperty>,
    _skill_customizing_list array<DevPacketData_Common_SkillCustomizing>,
    _customizing_data array<ByteProperty>, _server_start_time Int64Property).
    The _key echoes the request, and the character record fields are generated
    from our own state; nothing is replayed from captures.
    """
    if server_start_time is None:
        server_start_time = int(time.time() * 1000)
    out = bytearray(b"\x20\x36")
    out += (0).to_bytes(2, "little")                       # _result u16 (official wire)
    out += encode_varint(int(key))                         # _key varint (official wire)
    out += int(last_using_pc_id).to_bytes(4, "little", signed=True)
    out += int(dbid).to_bytes(8, "little", signed=True)    # _dbid
    out += bytes(5)  # as the client read the older layout (see the .NET builder)
    out += int(char_key).to_bytes(4, "little", signed=True)
    out += encode_varint(len(option_data)) + bytes(option_data)
    out += encode_varint(len(skill_customizing))
    for cond_id, skill_id, priority in skill_customizing:
        out += int(cond_id).to_bytes(4, "little", signed=True)
        out += int(skill_id).to_bytes(4, "little", signed=True)
        out += bytes([priority])
    out += encode_varint(len(customizing_data)) + bytes(customizing_data)
    out += int(server_start_time).to_bytes(8, "little", signed=True)
    return bytes(out)


def build_character_created(name, gender=1, pc_creation_id=1, key=1,
                            customizing_data=b"", voice_type=0, slot_no=0,
                            race=1, position=(0.0, 0.0, 0.0),
                            zone_id=3016, now_ms=None, dbid=0, level=1):
    """0x361c CharCreate_RS reply body: result + LobbyCharDesc.

    Schema is DevPacketData_GameServer_CharCreate_RS (_result IntProperty,
    _character DevPacketData_Common_LobbyCharDesc). The character record is
    generated from our own state plus the request's appearance bytes; never
    replayed from captures.
    """
    if now_ms is None:
        now_ms = int(time.time() * 1000)
    profile = starter_profile(pc_creation_id, gender)
    character = _lobby_char_desc(
        key=key, nickname=name, pc_id=profile["pc_id"] if profile else 1,
        race=profile["race"] if profile else race, gender=gender,
        level=level, equip_item_level=0, combat_level=level, guild_name="",
        logout_time=now_ms, position=position, zone_id=zone_id,
        reg_time=now_ms, customizing_data=customizing_data,
        voice_type=voice_type, slot_no=slot_no, combat_power=0, dbid=dbid)
    return b"\x1c\x36" + (0).to_bytes(4, "little", signed=True) + character


def build_prepare_enter_map(enter_map_ticket=1, map_id=1000, game_time=None,
                            position=(0.0, 0.0, 0.0), direction=0.0,
                            reason=0, env_state=0, env_start_time=0,
                            group_env_states=(), teleport_effect_id=0,
                            cutscene_name="", inform_type=0):
    """0x3621 PrepareEnterMap_NT: tell the client which map to load and where.

    Schema: DevPacketData_GameServer_PrepareEnterMap_NT (_enter_map_ticket,
    _map_id, _game_time, _pos Vec3, _dir f32, _reason enum u8, _env_state u8,
    _env_start_time i64, _group_env_state array<MapGroupEnvState>,
    _teleport_effect_id, _cutscene_name FString, _inform_type enum u8).
    Learned wire widths from the official plaintext sample: all IntProperties
    are i32 except _teleport_effect_id (u8); _game_time/_env_start_time are
    i64; enums/bytes are u8; empty arrays serialize to a single zero count
    byte. Nothing here is replayed from captures: ticket/time are synthetic,
    map_id is an identifier the client's installed map data must resolve.
    """
    if game_time is None:
        game_time = int(time.time() * 1000)
    out = bytearray(b"\x21\x36")
    out += int(enter_map_ticket).to_bytes(4, "little", signed=True)
    out += int(map_id).to_bytes(4, "little", signed=True)
    out += int(game_time).to_bytes(8, "little", signed=True)
    out += struct.pack("<3f", *position)
    out += struct.pack("<f", float(direction))
    out += bytes([reason, env_state])
    out += int(env_start_time).to_bytes(8, "little", signed=True)
    out += encode_varint(len(group_env_states))
    for group_id, state, start_time in group_env_states:
        out += int(group_id).to_bytes(4, "little", signed=True)
        out += bytes([state])
        out += int(start_time).to_bytes(8, "little", signed=True)
    out += bytes([int(teleport_effect_id) & 0xFF])
    out += _fstring(cutscene_name)
    out += bytes([inform_type])
    return bytes(out)


def build_load_complete(enter_map_ticket=0, position=(0.0, 0.0, 0.0),
                        frame=0, char_key=1):
    """0x3623 LoadComplete_RS: acknowledge the client's 0x3622 map-load done.

    Schema: DevPacketData_GameServer_LoadComplete_RS (_result, _enter_map_ticket,
    _server_enter_map_pos Vec3, _frame, _char_key). Learned widths from the
    official sample, disambiguated by requiring _server_enter_map_pos to equal
    PrepareEnterMap_NT._pos: _result u16, _enter_map_ticket i32, _frame i32,
    _char_key u16. The position is the server-authoritative spawn point we
    already sent in 0x3621; char_key matches the synthetic record from 0x3620.
    """
    out = bytearray(b"\x23\x36")
    out += b"\x00\x00"                       # _result u16 = 0
    out += int(enter_map_ticket).to_bytes(4, "little", signed=True)
    out += struct.pack("<3f", *position)
    out += int(frame).to_bytes(4, "little", signed=True)
    out += int(char_key).to_bytes(2, "little", signed=False)
    return bytes(out)


# Map.dat: World_L_Starter, not its solo instance layer 100001.
SPAWN_MAP_ID = 1000
# World_L_Starter/MapData.dat: L_Starter_Region_01_Home / _Respawn.
# Recovered ground anchor; the exact post-creation quest destination is unknown.
# User-marked approximate first spawn from the 234752 movement trace.
# The recovered Region_01_Home point is a different, downhill respawn anchor.
SPAWN_POSITION = (7259.90771484375, -18223.455078125, 22404.498046875)
COMBAT_TIMEOUT_MS = 3500  # official 0x8d21 leave: ~3.3-3.6 s after the last action

# One pool for MyCharAppear._hp/_hp_max, the 0x364a ceiling, and the
# 0x3649 stat sheet. A zero ceiling in 0x364a clears the bar after appear.
SPAWN_HP = 1500
SPAWN_HP_MAX = 1500
# Temporary starter mana setting from the user's reference (~200); base MP
# is not present in the recovered PcData/PcStatLevel tables inspected so far.
SPAWN_MP = 200
SPAWN_SP = 100000  # existing SPMax stat; ground dodge costs 30000 in Skill.dat


def _build_spawn_detail_tail():
    """Generate the observed starter detail layout, without offset padding.

    Matches official file offsets 66..183. The five 13-byte records are
    candidate craft entries; the two compact lists are candidate skill points.
    Do not substitute usmap widths here: the wire differs from that schema.
    Marker semantics and the two bytes after gather experience remain unknown.
    """
    out = bytearray(b"\x01")
    out += struct.pack("<iq", 1, 0)  # level 1, experience 0
    out += b"\x00\x00"              # observed compact fields/markers
    out += b"\x05"
    for category in range(1, 6):
        out += struct.pack("<Biq", category, 1, 0)
    out += struct.pack("<iiq", 0, 0, 0)  # cooldowns/reputation candidates
    out += b"\x01"                      # observed detail marker
    for _ in range(2):
        out += b"\x03"
        for point_type in (1, 2, 3):
            out += struct.pack("<BH", point_type, 0)
    return bytes(out)


def build_my_char_appear(key=1, name="", server_id=1204, pc_id=1, race=1,
                         char_level=1, equip_item_level=0,
                         highest_total_item_level=0, level_max=1, exp=0,
                         combat_level=1, combat_exp=0, gender=1,
                         position=SPAWN_POSITION, direction=360.0,
                         hp=SPAWN_HP, hp_max=SPAWN_HP_MAX, mp=SPAWN_MP, mp_max=SPAWN_MP,
                         map_id=SPAWN_MAP_ID, combat_power=0, appear_reason=0,
                         weapon_id=None, customizing_data=b"", voice_type=0):
    """0x3633 MyCharAppear_NT: spawn the player's own character in the world.

    State/tail follow readable client decoders 0x8498b80/0x91f3870.
    HP is unsigned LEB128, and bool fields share a packed byte across records.
    Only reason, subzone and voice optional fields are enabled.
    """
    body = bytearray(b"\x33\x36")
    # ---- front: key, mask/flag, name, identity ----
    body += encode_varint(key)
    body += (0x2006).to_bytes(4, "little")
    body += bytes([0x37])                            # flag; bit0 -> name
    body += _fstring(name)
    body += int(server_id).to_bytes(2, "little")     # _server_id u16
    body += int(pc_id).to_bytes(4, "little", signed=True)
    body += bytes([int(race) & 0xFF])
    body += int(char_level).to_bytes(4, "little", signed=True)
    body += int(equip_item_level).to_bytes(4, "little", signed=True)
    body += int(highest_total_item_level).to_bytes(4, "little", signed=True)
    body += int(level_max).to_bytes(4, "little", signed=True)
    body += int(exp).to_bytes(8, "little", signed=True)
    body += int(combat_level).to_bytes(4, "little", signed=True)
    body += int(combat_exp).to_bytes(8, "little", signed=True)
    body += bytes([int(gender) & 0xFF])
    body += _build_spawn_detail_tail()
    # CharState mask=0: no local movement, relationship or lock-on fields.
    body += struct.pack('<H3ffHB', 0, *position, direction, 0, 1)
    body += encode_varint(hp) + encode_varint(hp_max)
    # MP, AP, SP, DP (+step), OP and FP, in receiver order.
    body += struct.pack('<13i', mp, mp_max, 0, 0, SPAWN_SP, SPAWN_SP,
                        0, 10000, 0, 100000, 100000, 10000, 10000)
    body += b'\x03'
    body += _stat_entry(206, -3000)
    body += _stat_entry(208, 300)
    body += _stat_entry(551, -3000)
    body += b'\x00'  # no abnormal effects
    body += encode_varint(1 if weapon_id else 0)
    if weapon_id:
        # EquipBroad: item, enchant, visual slot, expiry. First bool reserves
        # a byte: expired=false, display=true, default_mesh=true, override=true.
        # Later purchased-customizing=false consumes bit 4 of this same byte.
        body += struct.pack('<iBBqB', weapon_id, 0, 1, 0, 14)
        body += struct.pack('<i', 0)  # skin
        body += b'\x00\x03' + bytes(12)  # no dyes, three empty godstones
        # All 15 reference equip records (including empty slots) use fx_bit14.
        body += b'\x0e'  # fx_bit varint; bools use the reserved byte
    body += struct.pack('<Bqiiq', appear_reason, 1, map_id, 3000, 1)
    body += encode_varint(len(customizing_data)) + bytes(customizing_data)
    if not weapon_id:
        body += b'\x00'  # first packed bool: purchased-customizing=false
    body += bytes([voice_type])
    body += struct.pack('<iiq', 0, 0, 0)  # conqueror info (always present)
    body += struct.pack('<qq', combat_power, combat_power)
    body += b'\x00\x00'  # used/bonus daevanion point arrays
    return bytes(body)


CHECKPOINT_MS = 30000


def parse_position_report(body):
    """Client position from 0x3700/0x3701 (x y z f32 at byte 3, after a 02 byte)
    or 0x3718 (at byte 4, after 00 00). Observed in dev runs; the coordinates match
    our spawn point and follow the player. Other fields are not decoded."""
    offsets = {0x3700: 3, 0x3701: 3, 0x3718: 4}
    offset = offsets.get(int.from_bytes(body[:2], "little")) if len(body) >= 2 else None
    if offset is None or len(body) < offset + 12:
        return None
    position = struct.unpack_from("<3f", body, offset)
    if not all(math.isfinite(value) and abs(value) < 1e7 for value in position):
        return None
    return position


def checkpoint_tick(state, now_ms, force=False):
    """Save a selected stored character's position every CHECKPOINT_MS when it
    moved, or now (force, at disconnect). Failures are recorded, never fatal."""
    stored = state.get("stored_characters")
    position = state.get("position")
    if stored is None or stored.selected is None or position is None:
        return
    if position == state.get("saved_position"):
        return
    if not force and now_ms < state.get("checkpoint_at", 0):
        return
    state["checkpoint_at"] = now_ms + CHECKPOINT_MS
    try:
        stored.save_position(position)
        state["saved_position"] = position
        state["checkpoints"] = state.get("checkpoints", 0) + 1
    except (ValueError, KeyError, TypeError) as error:
        state.setdefault("persistence_errors", []).append(type(error).__name__)


def combat_state_tick(state, now_ms):
    """0x8d21 leave-combat once the timer set by combat requests has expired."""
    if state.get('in_combat') and now_ms >= state.get('combat_until', 0):
        state['in_combat'] = False
        return [build_combat_state(1, False)]
    return []


def build_char_barrier(key=1):
    """0x3646 CharBarrierInfo_NT: empty barrier list for our own character.

    Observed 5-byte official body: tag | key varint | _info count 0.
    """
    return b"\x46\x36" + encode_varint(key) + b"\x00"


def build_char_stats_update(key=1, stat_hpmax=0):
    """0x364a CharStatsUpdate_NT: no stat deltas, just the hp-max ceiling.

    Observed 13-byte official body: tag | key varint | _stats count 0 |
    _stat_hpmax i64.
    """
    return (b"\x4a\x36" + encode_varint(key) + b"\x00"
            + int(stat_hpmax).to_bytes(8, "little", signed=True))


# EContentsUnlockType (usmap). The official server unlocked these for a fresh level-1
# Light character right after Poeta's opening (official recording: Post, Community,
# Chat, Party_Force, Map, RemoteStorage, HudEdit, DamageAnalyzer), then Customizing.
# Chat stays locked ("Available after completion of quest HQ1100010", ContentsUnlock
# row 44) until it is listed.
STARTER_UNLOCKED_CONTENTS = (17, 42, 50, 52, 56, 61, 62)
# Unlocked afterwards with 0xe258: the starter recording's world-entry 0xe257 is empty
# and unlocks arrive as updates. An update raises System_ContentsUnlocked (EAionEventName
# 26), whose chat listener 0x8b6ed50 starts the Lime chat login 0x94fbf20 (candidate:
# only when Chat itself is newly unlocked; research/chat-and-gm.md).
LATER_UNLOCKED_CONTENTS = (45, 43)


def build_contents_unlock_list(types=STARTER_UNLOCKED_CONTENTS):
    """0xe257 ContentsUnlockList_NT: count varint, one EContentsUnlockType byte each.

    OBSERVED in the official recording at world entry (8 types). 0xe258 is the update
    form with the same layout (observed with 1 and 2 entries).
    """
    return b"\x57\xe2" + encode_varint(len(types)) + bytes(types)


def _unlock_update_once(state):
    """The 0xe258 unlock update, at most once per connection."""
    if state.get("unlock_update_sent"):
        return []
    state["unlock_update_sent"] = True
    return [build_contents_unlock_update()]


def build_contents_unlock_update(types=LATER_UNLOCKED_CONTENTS):
    """0xe258 ContentsUnlock update: same layout as 0xe257, for newly unlocked types."""
    return b"\x58\xe2" + encode_varint(len(types)) + bytes(types)


def build_mentoring_role_update(role=1, expire_time=0):
    """0x3657 MentoringRoleUpdate_NT: mentoring role enum + expire timestamp.

    Observed 11-byte official body: tag | role u8 | expire_time i64.
    """
    return b"\x57\x36" + bytes([int(role) & 0xFF]) \
        + int(expire_time).to_bytes(8, "little", signed=True)


def _stat_entry(stat_type, stat_value):
    """StatType wire entry: u16 _stat_type + i32 _stat_value (LE).

    The usmap declares both as IntProperty, but the official 0x3649 sample
    shows _stat_type is u16 on the wire (not i32); only _stat_value is i32.
    """
    return (int(stat_type).to_bytes(2, "little", signed=False)
            + int(stat_value).to_bytes(4, "little", signed=True))


def build_char_all_stats(stats=(), stat_hpmax=1500):
    """0x3649 CharAllStats_RS: full stat list + hp-max ceiling.

    Schema: DevPacketData_GameServer_CharAllStats_RS (_result IntProperty,
    _stats array<StatType>, _stat_hpmax Int64Property). Official sample layout:
    result u16=0 | count u8 | {stat_type u16, stat_value i32} entries |
    _stat_hpmax i64. All values here are generated from our own state.
    """
    out = bytearray(b"\x49\x36")
    out += b"\x00\x00"                            # _result u16 = 0
    out.append(len(stats))                        # count u8
    for stat_type, stat_value in stats:
        out += _stat_entry(stat_type, stat_value)
    out += int(stat_hpmax).to_bytes(8, "little", signed=True)
    return bytes(out)


DEFAULT_STATS = (
    (31, 21),        # WeaponDamage
    (33, 17),        # WeaponMinDamage
    (89, 2500),      # DecreaseShieldBlock
    (93, 1500),      # DecreaseWeaponBlock. Not current HP.
    (96, 100000),    # MaxWeaponBlock (usmap name; wire mapping unverified)
    (97, 100000),    # MaxShieldBlock (usmap name; wire mapping unverified)
    (100, 100),      # BackAttackCritical
    (128, 150),      # Critical
    (193, 119),      # HPMax
    (195, 10),       # HPRegen
    (199, 1500),     # MPMax (usmap name; wire mapping unverified)
    (201, 250),      # RestMPRegen
    (206, -3000),    # BattleMoveSpeed
    (208, 300),      # GroundVehicleSpeed
    (211, 100000),   # FlyLimitHeight
    (242, 100000),   # SPMax
    (244, 20000),    # RestSPRegen
    (255, 200),      # Block
    (275, 5),        # BattleHPRegen
    (276, 25),       # BattleMPRegen
    (277, 5000),     # BattleSPRegen
    (295, 100000),   # OPMax
    (297, 20000),    # OPRegen
    (300, 2000),     # BattleFPRegen
    (301, 4000),     # RestFPRegen
    (307, 38),       # ArmorDefense
    (318, 150),      # WeaponAccuracy
    (377, 500),      # ElementalistDespawnHPRegen
    (551, -3000),    # CombatFlySpeed
)

# Type 193 is HPMax. The official 0x3649 sample carries 119 there, matching
# its appear HPMax varint. Our development HP remains explicitly configured.
# Type 93 is DecreaseWeaponBlock; its sample value is already 1500.
_SPAWN_STAT_OVERRIDES = {193: SPAWN_HP_MAX, 199: SPAWN_MP}


def spawn_stats():
    """Stat sheet for the character that just appeared at SPAWN_POSITION."""
    return tuple((stat_type, _SPAWN_STAT_OVERRIDES.get(stat_type, value))
                 for stat_type, value in DEFAULT_STATS)


# Bodyless queries the client sends right after 0x3648 at world entry, answered with
# the official recording's replies (starter-official-bodies.json entries 318-322).
# Field meanings are not decoded. Candidate: a loading step waits on one of these
# and its success callback (0x8f26c20 / 0x8f46a60, u16 result) starts the Lime chat
# login 0x94fbf20 (research/chat-and-gm.md).
WORLD_ENTRY_QUERY_REPLIES = {
    0x4300: bytes.fromhex(
        "01430100010000000000ccdb3ca1010000c0f3e43ca1010000050101000000000000000000"
        "000000000000000000010200000000000000000000000000000000000001030000000000"
        "000000000000000000000000000104000000000000000000000000000000000000010500"
        "0000000000000000000000000000000000"),
    0x570e: bytes.fromhex("0f57000000"),
    0x56a7: bytes.fromhex("a85600000000000000000000"),
    0x8a1c: bytes.fromhex("1d8a000000"),
}


def build_comm_server_key(comm_server_key=None):
    """0x365a CommServerKey_RS: answer the client's 0x3659 key request.

    Schema: DevPacketData_GameServer_CommServerKey_RS (_result IntProperty,
    _comm_server_key StrProperty). Defaults to the key Login_RS already gave
    the client, so both packets agree. _result is u16 like the other matched
    replies (LoadComplete_RS, Ping_RS). Candidate: as i32 the client read an empty
    key, and the Lime chat login stops on its empty +0x80 (research/chat-and-gm.md).
    """
    if comm_server_key is None:
        comm_server_key = COMM_SERVER_KEY
    return b"\x5a\x36" + (0).to_bytes(2, "little") + _fstring(comm_server_key)


def _game_load_complete_request(body):
    """Parse a 0x3622 LoadComplete_RQ body; returns reply args or None.

    Schema: DevPacketData_GameServer_LoadComplete_RQ (_enter_map_ticket i32,
    _use_client_enter_map_pos bool u8, _client_enter_map_pos Vec3, _visible_len
    f32, _voxel_export_revision array<i32>, _voxel_export_tick array<i64>,
    _npk_did FString). We only need the ticket to echo it back.
    """
    if len(body) < 6:
        return None
    try:
        enter_map_ticket = int.from_bytes(body[2:6], "little", signed=True)
    except IndexError:
        return None
    return {"enter_map_ticket": enter_map_ticket}


def build_game_login(name, position, class_template=0x2A14, world_id=1204,
                     account_sid="721357", guid=None, now_ms=None,
                     name_code=None, race=0, gender=0, detail_flag=0,
                     equip_level=0, level=0, zone_id=0):
    """Synthetic 0x3615 game-login reply body from our own server state.

    Candidate layout only, reconstructed from a single observed sample. The
    result/time prefix, the class template id, world id, and the 824-byte
    record tail all follow the observed offsets; every record byte we cannot
    explain is zero-filled. This never replays capture bytes.
    """
    if now_ms is None:
        now_ms = int(time.time() * 1000)
    if not 0 <= class_template <= 0xFFFFFFFF:
        raise ValueError("class_template outside uint32")
    if not 0 <= world_id <= 0xFFFFFFFF:
        raise ValueError("world_id outside uint32")
    if guid is None:
        guid = "00000000-0000-0000-0000-000000000000"
    tail = bytearray(824)
    tail[0] = 1  # record count, observed
    tail[1:5] = class_template.to_bytes(4, "little")
    encoded_name = name.encode("ascii")
    if 6 + len(encoded_name) > 57:
        raise ValueError("Game-login name leaves no room for the fixed tail")
    tail[5] = len(encoded_name)
    tail[6:6 + len(encoded_name)] = encoded_name
    # The auto-loaded sample has u16 42 in the two bytes after its 12-byte
    # `$` name. 42 is the same code the login placeholder row carries.
    if name_code is not None:
        tail[6 + len(encoded_name):8 + len(encoded_name)] = (
            int(name_code).to_bytes(2, "little"))
    # Single-sample tail, named from values our other packets already use.
    # After a 12-byte name these sit at 22, 32, 44, and 77. Byte 24 is the
    # sample's third flag (1). Zone 3016 is the lobby zone on CharCreate.
    if race or gender or detail_flag:
        if len(encoded_name) != 12:
            raise ValueError("Race and gender offsets are for a 12-byte name")
        tail[22] = int(race) & 0xFF
        tail[23] = int(gender) & 0xFF
        tail[24] = int(detail_flag) & 0xFF
    if equip_level:
        tail[32:36] = int(equip_level).to_bytes(4, "little")
    if level:
        tail[44:48] = int(level).to_bytes(4, "little", signed=True)
    tail[57:65] = now_ms.to_bytes(8, "little")
    tail[65:77] = struct.pack("<3f", *position)
    if zone_id:
        tail[77:79] = int(zone_id).to_bytes(2, "little")
    tail[81:89] = now_ms.to_bytes(8, "little")
    tail[372:376] = world_id.to_bytes(4, "little")
    account = (account_sid + ":" + guid).encode("ascii")
    tail[767] = len(account)
    tail[768:768 + len(account)] = account
    tail[811] = 4
    tail[812:816] = str(world_id).encode("ascii")
    tail[816] = 4
    tail[817:821] = COMM_SERVER_KEY.encode("ascii")
    # After the comm key: _server_race u8, _chat_enable_level varint, _user_flag u8
    # (bools such as _is_born_server are bit-packed into the byte before the session
    # string). Official sample tail: 01 1e 00. Chat level stays 0 here: candidate
    # "chat from level N" gate, and chat unlocked with 0 for our level-1 characters.
    tail[821] = 1
    body = (b"\x15\x36\x00\x00"
            + now_ms.to_bytes(8, "little")
            + (now_ms - 25200000).to_bytes(8, "little")
            + bytes(tail))
    return body


def build_game_login_wire(name, position, **kwargs):
    """0xffff envelope containing the synthetic 0x3615 game-login body."""
    return _compressed_envelope(build_game_login(name, position, **kwargs))


def _compressed_envelope(body):
    inner = frame(body)
    return b"\xff\xff" + len(inner).to_bytes(4, "little") + lz4_block_encode(inner)


def _stored_characters(state):
    """The PersistentCharacters adapter of an authenticated launch, else None."""
    if state.get("world_store") is None:
        return None
    if "stored_characters" not in state:
        try:  # imported here: persistent_characters imports this module
            from .persistent_characters import PersistentCharacters
        except ImportError:
            from persistent_characters import PersistentCharacters
        state["stored_characters"] = PersistentCharacters(
            state["world_store"], state["authenticated_account_id"])
    return state["stored_characters"]


def _stored_reply(state, action):
    """Run a storage-backed handler; failures send nothing rather than a guess."""
    try:
        return action()
    except (ValueError, KeyError, TypeError) as error:
        state.setdefault("persistence_errors", []).append(type(error).__name__)
        return []


def game_followup_replies(plain, now_ms=None, state=None):
    """Map one decrypted game client body to the plaintext replies to send.

    Confirmed game-port pairings: 0x3602 Ping_RQ -> 0x3603 echo, 0x3613 game
    login -> synthetic 0x3615 envelope, 0x3619 name check -> 0x361a, and
    0x361b CharCreate_RQ -> 0x361c CharCreate_RS. The 0x3615 and 0x361c
    records are generated from our own state; nothing is replayed from
    captures. Returns a list of reply bodies.

    `state` is an optional per-connection dict that persists the character
    identity (name/gender/appearance) across frames so the 0x3633 appear
    burst matches the character the client created.
    """
    if state is None:
        state = {}
    if now_ms is None:
        now_ms = int(time.time() * 1000)
    if len(plain) < 2:
        return []
    header = int.from_bytes(plain[:2], "little")
    if plain in (b'\x3a\x38', b'\x3c\x38'):
        # Live request pairing; handlers 0x91a8c60/0x91a8c90 read u16 result.
        state['combat_requested'] = header == 0x383a
        replies = [struct.pack('<HH', header + 1, 0)]
        if header == 0x383a:
            # The client brackets every cast with 0x383a/0x383c. Officially the player
            # enters combat (0x8d21 01) once and leaves ~3.5 s after the last action,
            # not on 0x383c; an immediate leave sheathes the weapon (live test 2).
            if not state.get('in_combat'):
                state['in_combat'] = True
                replies.append(build_combat_state(1, True))
            state['combat_until'] = now_ms + COMBAT_TIMEOUT_MS
        return replies
    if header == 0x3602 and len(plain) >= 10:
        return [build_ping_reply(int.from_bytes(plain[2:10], "little"), now_ms)]
    # `--character-level` for dev launches; a selected stored character sets its own.
    level = clamp_level(state.get("level", 1))
    stored = _stored_characters(state)
    if header == 0x3613:
        account_sid, guid = _game_login_identity(plain)
        state["login_identity"] = (account_sid, guid)
        if stored is not None:
            # Authenticated launch: the account's stored characters (official layout).
            return _stored_reply(state, lambda: [_compressed_envelope(
                stored.login_reply(f"{account_sid}:{guid}", now_ms=now_ms))])
        intro = state.get("intro") or {}
        # A `$` placeholder with the spawn position is the 0x3615 record
        # that loaded by itself on the official restart. The origin record
        # leaves the client on the create screen.
        if intro.get("name"):
            name = intro["name"]
            position = SPAWN_POSITION
            world_id = intro.get("server_id", 1204)
            name_code = intro.get("code", 42)
            race, gender, detail_flag = 1, 2, 1
            equip_level, zone_id = 12, 3000
        else:
            name = "$local"
            position = (0.0, 0.0, 0.0)
            world_id = 1204
            name_code = None
            race = gender = detail_flag = 0
            equip_level, zone_id = 0, 3000
        return [build_game_login_wire(
            name, position, world_id=world_id, name_code=name_code,
            race=race, gender=gender, detail_flag=detail_flag,
            equip_level=equip_level, level=level, zone_id=zone_id,
            account_sid=account_sid, guid=guid, now_ms=now_ms)]
    if header == 0x3619:
        name = _game_name_request(plain)
        if name:
            return [build_name_check(name)]
        return []
    if header == 0x361b:
        if stored is not None:
            # Saved before the success reply; a database failure sends nothing.
            return _stored_reply(state, lambda: [stored.create(plain, now_ms=now_ms)])
        request = _game_char_create_request(plain)
        if request:
            state["character"] = request
            return [build_character_created(**request, now_ms=now_ms, level=level)]
        return []
    if header == 0x361f:
        if stored is not None:
            request = _game_select_character_request(plain)
            if request:
                state.setdefault("select_keys", []).append(request["key"])
            replies = _stored_reply(state, lambda: stored.select(plain))
            if replies:
                character = stored.selected_character()
                state["character"] = character
                state["level"] = clamp_level(character["level"])
                state["spawn"] = (character["map_id"], character["position"])
            return replies
        request = _game_select_character_request(plain)
        if request:
            state["selected_key"] = request["key"]
            # Empty cutscene name: character creation, then the loaded map.
            # The builder still accepts Cutscene_L_A_QM_1001 when a caller
            # passes it; this followup does not.
            character = state.get("character", {})
            profile = starter_profile(character.get("pc_creation_id", 1),
                                      character.get("gender", 1))
            return [build_select_character(key=request["key"],
                                          last_using_pc_id=profile["pc_id"] if profile else 1,
                                          customizing_data=character.get("customizing_data", b"")),
                    build_prepare_enter_map(
                        enter_map_ticket=1, map_id=SPAWN_MAP_ID,
                        position=SPAWN_POSITION)]
    if header == 0x3622:
        request = _game_load_complete_request(plain)
        if request:
            ticket = request["enter_map_ticket"]
            character = state.get("character", {})
            intro = state.get("intro") or {}
            name = character.get("name", intro.get("name", "$local"))
            gender = character.get("gender", 1)
            profile = starter_profile(character.get("pc_creation_id", 1), gender)
            # Skills follow the character level (tools/class_skills.py), not a grant list.
            kit = learned_skills(profile["class"], level) if profile else []
            state["learned_skills"] = {skill for skill, _ in kit}
            state["class"] = profile["class"] if profile else None
            # Stored characters spawn where they were saved; otherwise the dev spawn.
            map_id, position = state.get("spawn", (SPAWN_MAP_ID, SPAWN_POSITION))
            # Training dummy (NpcData TraDummy), spawned after the player below.
            # Plus every NPC spawn of the map from the client's MapData (tools/world_npcs.py).
            if "targets" not in state:
                state["targets"] = {TRAINING_DUMMY_KEY: training_dummy(position),
                                    **map_targets(map_id, Target)}
            state["equipped_weapon"] = starter_weapon(profile)
            state['resources'] = {'hp': SPAWN_HP, 'hp_max': SPAWN_HP_MAX,
                                  'mp': SPAWN_MP, 'mp_max': SPAWN_MP,
                                  'sp': SPAWN_SP, 'sp_max': SPAWN_SP}
            state['resource_tick_ms'] = now_ms
            # _frame 2 as in the official sample (candidate: the loader callback that
            # starts Lime chat completes on this reply; research/chat-and-gm.md).
            replies = [build_load_complete(enter_map_ticket=ticket,
                                        position=position, frame=2, char_key=1),
                    build_my_char_appear(name=name, gender=gender, map_id=map_id, position=position,
                                         pc_id=profile["pc_id"] if profile else 1,
                                         race=profile["race"] if profile else 1,
                                         char_level=level, combat_level=level, level_max=level,
                                         weapon_id=state["equipped_weapon"],
                                         customizing_data=character.get("customizing_data", b""),
                                         voice_type=character.get("voice_type", 0),
                                         equip_item_level=1 if state.get("equipped_weapon") else 0,
                                         highest_total_item_level=1 if state.get("equipped_weapon") else 0),
                    build_char_barrier(),
                    build_char_stats_update(stat_hpmax=SPAWN_HP_MAX),
                    build_mentoring_role_update()]
            if profile:
                # Observed wire families: usmap ordinal 0x3741 is NOT the
                # live MySkillList opcode. Send owned skills before hotbar.
                replies += [build_skill_list(kit),
                            build_skill_slots(profile["slots"])]
            replies.append(build_contents_unlock_list())
            replies += [build_npc_appear(target) for target in state["targets"].values()]
            return replies
    if header == 0x3630:
        # Logout_RQ (usmap; empty body, sent by the in-game Exit button). Unanswered,
        # the client spins and only quits on a second press. Save first: the client
        # disconnects on the reply. Logout_RS _result as u16 like other results;
        # no official sample.
        checkpoint_tick(state, now_ms, force=True)
        return [b"\x31\x36\x00\x00"]
    position = parse_position_report(plain)
    if position is not None:
        state["position"] = position  # checkpoint_tick saves it for stored characters
        return []
    if header == 0x3800:
        return cast_control_replies(plain, state, now_ms if now_ms is not None else int(time.time() * 1000))
    if header == 0x5610:
        character = state.get("character", {})
        profile = starter_profile(character.get("pc_creation_id", 1), character.get("gender", 1))
        weapon = starter_weapon(profile)
        if weapon:
            state["equipped_weapon"] = weapon
            return [build_equipped_inventory(weapon)]
        return []
    if header == 0x3648:
        return [build_char_all_stats(stats=spawn_stats(), stat_hpmax=SPAWN_HP_MAX)]
    if header == 0x3659:
        # The client asks for the comm key once the world and chat init are up (only
        # when Login_RS gave it none); the unlock update fires System_ContentsUnlocked,
        # which starts chat login.
        return [build_comm_server_key()] + _unlock_update_once(state)
    if header == 0xe34f and stored is None and not state.get("lobby_login_sent"):
        # The Login_RS handler (0x900cfb0) needs [A+0x428], which does not exist yet
        # when the instant game-login reply arrives, so it never ran and Lime+0x80
        # (server key; the chat login requires it) stayed empty. Resent here, from the
        # lobby, it runs: verified live (+0x80 = "1204"). Resending it in the world
        # dropped the connection. _is_born_server selects +0x80 (else +0x90). Empty
        # list: the no-login flow is on character creation at this point.
        state["lobby_login_sent"] = True
        account_sid, guid = state.get("login_identity", ("0", "0"))
        return [build_login_characters([], f"{account_sid}:{guid}", "1204",
                                       COMM_SERVER_KEY, now_ms=now_ms, is_born_server=True,
                                       chat_enable_level=0)]
    if header in WORLD_ENTRY_QUERY_REPLIES:
        replies = [WORLD_ENTRY_QUERY_REPLIES[header]]
        if header == 0x8a1c:  # last world-entry query; 0x3659 is skipped when keyed
            replies += _unlock_update_once(state)
        return replies
    return []


def _game_name_request(body):
    """Recover the character name from a 0x3619 name-check request.

    Observed layout: tag u16 0x3619 | u8 len | name bytes | u8 1. Returns
    the name or an empty string if the body does not match; never raises.
    """
    try:
        length = body[2]
        name = body[3:3 + length]
        if len(name) != length:
            return ""
        return name.decode("utf-8")
    except (IndexError, UnicodeDecodeError):
        return ""


def _game_char_create_request(body):
    """Parse a 0x361b CharCreate_RQ body against the usmap schema.

    Schema: DevPacketData_GameServer_CharCreate_RQ (_nickname StrProperty,
    _gender enum EGender, _pc_creation_id IntProperty, _customizing_data
    array<ByteProperty>, _voice_type enum ECharacterVoiceType, _slot_no
    ByteProperty, _skip_tutorial BoolProperty). Wire encodings are
    u8-prefixed FString, one-byte enum, little-endian int32, LEB128 array
    count, then trailing u8 u8 u8 bytes. Returns a dict of reply arguments,
    or None when the body does not parse.
    """
    if len(body) < 4:
        return None
    reader = Reader(body[2:])
    try:
        name = reader.take(reader.uint(1)).decode("utf-8")
        gender = reader.uint(1)
        pc_creation_id = reader.uint(4)
        customizing = reader.take(reader.varuint())
        voice_type = reader.uint(1)
        slot_no = reader.uint(1)
        skip_tutorial = reader.uint(1)
    except (Incomplete, UnicodeDecodeError, ValueError):
        return None
    return {"name": name, "gender": gender, "pc_creation_id": pc_creation_id,
            "customizing_data": customizing, "voice_type": voice_type,
            "slot_no": slot_no}


def _game_select_character_request(body):
    """Parse a 0x361f SelectCharacter_RQ body against the usmap schema.

    Schema: DevPacketData_GameServer_SelectCharacter_RQ (_key IntProperty,
    _map_id IntProperty, _pos DevPacketData_Common_Vec3). The observed 21-byte
    body encodes _key as LEB128 (the generic serializer path), then the
    remaining zero bytes cover _map_id (i32) and _pos (three f32). Returns a
    dict of reply arguments, or None when the body does not parse.
    """
    if len(body) < 6:
        return None
    reader = Reader(body[2:])
    try:
        key = reader.varuint()
        map_id = reader.uint(4)
        position = (struct.unpack("<f", reader.take(4))[0],
                    struct.unpack("<f", reader.take(4))[0],
                    struct.unpack("<f", reader.take(4))[0])
    except (Incomplete, ValueError):
        return None
    return {"key": key, "map_id": map_id, "position": position}


def _game_login_identity(body):
    """Recover the 'account:guid' identity string from a 0x3613 login body.

    Observed layout: tag u16 0x3613 | u8 0x2c | "account:guid" | 0x00 |
    u8 len | "local-dev-<64 hex>" | trailing zeros. Falls back to synthetic
    placeholders if the body does not match; never raises.
    """
    try:
        payload = body[3:]
        end = payload.find(b"\x00")
        if end <= 0:
            raise ValueError("no identity string")
        identity = payload[:end].decode("ascii")
        account_sid, _, guid = identity.partition(":")
        if not account_sid or not guid:
            raise ValueError("identity lacks account:guid")
        return account_sid, guid
    except (ValueError, UnicodeDecodeError):
        return "1000001", "00000000-0000-0000-0000-000000000000"


def drain_frames(buffer):
    frames, consumed = split_frames(bytes(buffer))
    del buffer[:consumed]
    return frames


def handle(connection, key, save_dir, followup_seconds=300,
           intro_character=None, authenticate=None, character_level=1):
    stores = []
    report = None

    def tracked_authentication(body):
        identity = authenticate(body)
        if identity and identity.get('world_store') is not None:
            stores.append(identity['world_store'])
        return identity

    try:
        report = _handle_connection(connection, key, save_dir, followup_seconds,
                                    intro_character, tracked_authentication if authenticate else None,
                                    character_level)
        return report
    finally:
        for store in stores:
            try:
                store.close()
            except (ValueError, OSError):
                # Failed cleanup cannot make the credential permanent: it also
                # expires with/revokes alongside its parent browser session.
                if report is not None:
                    report['world_session_close'] = 'unavailable'


def _handle_connection(connection, key, save_dir, followup_seconds=300,
                       intro_character=None, authenticate=None, character_level=1):
    authenticated = authenticate is None
    if authenticate is not None:
        save_dir = None  # Disable credential-bearing raw/key/journal captures.
    report = {"utc": datetime.now(timezone.utc).isoformat(),
              "peer_port": connection.getpeername()[1]}

    greeting = read_greeting(connection)
    report["greeting_bytes"] = len(greeting)
    try:
        frames, consumed = split_frames(greeting)
        if not frames or consumed != len(greeting):
            raise ValueError("Greeting did not form a complete frame")
        body = frames[0].body
        if int.from_bytes(body[:2], "little") != 0x3610:
            raise ValueError("First frame is not a 0x3610 greeting")
        modulus, exponent = rsa_public_from_hello(body)
    except (ValueError, IndexError) as error:
        report["greeting_error"] = str(error)
        return report
    report["public_key"] = {"modulus_bits": modulus.bit_length(), "exponent": exponent}
    reply = build_game_reply(modulus, exponent, key)
    connection.sendall(reply)
    report["reply_bytes"] = len(reply)

    cipher = RC4(key)
    buffer = bytearray()
    raw = bytearray()
    decrypted_headers = []
    state = {"level": clamp_level(character_level)}
    if intro_character:
        state["intro"] = intro_character
    ticks_sent = 0
    deadline = time.monotonic() + followup_seconds
    connection.settimeout(0.05)
    if save_dir is not None:
        raw_file = save_dir / f"followup-{report['peer_port']}.bin"
        raw_file.parent.mkdir(parents=True, exist_ok=True)
        key_file = save_dir / f"injected-key-{report['peer_port']}.bin"
        key_file.write_bytes(key)
        headers_file = save_dir / f"headers-{report['peer_port']}.txt"
        # Append new traffic instead of rewriting the growing session.
        raw_file.write_bytes(b'')
        headers_file.write_text('', encoding='ascii')
        (save_dir / f"events-{report['peer_port']}.jsonl").write_text('', encoding='utf-8')
    else:
        raw_file = None
        key_file = None
        headers_file = None
    while time.monotonic() < deadline:
        now = int(time.time() * 1000)
        for out in (regenerate_resources(state, now) + respawn_targets(state, now)
                    + combat_state_tick(state, now) + npc_ai_tick(state, now)):
            connection.sendall(frame(out))
        checkpoint_tick(state, now)
        try:
            chunk = connection.recv(8192)
        except socket.timeout:
            chunk = b""
        except OSError:
            break
        if not chunk:
            if time.monotonic() >= deadline:
                break
            connection.sendall(frame(build_clock()))
            ticks_sent += 1
            continue
        raw.extend(chunk)
        if raw_file is not None:
            with raw_file.open('ab') as stream:
                stream.write(chunk)
        buffer.extend(chunk)
        for inbound in drain_frames(buffer):
            plain = cipher.crypt(inbound.body)
            header = int.from_bytes(plain[:2], "little") if len(plain) >= 2 else -1
            if authenticate is not None:
                if header == 0x3613:
                    if authenticated:
                        return {**report, 'authentication': 'replay_rejected'}
                    identity = authenticate(plain)
                    if not identity:
                        return {**report, 'authentication': 'rejected'}
                    state.update(identity)
                    authenticated = True
                    report['authentication'] = 'accepted'
                elif not authenticated and header != 0x3602:
                    return {**report, 'authentication': 'required'}
            decrypted_headers.append("0x%04x" % header)
            if headers_file is not None:
                with headers_file.open('a', encoding='ascii') as stream:
                    stream.write(decrypted_headers[-1] + '\n')
            started = time.monotonic()
            sent = []
            error = None
            try:
                for out in game_followup_replies(plain, state=state):
                    connection.sendall(frame(out))
                    sent.append(out)
            except Exception as exc:
                error = exc
                raise
            finally:
                if save_dir is not None:
                    record_exchange(save_dir / f"events-{report['peer_port']}.jsonl",
                                    plain, sent, state,
                                    (time.monotonic() - started) * 1000, error)
    checkpoint_tick(state, int(time.time() * 1000), force=True)  # save on disconnect
    report["decrypted_body_headers_rc4_body_continuing"] = decrypted_headers
    if state.get("world_store") is not None:
        # Stored-character diagnostics: error types and the raw select keys only.
        report["persistence"] = {"errors": state.get("persistence_errors", []),
                                 "select_keys": state.get("select_keys", []),
                                 "checkpoints": state.get("checkpoints", 0)}
    report["raw_followup_bytes"] = len(raw)
    report["clock_ticks_sent"] = ticks_sent
    if save_dir is not None:
        save_dir = Path(save_dir)
        save_dir.mkdir(parents=True, exist_ok=True)
        if key_file is None:
            (save_dir / f"injected-key-{report['peer_port']}.bin").write_bytes(key)
        if raw_file is None:
            (save_dir / f"followup-{report['peer_port']}.bin").write_bytes(bytes(raw))
    return report


def serve(port=DEFAULT_PORT, key=None, save_dir=None, seconds=1800,
          followup_seconds=300, on_ready=None, intro_character=None, authenticate=None,
          character_level=1):
    if key is None:
        key = os.urandom(32)
    with socket.socket() as listener:
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind((HOST, port))
        listener.listen(1)
        listener.settimeout(1.0)
        if on_ready is not None:
            on_ready(listener.getsockname()[1])
        deadline = time.monotonic() + seconds
        reports = []
        while time.monotonic() < deadline and not (save_dir and (Path(save_dir) / "stop").exists()):
            try:
                connection, peer = listener.accept()
            except socket.timeout:
                continue
            if peer[0] != HOST:
                connection.close()
                continue
            with connection:
                report = handle(connection, key, save_dir, followup_seconds,
                                intro_character=intro_character, authenticate=authenticate,
                                character_level=character_level)
                reports.append(report)
                if save_dir is not None:
                    (Path(save_dir) / "report.json").write_text(
                        json.dumps(reports, indent=2) + "\n", encoding="utf-8")
                print(json.dumps(report), flush=True)
        return reports


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=local_port())
    parser.add_argument("--output", type=Path, default=None)
    parser.add_argument("--seconds", type=int, default=1800)
    parser.add_argument("--followup-seconds", type=int, default=300)
    parser.add_argument("--ready-file", type=Path, default=None,
                        help="Write this file once the listener is bound")
    args = parser.parse_args(argv)
    if args.output is not None:
        local = (ROOT / "research/local").resolve()
        output = args.output.resolve()
        if not output.is_relative_to(local) or output.exists():
            parser.error("Use a new directory directly under research/local/")
        output.mkdir(parents=True, exist_ok=False)
    on_ready = None
    if args.ready_file is not None:
        def on_ready(port):
            args.ready_file.write_text(str(port), encoding="ascii")
    serve(port=args.port, save_dir=args.output, seconds=args.seconds,
          followup_seconds=args.followup_seconds, on_ready=on_ready)


if __name__ == "__main__":
    main()
