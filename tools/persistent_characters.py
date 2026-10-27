"""Storage-backed native character adapter, prepared for the lobby dispatcher.

The 0x3615 list layout is decoded from the official recording (game_server
build_login_characters). Used by the game server for authenticated launches.
"""
import base64
import hashlib
import math
import uuid
try:
    from . import game_server as wire
    from .aion_protocol import Reader
    from .starter_skills import starter_profile
    from .starter_equipment import starter_weapon
    from .world_store import WorldStoreError
except ImportError:
    import game_server as wire
    from aion_protocol import Reader
    from starter_skills import starter_profile
    from starter_equipment import starter_weapon
    from world_store import WorldStoreError


class PersistentCharacters:
    def __init__(self, store, account_id):
        self.store = store
        self.account_id = uuid.UUID(str(account_id))
        self.selected = None

    @staticmethod
    def _profile(state):
        profile = starter_profile(state['creationId'], state['gender'])
        if not profile or profile['race'] != state['race']:
            raise ValueError('Persisted character profile is unsupported')
        return profile

    @staticmethod
    def _position(state):
        position = state['progress']['position']
        values = tuple(float(position[key]) for key in ('x', 'y', 'z'))
        if not all(math.isfinite(value) for value in values):
            raise ValueError('Invalid saved position')
        return values

    def lobby_characters(self):
        """Stored characters as _lobby_char_desc keyword records, official-shaped.

        Zone 3009, equip item level 1 and the starter weapon follow the official
        0x3615 records of level-one starter characters.
        """
        records = []
        for state in self.store.lobby():
            profile = self._profile(state)
            if state['lobbySlot'] is None:
                raise ValueError('Character has no assigned lobby slot')
            progress = state['progress']
            weapon = starter_weapon(profile)
            records.append(dict(
                key=state['wireId'], dbid=state['wireId'], nickname=state['summary']['name'],
                pc_id=profile['pc_id'], race=state['race'], gender=state['gender'],
                level=progress['level'], combat_level=progress['level'], exp=progress['experience'],
                equip_item_level=1 if weapon else 0, zone_id=3009,
                equips=((weapon, 0, 1),) if weapon else (),
                position=self._position(state), voice_type=state['voiceType'], slot_no=state['lobbySlot'],
                customizing_data=base64.b64decode(state['appearance'], validate=True)))
        return records

    def lobby_records(self):
        return [wire._lobby_char_desc(**record) for record in self.lobby_characters()]

    def account_race(self):
        """The race this account chose on this server (ERace 1 Light, 2 Dark), or 0.

        Official servers allow one race per account per server: the first character
        fixes it. Mixed stored races (created before this rule) are reported as 0.
        """
        races = {state['race'] for state in self.store.lobby()}
        return races.pop() if len(races) == 1 else 0

    def login_reply(self, account_session, world_id=1204, now_ms=None):
        """0x3615 listing this account's stored characters in the connection world.

        `_server_race` carries the account's race on this server (official sample: 1
        with Light characters); 0 for a new account leaves the choice open (CANDIDATE).
        """
        characters = self.lobby_characters()
        races = {c['race'] for c in characters}
        return wire.build_login_characters(characters, account_session, str(world_id), wire.COMM_SERVER_KEY,
                                           now_ms=now_ms,
                                           server_race=races.pop() if len(races) == 1 else 0)

    def save_position(self, position):
        """Checkpoint the selected character's position (revision-checked).

        On a stale revision (another writer, e.g. the staff dashboard changed the
        level) the fresh state is reloaded and only the position is replaced, so
        those edits are kept. Returns True when saved.
        """
        state = self.selected
        if state is None:
            return False
        x, y, z = (float(value) for value in position)
        if not all(math.isfinite(value) for value in (x, y, z)):
            raise ValueError('Invalid position')
        character = state['summary']['characterId']
        for attempt in range(2):
            progress = dict(state['progress'], position=dict(x=x, y=y, z=z))
            try:
                saved = self.store.save(character, state['revision'], progress)
                break
            except WorldStoreError as error:
                if error.status != 409 or attempt:
                    raise
                state = self.store.load(character)
        if isinstance(saved, dict) and 'progress' in saved:
            self.selected = saved
        else:
            self.selected = dict(state, progress=progress,
                                 revision=(saved or {}).get('revision', state['revision'] + 1))
        return True

    def selected_character(self):
        """The selected stored character in the game server's state shape."""
        state = self.selected
        if state is None:
            return None
        return dict(name=state['summary']['name'], gender=state['gender'],
                    pc_creation_id=state['creationId'], voice_type=state['voiceType'],
                    customizing_data=base64.b64decode(state['appearance'], validate=True),
                    level=state['progress']['level'], position=self._position(state),
                    map_id=state['progress']['mapId'])

    def create(self, body, now_ms=None):
        # Strict complete parsing here: unknown tails must not create a character.
        if body[:2] != b'\x1b\x36':
            raise ValueError('Expected character creation request')
        reader = Reader(body[2:])
        name = reader.take(reader.uint(1)).decode('utf-8')
        gender, creation = reader.uint(1), reader.uint(4)
        appearance = reader.take(reader.varuint())
        voice, slot, skip = reader.uint(1), reader.uint(1), reader.uint(1)
        if reader.offset != len(body) - 2 or skip not in (0, 1):
            raise ValueError('Unsupported character creation layout')
        profile = starter_profile(creation, gender)
        if profile is None:
            raise ValueError('Unknown creation profile')
        # One race per account per server, as on the official servers.
        race = self.account_race()
        if race and profile['race'] != race:
            raise ValueError('Account already chose the other race on this server')
        request = uuid.uuid5(self.account_id, 'native-create-v1:' + hashlib.sha256(body).hexdigest())
        result = self.store.create(request, f'dev-v1-{creation}:{gender}', name, appearance,
                                   voice_type=voice, lobby_slot=slot)
        state = self.store.load(result['characterId'])
        # Database success precedes any successful native reply.
        return wire.build_character_created(name=state['summary']['name'], gender=state['gender'],
            pc_creation_id=state['creationId'], key=state['wireId'], dbid=state['wireId'],
            customizing_data=base64.b64decode(state['appearance'], validate=True),
            voice_type=state['voiceType'], slot_no=state['lobbySlot'], now_ms=now_ms)

    def select(self, body):
        if body[:2] != b'\x1f\x36':
            raise ValueError('Expected character selection request')
        reader = Reader(body[2:])
        key = reader.varuint()
        reader.take(16)  # Request map/position are not authoritative spawn state.
        if reader.offset != len(body) - 2:
            raise ValueError('Unsupported character selection layout')
        # Dev runs: after a 0x361c record with key 1 the client selected 65536
        # (varint 80 80 04). CANDIDATE: the key arrives shifted left 16 bits.
        candidates = [key] + ([key >> 16] if key and not key & 0xFFFF else [])
        state = None
        for wire_id in candidates:
            if not 0 < wire_id <= 2147483647:
                continue
            try:
                state = self.store.select(wire_id)
                break
            except ValueError:
                continue
        if state is None:
            raise ValueError('No stored character for the selection key')
        if state['wireId'] not in candidates:
            raise ValueError('Character identity mismatch')
        profile = self._profile(state)
        position = self._position(state)
        map_id = state['progress']['mapId']
        if not isinstance(map_id, int) or map_id <= 0:
            raise ValueError('Saved character has no valid map')
        # The reply echoes the request key, as the working dev flow does.
        replies = [wire.build_select_character(key=key, dbid=state['wireId'], char_key=1,
            last_using_pc_id=profile['pc_id'], customizing_data=base64.b64decode(state['appearance'], validate=True)),
            wire.build_prepare_enter_map(enter_map_ticket=1, map_id=map_id, position=position)]
        self.selected = state
        return replies
