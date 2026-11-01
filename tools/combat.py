"""Skill damage for the development server.

A data-driven CANDIDATE model, not recovered retail formulas. Inputs are recovered:
each skill's Damage rows (tools/class_skills.py), the equipped weapon's damage and
Critical stats, and the class's per-level FixingDamage/Defense/HPMax (PcStatLevel).
How they combine is our choice until the client's formula is found:

    attack  = weapon damage roll (min..max) + FixingDamage(level)
    raw     = flat (min..max) + attack * ratio / 10000        per Damage row
    crit    = chance Critical / 1000 (capped), damage x CRITICAL_MULTIPLIER
    damage  = raw * DEFENSE_SCALE / (DEFENSE_SCALE + target defense), at least 1
"""
import random
import struct
from dataclasses import dataclass

try:
    from . import class_skills
    from .aion_protocol import encode_varint, varuint
except ImportError:
    import class_skills
    from aion_protocol import encode_varint, varuint

CRITICAL_MULTIPLIER = 1.5
CRITICAL_CAP = 0.5
DEFENSE_SCALE = 1000


@dataclass
class Target:
    key: int
    name: str
    hp: int
    hp_max: int
    defense: int = 0
    position: tuple = None
    npc_id: int = 0
    level: int = 1
    direction: float = 0.0
    relationship: int = 6      # ERelationshipEntity (6 Monster)
    aggressive: bool = False   # NpcData AggroType Aggro_A
    walk_speed: float = 0.0    # NpcData BaseWalkSpeed / BaseMoveSpeed, units per second
    run_speed: float = 0.0
    radius: int = 0            # NpcData CollisionCylinderRadius
    function: str = 'None'     # NpcData FunctionType (BuySell, EnterInstanceLayer, ...)

    @property
    def dead(self):
        return self.hp <= 0

    @property
    def hostile(self):
        """Monster-family ERelationshipEntity values (Monster, AntiCompanion, PCFriendly
        excluded, teams A-E, Invisible)."""
        return self.relationship in (6, 11, 16, 17, 18, 19, 20, 21)


def base_stats(class_name, level):
    """PcStatLevel stats for a class at a level (FixingDamage, Defense, HPMax, ...)."""
    table = class_skills.data()['stats'].get(class_name, {})
    return dict(table.get(str(class_skills.clamp_level(level)), {}))


def weapon_stats(item_id):
    return class_skills.data()['weapon_stats'].get(str(item_id), {'min': 0, 'max': 0, 'critical': 0, 'accuracy': 0})


def _roll(pair, rng):
    low, high = (value or 0 for value in pair)
    return rng.randint(min(low, high), max(low, high))


def skill_hits(skill_id, class_name, level, weapon_item, target_defense=0, rng=random, pvp=False):
    """One hit per Damage row of the skill: [{'amount', 'critical', 'effect_id'}]."""
    definition = class_skills.skill(skill_id)
    if not definition:
        return []
    weapon = weapon_stats(weapon_item)
    fixing = base_stats(class_name, level).get('FixingDamage', 0)
    critical_chance = min(CRITICAL_CAP, weapon['critical'] / 1000)
    hits = []
    for row in definition.get('damage', []):
        values = row['pvp'] if pvp and row.get('pvp') else row
        attack = _roll((weapon['min'], weapon['max']), rng) + fixing
        raw = _roll(values['flat'], rng) + attack * _roll(values['ratio'], rng) / 10000
        critical = rng.random() < critical_chance
        if critical:
            raw *= CRITICAL_MULTIPLIER
        amount = max(1, round(raw * DEFENSE_SCALE / (DEFENSE_SCALE + max(0, target_defense))))
        hits.append({'amount': amount, 'critical': critical, 'effect_id': row['effect_id']})
    return hits


def apply_hits(target, hits):
    """Subtract hits from the target's HP (never below zero); returns damage dealt."""
    before = target.hp
    for hit in hits:
        target.hp = max(0, target.hp - hit['amount'])
    return before - target.hp


