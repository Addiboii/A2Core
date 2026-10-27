"""Skill action playback and resources for observed self-target requests.

Any skill the character has learned (tools/class_skills.py) can be cast, using its
recovered Skill.dat cost, cooldown and weapon requirement.

Request evidence: basic Gladiator attack 11020000 and dodge 11000100 on
chain-20261012-231745-65bb6178. SkillEnd's layout is corroborated by a2kit.
The 0x3801 layout is read from handler RVA 0x91a6a70: flags u8, result u16,
skill i32, optional toggle (bit 0) and client action UID (bit 1).
Successful requests receive a start notification, not an immediate end.
Damage, hit targets and full effect simulation are not implemented.
Request optional field order follows the readable writer at RVA 0x8f7db28;
both the 35-byte normal and 36-byte reserved forms are supported.
"""
import math
import struct
try:
    from .aion_protocol import Reader, Incomplete, encode_varint
    from . import class_skills, combat, npc_ai
except ImportError:
    from aion_protocol import Reader, Incomplete, encode_varint
    import class_skills
    import combat
    import npc_ai


def build_skill_start(skill, action_uid, yaw, location, target_key=1, actor_key=1,
                      attack_speed=10000, cooldown=0):
    """Observed mask-zero 0x3802 form matching pinned a2kit cast fixtures.

    No optional client UID/act override or projectiles. The client selects its
    normal animation from Skill.dat. Never immediately end a successful start.
    """
    return (b'\x02\x38' + encode_varint(actor_key) + b'\x00'
            + struct.pack('<iB', skill, action_uid) + b'\x02'
            + encode_varint(target_key) + struct.pack('<4f', yaw, *location)
            + encode_varint(attack_speed) + b'\x01' + encode_varint(cooldown))


def build_resource_update(resources, key=1):
    """0x364c, receiver 0x91f88f0: key, HP varints, 12 ints, parts array."""
    return (b'\x4c\x36' + encode_varint(key) + encode_varint(resources['hp'])
            + encode_varint(resources['hp_max'])
            + struct.pack('<12i', resources['mp'], resources['mp_max'],
                          0, 0, 0, 0, resources['sp'], resources['sp_max'],
                          0, 10000, 100000, 100000) + b'\x00')


