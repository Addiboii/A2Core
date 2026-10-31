"""Golden bytes from the Python native-protocol builders, for the .NET port's parity checks.

Every case uses fixed inputs (no captures, no secrets); tests/Aion.Native.Checks rebuilds
the same cases with server/Aion.Protocol and compares byte-for-byte. Regenerate after a
deliberate builder change:

    python tools/native_parity.py          # writes tests/fixtures/native-parity.json
"""
import json
import struct
import uuid
from pathlib import Path
from unittest import mock

try:
    from . import aion_protocol, combat, game_server as g, login_server as l, npc_ai, probe_login_reply as p
    from . import starter_casts, starter_equipment, starter_skills
    from .dev_defaults import ACCOUNT_SID, WORLD_ID, WORLD_ID_STRING, account_session
except ImportError:
    import aion_protocol, combat, game_server as g, login_server as l, npc_ai, probe_login_reply as p
    import starter_casts, starter_equipment, starter_skills
    from dev_defaults import ACCOUNT_SID, WORLD_ID, WORLD_ID_STRING, account_session

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'tests/fixtures/native-parity.json'
NOW = 1760000000123
GUID = '0b4f6a3c-1d2e-4f50-8a9b-0c1d2e3f4a5b'
POS = (7259.90771484375, -18223.455078125, 22404.498046875)
# A fixed odd 1024-bit modulus: only the arithmetic is compared, nothing is decrypted.
MODULUS = (1 << 1023) + 0x1234567 * 2 + 1
SEED = bytes(range(20))
KEY = bytes(range(32))