# ---------- Wire (server -> client) ----------
# Layouts follow PlayerPackets.SkillEffect / NpcSkillEffect (usmap SkillEffect_NT). CANDIDATE until
# every capture is replayed; parsers round-trip the fixtures in tests/test_combat.py.
def _parse_skill_effect_body(body):
    """Parse 0x3804 body after the opcode (switch 04 player, 06 NPC, 14 projectile)."""
    off = 0
    target_key, off = varuint(body, off)
    switch = body[off]
    off += 1
    mask = body[off]
    off += 1
    caster_key, off = varuint(body, off)
    skill_id = struct.unpack_from('<i', body, off)[0]
    off += 4
    action_uid = body[off]
    off += 1
    hit_type, off = varuint(body, off)
    plotter = None
    if switch == 6 and (mask & 0x02):
        plotter = body[off:off + 3]
        off += 3
    effect_id = struct.unpack_from('<I', body, off)[0]
    off += 4
    effect_seq = struct.unpack_from('<I', body, off)[0]
    off += 4
    attack_speed, off = varuint(body, off)
    damage, off = varuint(body, off)
    effect_seq_u8 = body[off]
    off += 1
    tail = body[off:]
    return {
        'target_key': target_key,
        'switch': switch,
        'mask': mask,
        'caster_key': caster_key,
        'skill_id': skill_id,
        'action_uid': action_uid,
        'hit_type': hit_type,
        'plotter': plotter,
        'effect_id': effect_id,
        'effect_seq': effect_seq,
        'attack_speed': attack_speed,
        'damage': damage,
        'effect_seq_u8': effect_seq_u8,
        'tail': tail,
    }


def parse_skill_hit(packet):
    """Decode a full 0x3804 SkillEffect_NT packet (opcode + body)."""
    if len(packet) < 4 or packet[0:2] != b'\x04\x38':
        raise ValueError('Not a 0x3804 packet')
    return _parse_skill_effect_body(packet[2:])


def build_skill_hit(target_key, actor_key, skill_id, effect_id, damage, critical=False,
                    action_uid=0, seq=1, attack_speed=10000):
    """0x3804 player hit (PlayerPackets.SkillEffect switch 04): action uid matches 0x3802."""
    if not 0 <= seq <= 255:
        raise ValueError('Hit sequence must fit one byte')
    if not 0 <= action_uid <= 255:
        raise ValueError('Action uid must fit one byte')
    hit_type = 3 if critical else 2
    body = (encode_varint(target_key) + bytes([4, 0]) + encode_varint(actor_key)
            + struct.pack('<iB', skill_id, action_uid) + encode_varint(hit_type)
            + struct.pack('<II', effect_id, seq) + encode_varint(attack_speed)
            + encode_varint(damage) + bytes([seq, 0]))
    return b'\x04\x38' + body


def build_npc_skill_hit(target_key, actor_key, skill_id, effect_id, damage, action_uid=1, seq=1,
                        attack_speed=10000, hit_type=2, include_plotter=True):
    """0x3804 NPC hit (PlayerPackets.NpcSkillEffect switch 06)."""
    if not 0 <= seq <= 255:
        raise ValueError('Hit sequence must fit one byte')
    if not 0 <= action_uid <= 255:
        raise ValueError('Action uid must fit one byte')
    mask = 0x04 | (0x02 if include_plotter else 0)
    body = encode_varint(target_key) + bytes([6, mask]) + encode_varint(actor_key)
    body += struct.pack('<iB', skill_id, action_uid) + encode_varint(hit_type)
    if include_plotter:
        body += b'\x00\x00\x02'
    body += struct.pack('<II', effect_id, seq) + encode_varint(attack_speed) + encode_varint(damage) + bytes([seq, 0])
    return b'\x04\x38' + body


def parse_npc_appear(packet):
    """Decode 0x3641 NpcAppear_NT enough for HP/level calibration (level is the i32 before spawn tag)."""
    if len(packet) < 4 or packet[0:2] != b'\x41\x36':
        raise ValueError('Not a 0x3641 packet')
    body = packet[2:]
    off = 0
    key, off = varuint(body, off)
    mask = struct.unpack_from('<H', body, off)[0]
    off += 2
    off += 1  # reserved u8 after mask
    npc_id = struct.unpack_from('<I', body, off)[0]
    off += 4
    off += 2  # state bytes observed 00 02
    off += struct.calcsize('<4fHB')  # position + direction + angle + live flag
    hp, off = varuint(body, off)
    hp_max, off = varuint(body, off)
    off += 13 * 4  # resource ints
    relationship = body[off]
    off += 3
    if mask & 0x08:
        off += 1  # aggressive byte
    off += 1  # trailing 00 before level
    level = struct.unpack_from('<i', body, off)[0]
    off += 4
    spawn_tag = struct.unpack_from('<I', body, off)[0]
    return {'key': key, 'npc_id': npc_id, 'hp': hp, 'hp_max': hp_max, 'relationship': relationship,
            'level': level, 'spawn_tag': spawn_tag, 'aggressive': bool(mask & 0x08)}