def regenerate_resources(state, now_ms):
    resources = state.get('resources')
    if resources is None:
        return []
    last = state.setdefault('resource_tick_ms', now_ms)
    seconds = max(0, (now_ms - last) // 1000)
    if not seconds:
        return []
    state['resource_tick_ms'] = last + seconds * 1000
    previous = (resources['mp'], resources['sp'])
    # Conservative battle regeneration values already present in stat sheet.
    resources['mp'] = min(resources['mp_max'], resources['mp'] + seconds * 25)
    resources['sp'] = min(resources['sp_max'], resources['sp'] + seconds * 5000)
    return [build_resource_update(resources)] if previous != (resources['mp'], resources['sp']) else []


def parse_starter_cast(body):
    if len(body) < 26 or body[:2] != b"\x00\x38" or body[8] != 2:
        return None
    # Readable writer 0x8f7db28: this is a u16 presence mask, not action UID.
    flags = struct.unpack_from("<H", body, 2)[0]
    if flags & ~0x1ff:
        return None
    # Byte 8 = 2, then a varint target key (1 = the caster) and yaw/location: the same
    # shape as the confirmed 0x3802 start. Target keys other than 1 are a candidate reading.
    reader = Reader(body)
    reader.offset = 9
    try:
        target_key = reader.varuint()
        values = struct.unpack('<4f', reader.take(16))
    except (Incomplete, ValueError):
        return None
    if not all(math.isfinite(x) for x in values) or target_key < 1:
        return None
    try:
        if flags & 1:
            reader.uint(1)  # caster direction
        if flags & 6:
            reader.uint(1)  # auto-use/reserved share one packed bool byte
        toggle = reader.uint(1) if flags & 8 else 0
        uid = reader.uint(1) if flags & 16 else 0
        if flags & 32:
            reader.varuint()  # client act ID
        for bit in (64, 128):
            if flags & bit:
                if not math.isfinite(struct.unpack('<f', reader.take(4))[0]):
                    return None
        if flags & 256:
            reader.uint(8)  # client time
    except (Incomplete, ValueError):
        return None
    if reader.offset != len(body):
        return None
    return {"skill_id": struct.unpack_from("<i", body, 4)[0],
            "client_action_uid": uid, "change_toggle": toggle, "target_key": target_key,
            "yaw": values[0], "location": values[1:]}


def cast_control_replies(body, state, now_ms):
    request = parse_starter_cast(body)
    if request is None:
        return []
    skill, uid = request["skill_id"], request["client_action_uid"]
    # Recovered Skill.dat cost, cooldown and weapon requirement for any learned
    # skill. Playback only: no targets are hit and no effects are applied.
    definition = class_skills.skill(skill)
    cooldown_ms = definition['cooldown_ms'] if definition else 0
    cost = definition['cost'] if definition else {'mp': 0, 'hp': 0, 'sp': 0}
    weapons = [w for w in (definition or {}).get('weapons', []) if w != 'None']
    resources = state.get('resources')
    cooldowns = state.setdefault("skill_cooldowns", {})
    accepted = (definition is not None and resources is not None
                and resources['mp'] >= cost['mp'] and resources['sp'] >= cost['sp']
                and resources['hp'] > cost['hp']
                and skill in state.get("learned_skills", ())
                and (not weapons or class_skills.weapon_type(state.get("equipped_weapon")) in weapons)
                and now_ms >= cooldowns.get(skill, 0))
    if accepted:
        cooldowns[skill] = now_ms + cooldown_ms
        resources['mp'] -= cost['mp']
        resources['sp'] -= cost['sp']
        resources['hp'] -= cost['hp']
        state['action_uid'] = state.get('action_uid', 0) % 255 + 1
    state["last_cast"] = {**request, "accepted": accepted}
    # Both optional reply bytes are present (toggle and client action UID).
    reply = b"\x01\x38\x03" + struct.pack("<HiBB", 0 if accepted else 1, skill, 0, uid)
    if not accepted:
        # A rejection must not end an unrelated active server action.
        return [reply]
    start = build_skill_start(skill, state['action_uid'], request['yaw'], request['location'],
                              target_key=request['target_key'], cooldown=cooldown_ms)
    return [reply, build_resource_update(resources), start] + combat_replies(skill, request, state, now_ms)


AREA_RANGE = 400        # 4 m in world units (cm): the "within 4m" of the area skills' text
RESPAWN_MS = 5000


def respawn_targets(state, now_ms):
    """Corpses due for respawn: 0x3642 despawn (flag 3) then a fresh 0x3641 at full HP."""
    replies = []
    respawn_at = state.setdefault('respawn_at', {})
    for target in (state.get('targets') or {}).values():
        if target.dead and target.key in respawn_at and now_ms >= respawn_at[target.key]:
            del respawn_at[target.key]
            target.hp = target.hp_max
            replies += [combat.build_death(target.key), combat.build_npc_appear(target)]
    return replies


def combat_replies(skill, request, state, now_ms):
    """Damage from an accepted cast to the server's targets (the training dummy).

    The cast's target key selects a target; a self-centred cast (target key 1) hits
    targets within AREA_RANGE of the cast location. Returns 0x3804 hits and the
    0x8d00 HP update, as in the official recording; dead targets respawn after
    RESPAWN_MS (respawn_targets, also polled by the server loop).
    """
    targets = state.get('targets') or {}
    replies = respawn_targets(state, now_ms)
    if not targets or not (class_skills.skill(skill) or {}).get('damage'):
        return replies
    if request['target_key'] in targets:
        chosen = [targets[request['target_key']]]
    elif request['target_key'] == 1:
        chosen = [t for t in targets.values() if t.position
                  and sum((a - b) ** 2 for a, b in zip(t.position, request['location'])) <= AREA_RANGE ** 2]
    else:
        chosen = []
    # Only monsters take damage (citizens and other friendly NPCs share the targets map).
    chosen = [t for t in chosen if t.hostile]
    for target in chosen:
        if target.dead:
            continue
        hits = combat.skill_hits(skill, state.get('class'), state.get('level', 1), state.get('equipped_weapon'),
                                 target_defense=target.defense, rng=state.get('rng') or combat.random)
        for seq, hit in enumerate(hits, 1):
            target.hp = max(0, target.hp - hit['amount'])
            replies.append(combat.build_skill_hit(target.key, 1, skill, hit['effect_id'], hit['amount'],
                                                  hit['critical'], seq=seq))
        replies.append(combat.build_npc_hp(target.key, target.hp))
        state.setdefault('combat_log', []).append({'skill': skill, 'target': target.key,
                                                   'hits': [h['amount'] for h in hits], 'hp': target.hp})
        if target.dead:
            # HP 0 is the kill; the corpse is despawned and replaced on respawn.
            state['respawn_at'][target.key] = now_ms + RESPAWN_MS
        else:
            replies += npc_ai.engage(state, target, now_ms)  # a hit monster chases
    return replies
