"""Server-side NPC behaviour: wander, aggro, chase and leash, sent as NPC moves.

Wire format, OBSERVED in the official recording (research/npc-movement.md):
- 0x371b move start and 0x371c move update (every 1 s while moving):
  `key varint | mode | x y z f32 | heading u16 | move heading u16 [| 01]`.
  Walk is `04 ... 01` and the client moves the NPC at NpcData BaseWalkSpeed; run is
  `05 03 ...` (no trailing byte) at BaseMoveSpeed. The heading is the travel direction
  as `degrees / 360 * 65536`. The leading/trailing bytes are reproduced, not decoded.
- 0x371a stop: `key | 00 00 | x y z | heading u16`.
- 0x371d (100 ms position deltas between updates) is not sent; we resend 0x371c
  when the heading changes instead.

Behaviour is OURS, not recovered: the aggro range, leash, wander radius and timings
below are development values. AggroDetectRange names a shape (F_Ground_Arc_01)
whose size is unknown. NPCs do not attack yet (the NPC skill messages are not
decoded), and heights are not ground-traced: Z is the spawn's or the player's.
"""
from dataclasses import dataclass, field
import math
import random
import struct

try:
    from . import combat
    from .aion_protocol import encode_varint
except ImportError:
    import combat
    from aion_protocol import encode_varint

UPDATE_MS = 1000          # official 0x371c cadence while moving
AGGRO_RANGE = 1000.0      # 10 m, Aggro_A monsters notice the player
LEASH_RANGE = 3000.0      # chase gives up this far from home
ACTIVE_RANGE = 8000.0     # idle NPCs farther from the player than this do not wander
WANDER_RADIUS = 400.0
CITIZEN_WANDER_RADIUS = 200.0
WANDER_IDLE_MS = (6000, 15000)
REACH = 120.0             # stop this far beyond the NPC's collision radius
TURN_RESEND_DEG = 10.0


@dataclass
class Brain:
    home: tuple
    home_heading: float
    mode: str = 'idle'          # idle, wander, chase, return, dead
    goal: tuple = None
    moving: bool = False
    run: bool = False
    heading: float = 0.0
    sent_heading: float = 0.0
    last_ms: int = 0
    update_ms: int = 0
    wander_ms: int = 0
    engaged: bool = False
    log: list = field(default_factory=list)


def heading_u16(degrees):
    return round(degrees % 360 / 360 * 65536) & 0xFFFF


def heading_to(origin, goal):
    return math.degrees(math.atan2(goal[1] - origin[1], goal[0] - origin[0])) % 360


def _move(opcode, key, position, heading, run):
    angle = heading_u16(heading)
    head = b'\x05\x03' if run else b'\x04'
    tail = b'' if run else b'\x01'
    return (struct.pack('<H', opcode) + encode_varint(key) + head + struct.pack('<3f', *position)
            + struct.pack('<HH', angle, angle) + tail)


def build_npc_move_start(key, position, heading, run=False):
    """0x371b: the NPC starts walking (run=False) or running along `heading`."""
    return _move(0x371b, key, position, heading, run)


def build_npc_move(key, position, heading, run=False):
    """0x371c: periodic position/heading of a moving NPC."""
    return _move(0x371c, key, position, heading, run)


def build_npc_stop(key, position, heading):
    """0x371a: the NPC stops at `position`, facing `heading`."""
    return (b'\x1a\x37' + encode_varint(key) + b'\x00\x00' + struct.pack('<3f', *position)
            + struct.pack('<H', heading_u16(heading)))