TRAINING_DUMMY_KEY = 1000
# NpcData.dat 2400032 M_D1_FV_TraDummy_01_01 (model MOB_TraDummy_01, level 10, Monster,
# AggroNormal): a training-dummy model. (2000000 "SandbagMonster" is a Krall model.)
# Its HP is ours; NpcData carries no HP. Small enough that one hit visibly moves the bar.
SANDBAG_NPC_ID = 2400032
SANDBAG_LEVEL = 10


def training_dummy(spawn_position, hp=5_000):
    """The sandbag 3 m from the spawn point (world units are cm), facing the player."""
    x, y, z = spawn_position
    return Target(key=TRAINING_DUMMY_KEY, name='Training Dummy', hp=hp, hp_max=hp,
                  position=(x + 300.0, y, z), npc_id=SANDBAG_NPC_ID, level=SANDBAG_LEVEL,
                  direction=180.0)


# Official recording 2026-10-13 (research/official-combat-capture.md): 108 unique
# 0x3641 bodies. Observed: the 0x2004 mask word (bit 0x08 adds an aggressive byte,
# set for EAggroType Aggro_A NPCs; bit 0x01, not built here, adds a byte that varies
# between spawns of one NPC), NpcData ID, state mask 0x0200, position, direction
# in degrees and again as a u16 angle, live flag, HP/HP-max varints, 13 resource
# ints (as observed on ordinary monsters), the NpcData RelationshipEntity byte
# (Monster 6), empty abnormal/stat arrays, NpcData level. The final u32 differs
# per spawn group (CANDIDATE spawn key) and the move-state byte 01 is copied as
# seen; neither has a confirmed meaning.
RELATIONSHIP_MONSTER = 6


def build_npc_appear(target, relationship=None, aggressive=None, spawn_tag=None):
    """0x3641 NpcAppear for an ordinary standing NPC (relationship/aggression from the target)."""
    relationship = target.relationship if relationship is None else relationship
    aggressive = target.aggressive if aggressive is None else aggressive
    x, y, z = target.position
    mask = 0x2004 | (0x08 if aggressive else 0)
    angle = round(target.direction % 360 / 360 * 65536) & 0xFFFF
    return (b'\x41\x36' + encode_varint(target.key) + struct.pack('<HB', mask, 0)
            + struct.pack('<I', target.npc_id) + b'\x00\x02'
            + struct.pack('<4fHB', x, y, z, target.direction, angle, 0 if target.dead else 1)
            + encode_varint(target.hp) + encode_varint(target.hp_max)
            + struct.pack('<13i', 100, 100, 0, 0, 0, 0, 100, 100, 1, 0, 0, 0, 0)
            + bytes([relationship, 0, 1]) + (b'\x01' if aggressive else b'') + b'\x00'
            + struct.pack('<iI', target.level, target.key if spawn_tag is None else spawn_tag) + b'\x00')


def build_npc_hp(key, hp):
    """0x8d00 entity points: key, kind 2 (i64 values), one entry, type 0 (HP), i64.

    Sent after every hit in the official recording; HP 0 is the kill. Kind 1 carries
    i32 entries (player MP/SP types 1/3/8...), kind 3 both arrays.
    """
    return b'\x00\x8d' + encode_varint(key) + b'\x02\x01\x00' + struct.pack('<q', hp)


def build_combat_state(key, in_combat):
    """0x8d21: key | 00 | 1 in combat / 0 out (OBSERVED in the official recording: sent
    for the player when its first attack starts and again when combat ends, and for a
    mob when it engages). The usmap name for this opcode is not established."""
    return b'\x21\x8d' + encode_varint(key) + b'\x00' + bytes([1 if in_combat else 0])


def build_death(key, flag=3):
    """0x3642 despawn: entity, 0, flag. Officially sent seconds after the HP-0 update
    to remove the corpse (flag 3); flag 1 is the common out-of-view removal."""
    return b'\x42\x36' + encode_varint(key) + b'\x00' + encode_varint(flag)