def cases():
    out = {}
    out['varint'] = b''.join(aion_protocol.encode_varint(v) for v in (0, 1, 127, 128, 300, 16384, 0xFFFFFFFF))
    out['frame'] = g.frame(b'\x00\x36abc')
    data = (b'hello hello hello world ' * 9) + bytes(range(40)) + b'x' * 300
    out['lz4'] = aion_protocol.lz4_block_encode(data)
    out['rc4'] = p.rc4(b'\x00' * 40 + b'native parity', KEY)
    with mock.patch.object(p.os, 'urandom', lambda n: SEED[:n]):
        out['oaep'] = p.oaep_encode(KEY, 128)
        out['rsa'] = p.rsa_pub_encrypt(KEY, MODULUS, 3)
        out['login_reply'] = p.build_reply(MODULUS, 3, KEY)
        out['game_reply'] = g.build_game_reply(MODULUS, 3, KEY)
    # Login port
    out['account'] = l.build_account(ACCOUNT_SID, GUID, 1361)
    rows = [{'server_id': 1361, 'name': f'Dev_{i}', 'level': i, 'exp': i * 10, 'code': 5, 'flags': 1,
             'last_played_unix_ms': NOW} for i in range(1, 6)]
    out['characters_empty'] = l.build_characters([])
    out['characters_5'] = l.build_characters(rows)
    out['worlds'] = l.build_world_directory(l.DEFAULT_WORLDS)
    out['worlds_5'] = l.build_world_directory([{'id': 1361 + i, 'group': 1 + i % 2, 'name': f'W{i}'} for i in range(5)])
    out['select_result'] = l.build_select_result(1361)
    out['redirect'] = l.build_redirect(1361, '127.0.0.1', 13328)
    out['login_ping'] = l.build_ping(0x1122334455667788, NOW)
    # Game port
    out['ping'] = g.build_ping_reply(42, NOW)
    out['clock'] = g.build_clock(NOW)
    out['name_check'] = g.build_name_check('Player1')
    lobby = dict(key=7, nickname='Player1', pc_id=5, race=1, gender=1, level=3, exp=99, combat_level=3,
                 equip_item_level=1, zone_id=3009, equips=((110160001, 0, 1),), position=POS, voice_type=2,
                 slot_no=1, customizing_data=bytes(range(12)), dbid=7, logout_time=NOW, reg_time=NOW)
    out['lobby_record'] = g._lobby_char_desc(**lobby)
    out['login_characters'] = g.build_login_characters([lobby, dict(lobby, key=8, nickname='Second', equips=())],
                                                       account_session(GUID), WORLD_ID_STRING, g.COMM_SERVER_KEY, now_ms=NOW,
                                                       server_race=1)
    out['login_characters_lobby'] = g.build_login_characters([], account_session(GUID), WORLD_ID_STRING, g.COMM_SERVER_KEY,
                                                             now_ms=NOW, is_born_server=True, chat_enable_level=0)
    out['select_character'] = g.build_select_character(key=65536, dbid=7, char_key=1, last_using_pc_id=5,
                                                       customizing_data=b'\x0c\x64\x65', server_start_time=NOW)
    out['character_created'] = g.build_character_created('Player1', gender=1, pc_creation_id=1, key=7, dbid=7,
                                                         customizing_data=b'\x0c\x64', voice_type=2, slot_no=1,
                                                         now_ms=NOW, level=1)
    out['prepare_enter_map'] = g.build_prepare_enter_map(enter_map_ticket=1, map_id=1000, game_time=NOW, position=POS)
    out['load_complete'] = g.build_load_complete(enter_map_ticket=1, position=POS, frame=2, char_key=1)
    out['my_char_appear_weapon'] = g.build_my_char_appear(name='Player1', gender=1, map_id=1000, position=POS, pc_id=5,
                                                          race=1, char_level=3, combat_level=3, level_max=3,
                                                          weapon_id=110160001, customizing_data=b'\x0c\x64',
                                                          voice_type=2, equip_item_level=1, highest_total_item_level=1)
    out['my_char_appear_bare'] = g.build_my_char_appear(name='$local')
    out['barrier'] = g.build_char_barrier()
    out['stats_update'] = g.build_char_stats_update(stat_hpmax=g.SPAWN_HP_MAX)
    out['mentoring'] = g.build_mentoring_role_update()
    out['unlock_list'] = g.build_contents_unlock_list()
    out['unlock_update'] = g.build_contents_unlock_update()
    out['comm_key'] = g.build_comm_server_key()
    out['all_stats'] = g.build_char_all_stats(stats=g.spawn_stats(), stat_hpmax=g.SPAWN_HP_MAX)
    out['game_login_dev'] = g.build_game_login('$local', (0.0, 0.0, 0.0), world_id=WORLD_ID, level=1, zone_id=3000,
                                               account_sid=ACCOUNT_SID, guid=GUID, now_ms=NOW)
    out['game_login_intro'] = g.build_game_login('$abcdefghijk', g.SPAWN_POSITION, world_id=1361, name_code=42,
                                                 race=1, gender=2, detail_flag=1, equip_level=12, level=5,
                                                 zone_id=3000, account_sid=ACCOUNT_SID, guid=GUID, now_ms=NOW)
    out['game_login_wire'] = g.build_game_login_wire('$local', (0.0, 0.0, 0.0), level=1, zone_id=3000,
                                                     account_sid=ACCOUNT_SID, guid=GUID, now_ms=NOW)
    out['skill_list'] = starter_skills.build_skill_list([(11020000, 1), (11010000, 2)])
    out['skill_slots'] = starter_skills.build_skill_slots([[0, 11020000], [1, 11010000]])
    out['equipped_inventory'] = starter_equipment.build_equipped_inventory(110160001)
    out['skill_hit'] = combat.build_skill_hit(2005, 1, 11020000, 986511, 1234, critical=True, seq=2)
    target = combat.Target(key=2005, name='x', hp=266, hp_max=266, position=(1.5, -2.25, 3.0), npc_id=2290017,
                           level=4, direction=-49.219, relationship=6, aggressive=True)
    out['npc_appear_aggressive'] = combat.build_npc_appear(target)
    dead = combat.Target(key=1000, name='d', hp=0, hp_max=5000, position=POS, npc_id=2400032, level=10,
                         direction=180.0, relationship=3)
    out['npc_appear_dead'] = combat.build_npc_appear(dead)
    out['npc_hp'] = combat.build_npc_hp(2005, 17)
    out['combat_state'] = combat.build_combat_state(1, True)
    out['death'] = combat.build_death(2005)
    out['skill_start'] = starter_casts.build_skill_start(11020000, 3, 90.5, POS, target_key=2005, cooldown=1000)
    out['resource_update'] = starter_casts.build_resource_update({'hp': 1500, 'hp_max': 1500, 'mp': 150, 'mp_max': 200,
                                                                  'sp': 70000, 'sp_max': 100000})
    out['cast_reply'] = b"\x01\x38\x03" + struct.pack("<HiBB", 0, 11020000, 0, 7)
    out['npc_move_start_walk'] = npc_ai.build_npc_move_start(2005, POS, 33.3)
    out['npc_move_run'] = npc_ai.build_npc_move(2005, POS, 359.9, run=True)
    out['npc_stop'] = npc_ai.build_npc_stop(2005, POS, -10.0)
    out['uuid5'] = uuid.uuid5(uuid.UUID(GUID), 'native-create-v1:abc').bytes
    return {name: value.hex() for name, value in out.items()}


def main():
    OUTPUT.parent.mkdir(exist_ok=True)
    OUTPUT.write_text(json.dumps(cases(), indent=1, sort_keys=True) + '\n', encoding='utf-8')
    print(f'{len(cases())} parity cases -> {OUTPUT.relative_to(ROOT)}')


if __name__ == '__main__':
    main()