def _distance2d(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def brains(state):
    table = state.setdefault('npc_brains', {})
    for target in (state.get('targets') or {}).values():
        if target.key not in table and target.position:
            table[target.key] = Brain(home=tuple(target.position), home_heading=target.direction)
    return table


def engage(state, target, now_ms):
    """A hit monster (any aggro type) chases the player."""
    brain = brains(state).get(target.key)
    if brain is None or target.dead or not target.run_speed:
        return []
    return _start_chase(target, brain, now_ms)


def _start_chase(target, brain, now_ms):
    replies = []
    if not brain.engaged:
        brain.engaged = True
        replies.append(combat.build_combat_state(target.key, True))
    brain.mode = 'chase'
    brain.moving = False  # the next tick starts a run (0x371b) towards the player
    brain.last_ms = now_ms
    return replies


def _go(target, brain, goal, run, now_ms):
    """Start moving towards goal (sends 0x371b)."""
    brain.goal = goal
    brain.run = run
    brain.heading = heading_to(target.position, goal)
    brain.sent_heading = brain.heading
    brain.moving = True
    brain.last_ms = now_ms
    brain.update_ms = now_ms + UPDATE_MS
    return [build_npc_move_start(target.key, target.position, brain.heading, run)]


def _stop(target, brain, heading, now_ms):
    brain.moving = False
    brain.heading = heading
    brain.last_ms = now_ms
    return [build_npc_stop(target.key, target.position, heading)]


def _step(target, brain, goal, stop_at, z, now_ms):
    """Advance a moving NPC towards goal; True when within stop_at of it."""
    elapsed = max(0, now_ms - brain.last_ms) / 1000
    brain.last_ms = now_ms
    speed = target.run_speed if brain.run else target.walk_speed
    x, y, _ = target.position
    remaining = _distance2d((x, y), goal) - stop_at
    if remaining <= 0:
        return True
    travel = min(remaining, speed * elapsed)
    heading = heading_to((x, y), goal)
    target.position = (x + math.cos(math.radians(heading)) * travel,
                       y + math.sin(math.radians(heading)) * travel, z)
    brain.heading = heading
    return travel >= remaining


def _reset(target, brain, now_ms, rng):
    replies = []
    if brain.engaged:
        brain.engaged = False
        replies.append(combat.build_combat_state(target.key, False))
    if target.hp != target.hp_max:
        target.hp = target.hp_max
        replies.append(combat.build_npc_hp(target.key, target.hp))
    brain.mode = 'idle'
    brain.wander_ms = now_ms + rng.randint(*WANDER_IDLE_MS)
    return replies


def _tick_one(state, target, brain, now_ms, rng):
    player = state.get('position')
    if target.dead:
        if brain.mode != 'dead':
            brain.mode, brain.moving, brain.engaged = 'dead', False, False
            target.position = brain.home   # respawn (0x3641) uses the spawn point
            target.direction = brain.home_heading
        return []
    if brain.mode == 'dead':
        brain.mode = 'idle'
        brain.wander_ms = now_ms + rng.randint(*WANDER_IDLE_MS)
    if not (target.walk_speed or target.run_speed):
        return []
    if not target.hostile:
        # Townsfolk without a service (merchants etc. stay put) only stroll near their spot.
        if target.function != 'None':
            return []
        return _idle(state, target, brain, now_ms, rng, CITIZEN_WANDER_RADIUS)
    replies = []
    near = player is not None and _distance2d(target.position, player) <= AGGRO_RANGE
    if brain.mode in ('idle', 'wander') and target.aggressive and near and target.run_speed:
        replies += _start_chase(target, brain, now_ms)

    if brain.mode == 'chase':
        if player is None or _distance2d(target.position, brain.home) > LEASH_RANGE \
                or _distance2d(player, brain.home) > LEASH_RANGE:
            brain.mode = 'return'
            return replies + _go(target, brain, brain.home[:2], True, now_ms)
        stop_at = target.radius + REACH
        if not brain.moving:
            if _distance2d(target.position, player) > stop_at + 50:
                return replies + _go(target, brain, player[:2], True, now_ms)
            facing = heading_to(target.position, player)
            if abs((facing - brain.heading + 180) % 360 - 180) > 30:
                replies += _stop(target, brain, facing, now_ms)
            return replies
        arrived = _step(target, brain, player[:2], stop_at, player[2], now_ms)
        if arrived:
            return replies + _stop(target, brain, heading_to(target.position, player), now_ms)
        turned = abs((brain.heading - brain.sent_heading + 180) % 360 - 180) > TURN_RESEND_DEG
        if turned or now_ms >= brain.update_ms:
            brain.sent_heading, brain.update_ms = brain.heading, now_ms + UPDATE_MS
            replies.append(build_npc_move(target.key, target.position, brain.heading, True))
        return replies

    if brain.mode in ('return', 'wander'):
        if _step(target, brain, brain.goal, 0, brain.home[2], now_ms):
            target.position = (brain.goal[0], brain.goal[1], brain.home[2])
            replies += _stop(target, brain, brain.heading, now_ms)
            if brain.mode == 'return':
                replies += _reset(target, brain, now_ms, rng)
            else:
                brain.mode = 'idle'
                brain.wander_ms = now_ms + rng.randint(*WANDER_IDLE_MS)
        elif now_ms >= brain.update_ms:
            brain.update_ms = now_ms + UPDATE_MS
            replies.append(build_npc_move(target.key, target.position, brain.heading, brain.run))
        return replies

    return replies + _idle(state, target, brain, now_ms, rng, WANDER_RADIUS)


def _idle(state, target, brain, now_ms, rng, radius):
    """Idle and wander states: an occasional short walk around the spawn point, only
    near the player."""
    replies = []
    player = state.get('position')
    if brain.mode == 'wander':
        if _step(target, brain, brain.goal, 0, brain.home[2], now_ms):
            target.position = (brain.goal[0], brain.goal[1], brain.home[2])
            replies += _stop(target, brain, brain.heading, now_ms)
            brain.mode = 'idle'
            brain.wander_ms = now_ms + rng.randint(*WANDER_IDLE_MS)
        elif now_ms >= brain.update_ms:
            brain.update_ms = now_ms + UPDATE_MS
            replies.append(build_npc_move(target.key, target.position, brain.heading, False))
        return replies
    if not brain.wander_ms:
        brain.wander_ms = now_ms + rng.randint(*WANDER_IDLE_MS)
    if now_ms >= brain.wander_ms and target.walk_speed and player is not None \
            and _distance2d(target.position, player) <= ACTIVE_RANGE:
        angle, reach = rng.uniform(0, 2 * math.pi), rng.uniform(radius / 4, radius)
        goal = (brain.home[0] + math.cos(angle) * reach, brain.home[1] + math.sin(angle) * reach)
        brain.mode = 'wander'
        replies += _go(target, brain, goal, False, now_ms)
    return replies


def npc_ai_tick(state, now_ms, rng=random):
    """Movement messages for every NPC this tick (called from the server loop)."""
    replies = []
    for key, brain in brains(state).items():
        target = state['targets'].get(key)
        if target is not None:
            replies += _tick_one(state, target, brain, now_ms, rng)
    return replies
