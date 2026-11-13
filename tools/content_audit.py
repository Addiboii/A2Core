"""Content audit: every quest step and every map event step checked against what the server has.

    CONTENT_WALK_OUT=walk.tsv dotnet run --project tests/Aion.Native.Checks   (the dynamic walk, optional)
    python tools/content_audit.py [--out report.md] [--walk walk.tsv] [--json findings.json] [--tables research/local/tables-<stamp>]

Walks tools/data/quests.json (every quest, step and objective) and tools/data/map-events.json (every event on a served map:
stages, steps, objectives, the directives before/after them, start and fail conditions), and checks each named thing against
the server's own data, the way the server code looks it up (server/Aion.World/MapEvents.cs, GameSession.MapEvents.cs,
GameSession.cs CompleteUseAsync, Quests.cs):
  - NPC spawners / NPC ids: world-npcs.json spawns of the event's map (exact name, or the engine's "<name>_" prefix);
  - objects: world-objects.json objects of the map; a UseEnvObj objective names the EnvObjData template (env-usages.json), the
    server matches the template exactly, else the spawn name, else only by its fuzzy "kind word" rules (flagged);
  - trigger volumes, waypoint routes, teleport points (MapData points, Teleport.dat), subzones;
  - objective, directive and condition types against the ones the engine implements (MapEvents.cs is parsed);
  - dungeons/instance layers: an entry (NPC, trigger, door/portal, quest teleport), an arrival point (inside the layer's
    volume), a way out; EnvObjData usages of placed doors/portals (MoveMap points, EnterInstanceLayer/EnterDungeon ids);
  - quests: NPCs placed, objects placed, triggers and subzones on the map, ClearMapEvent targets served, startable by the
    step and paid by a DungeonClear, unimplemented objective types, reward items known.
Output: one line per broken step, in the order a player meets them (each race's hero chain first, then by level).
"""
from __future__ import annotations

import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data'
ENGINE = ROOT / 'server/Aion.World/MapEvents.cs'
# Destroyed objects report their EnvObjData template to the map events (GameSession.MapEventObjectName) since 2026-11-09.
DESTROY_USES_TEMPLATE = 'MapEventObjectName' in (ROOT / 'server/Aion.World/GameSession.EnvDestroy.cs').read_text(encoding='utf-8')

# Severity: BLOCK = the step cannot advance (the player is stuck or the quest never counts); TIMEOUT = the server finishes the
# objective by its 30 s fallback (not official behaviour); WARN = a directive does nothing (NPC not spawned/moved, caption
# missing...), the step still advances; FUZZY = only the engine's fuzzy name rules connect the objective to an object.
BLOCK, TIMEOUT, FUZZY, WARN = 'BLOCK', 'TIMEOUT', 'FUZZY', 'WARN'
RANK = {BLOCK: 0, FUZZY: 1, TIMEOUT: 2, WARN: 3}
# Listed only in the summary of unimplemented types (one per directive/condition, hundreds of lines otherwise).
SUMMARY_ONLY = {'summon-object', 'condition-unimplemented', 'directive-unimplemented'}


def load(name):
    return json.loads((DATA / name).read_text(encoding='utf-8'))


# ---------------------------------------------------------------- engine vocabulary (parsed from MapEvents.cs)

def engine_vocabulary(source: str):
    def block(start, end):
        i = source.index(start)
        j = source.index(end, i)
        return source[i:j]
    directive_src = block('void Execute(MapEventDirective d', 'void SetVariable(')
    directives = set(re.findall(r'case "(\w+)"', directive_src))
    noop = set(re.findall(r'"(\w+)"', directive_src.split('case "CameraShake"')[1].split('break;')[0])) | {'CameraShake'}
    condition_src = block('bool Condition(MapEventCondition c', 'static double Number(')
    conditions = set(re.findall(r'case "(\w+)"', condition_src))
    unsupported_src = block('public bool Unsupported =>', ';\n}')
    unsupported = set(re.findall(r'"(\w+)"', unsupported_src))
    client_src = block('public bool ClientSide =>', ';')
    client = set(re.findall(r'"(\w+)"', client_src))
    # Objective types some engine entry point counts (Killed, Talked, UsedObject, PositionAt, CombatStarted, NpcArrived, HpChanged,
    # CutsceneDone, EnvObjStateChanged): every o.Type == "X" / "X" or "Y" test outside the Unsupported/ClientSide lists.
    counted = set()
    for m in re.finditer(r'(?:o\.Type ==|o\.Type is|Type ==|case) "(\w+)"', block('// ---------- Events from the world', '// ---------- Steps')):
        counted.add(m.group(1))
    counted |= set(re.findall(r'"(\w+)" or "(?:\w+)"', block('public bool Killed(', 'public bool KillPending(')))
    counted |= set(re.findall(r'"(\w+)"', block('public bool Killed(', 'public bool KillPending(')))
    # Types tracked only with their data (MapEventRun.Tracked: a score with its UnifiedObjectivesList, a WaveStepData wave, ...).
    counted |= set(re.findall(r'"(\w+)" =>', block('static bool Tracked(', '};')))
    return {'directives': directives - noop, 'noop': noop, 'conditions': conditions, 'unsupported': unsupported, 'client': client, 'counted': counted}


# ---------------------------------------------------------------- the engine's name matching (MapEvents.cs, ported)

NOT_KINDS = {w.lower() for w in ["Layer", "Dungeon", "Env", "EnvObj", "Spawn", "SpawnMulti", "Single", "Multi", "Hit", "Starter", "InstanceLayer", "Common", "All"]}
KIND_SYNONYMS = {'corpse': ['Deadbody', 'Body'], 'deadbody': ['Corpse'], 'barrier': ['LedaPortal']}


def name_matches(name, spawn):
    return name.lower() == spawn.lower() or spawn.lower().startswith(name.lower() + '_')


def base_word(name):
    for p in reversed([p for p in name.split('_') if p]):
        if not p.isdigit():
            return p
    return ''


def kind_matches(template, spawn):
    words = lambda s: [w for w in s.split('_') if w and not w.isdigit()]
    kinds = [w for w in words(template)[2:] if len(w) > 3 and w.lower() not in NOT_KINDS][-1:]
    if not kinds:
        return False
    allk = kinds + [s for k in kinds for s in KIND_SYNONYMS.get(k.lower(), [])]
    sw = [w for w in words(spawn) if len(w) > 3 and w.lower() not in NOT_KINDS]
    for k in allk:
        for w in sw:
            if w.lower().startswith(k.lower()) or (k.lower().startswith(w.lower()) and len(w) >= 5) or (len(k) >= 5 and k.lower() in w.lower()):
                return True
    return False


def fuzzy_names(objective_names, spawn):
    for n in objective_names:
        if n.startswith('E_') and len(base_word(n)) > 2 and base_word(n).lower() == base_word(spawn).lower():
            return True
        if kind_matches(n, spawn):
            return True
    return False


# ---------------------------------------------------------------- the world as the server holds it

class World:
    def __init__(self, tables: Path | None):
        self.me = load('map-events.json')
        self.quests = load('quests.json')['quests']
        npcs = load('world-npcs.json')
        self.npc_maps = npcs['maps']
        self.routes = {m: {r['name'].lower() for r in rs} for m, rs in npcs['routes'].items()}
        self.templates = npcs['templates']
        wo = load('world-objects.json')['maps']
        self.objects = {m: e['objects'] for m, e in wo.items()}
        self.triggers = {m: {t['name'].lower(): t for t in e['triggers']} for m, e in wo.items()}
        self.env = {int(k): v for k, v in load('env-usages.json')['objects'].items()}
        self._env_names = None
        self.map_points = load('map-points.json')['maps']
        subzones = load('subzones.json')
        self.subzones = {m: {z['name'].lower() for z in zs} for m, zs in subzones['maps'].items()}
        # Subzones.InSubzone: a map's default subzone is the whole map; a layer also has its base map's areas.
        for m, name in (subzones.get('defaults') or {}).items():
            self.subzones.setdefault(m, set()).add(name.lower())
        self.subzone_maps = defaultdict(set)
        for m, zs in load('subzones.json')['maps'].items():
            for z in zs:
                self.subzone_maps[z['name'].lower()].add(int(m))
        for m, name in (subzones.get('defaults') or {}).items():
            self.subzone_maps[name.lower()].add(int(m))
        self.items = {m for f in sorted((ROOT / 'gamedata/items').glob('*.xml')) for m in re.findall(r'<item>\s*<id>(\d+)</id>', f.read_text(encoding='utf-8'))}
        self.artifacts = {a['artifact'] for l in load('teleport-artifacts.json')['maps'].values() for a in l}
        self.instances = load('instances.json')['instances']
        self.served = set(self.me['served_maps'])
        self.dungeon_by_map = {}
        for i, d in self.me['dungeons'].items():
            self.dungeon_by_map.setdefault(d['map'], int(i))
        self.steps_by_id = {}
        for code, q in self.quests.items():
            for s in q['steps']:
                if s.get('id'):
                    self.steps_by_id.setdefault(s['id'], (q['id'], s['order']))
        self.quest_by_id = {q['id']: code for code, q in self.quests.items()}
        self.npc_names = {m: {e[0].lower() for e in l} for m, l in self.npc_maps.items()}
        self.npc_ids = {m: {e[1] for e in l} for m, l in self.npc_maps.items()}
        self.npc_where = defaultdict(set)
        for m, l in self.npc_maps.items():
            for e in l:
                self.npc_where[e[1]].add(int(m))
        self.env_where = defaultdict(set)
        for m, l in self.objects.items():
            for o in l:
                self.env_where[o['env']].add(int(m))
        self.map_names = {}
        if tables and (tables / 'Table_Map.json').exists():
            t = json.loads((tables / 'Table_Map.json').read_text(encoding='utf-8'))
            for r in t['Properties']['Data']:
                self.map_names[r['ID']['Value']] = r['Name']
        self.bases = {int(k): v for k, v in load('map-bases.json')['bases'].items()}

    def map_name(self, m):
        return self.map_names.get(int(m), '')

    def step_of(self, step_id):
        try:
            sid = int(step_id)
        except (TypeError, ValueError):
            return None
        if sid in self.steps_by_id:
            return self.steps_by_id[sid]
        return (sid // 100, sid % 100) if sid > 100 else None

    def quest_step_exists(self, q, s):
        code = self.quest_by_id.get(q)
        return code is not None and any(st['order'] == s for st in self.quests[code]['steps'])

    def points(self, m):
        return self.me['points'].get(str(m)) or self.map_points.get(str(m)) or {}

    def all_points(self, m):
        return {**self.map_points.get(str(m), {}), **self.me['points'].get(str(m), {})}

    def env_names(self):
        """Every EnvObjData template name (lower case): what SummonAroundTarget may summon as an object."""
        if self._env_names is None:
            self._env_names = {v.get('name', '').lower() for v in self.env.values()}
        return self._env_names

    # Spawner lookups on a map: NPC spawn entries, objects, trigger actors (exact name as SpawnerNpcs/SpawnerObjects compare).
    def spawner_kind(self, m, name):
        k = name.lower()
        if k in self.npc_names.get(str(m), ()):
            return 'npc'
        if any(o['name'].lower() == k for o in self.objects.get(str(m), [])):
            return 'object'
        if k in self.triggers.get(str(m), {}):
            return 'trigger'
        return None

    def npc_prefix(self, m, name):
        return any(name_matches(name, s) for s in self.npc_names.get(str(m), ()))

    def arrival_point(self, dungeon):
        """GameSession.ArrivalPoint: the hand-entered arrival, the race start point, PCStart, *_Respawn, *Start*, any point, MapStart."""
        m = dungeon['map']
        if str(m) in self.instances:
            return self.instances[str(m)]['arrival'], 'instances.json'
        pts = self.me['points'].get(str(m)) or self.map_points.get(str(m)) or {}
        for named in (dungeon.get('start_light'), dungeon.get('start_dark')):
            if named in pts:
                return pts[named], named
        for pred in (lambda k: 'pcstart' in k.lower(), lambda k: k.lower().endswith('_respawn'), lambda k: 'start' in k.lower(), lambda k: True):
            for k, p in pts.items():
                if pred(k):
                    return p, k
        mp = self.map_points.get(str(m), {})
        for word in ('start', 'home', 'respawn'):
            for k, p in mp.items():
                if word in k.lower().split('_'):
                    return p, k
        if mp:
            k = next(iter(mp))
            return mp[k], k
        return None, None


def in_volume(volume, p):
    pts = volume['points']
    inside = False
    j = len(pts) - 1
    for i in range(len(pts)):
        xi, yi, xj, yj = pts[i][0], pts[i][1], pts[j][0], pts[j][1]
        if (yi > p[1]) != (yj > p[1]) and p[0] < (xj - xi) * (p[1] - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


# ---------------------------------------------------------------- findings

class Findings:
    def __init__(self):
        self.items = []

    def add(self, severity, kind, owner, step, what, evidence='', area=None):
        self.items.append({'severity': severity, 'kind': kind, 'owner': owner, 'step': step, 'what': what, 'evidence': evidence, 'area': area})


def walk_directives(directives):
    for d in directives or []:
        yield d


def event_directives(e):
    """(where, directive) for every directive of an event."""
    for d in e.get('complete') or []:
        yield 'on complete', d
    for d in e.get('failure') or []:
        yield 'on failure', d
    for r in e.get('global') or []:
        for d in r.get('d', []):
            yield 'global rule', d
    for st in e['stages']:
        for d in st.get('faildir') or []:
            yield f'stage {st["id"]} fail', d
        for sp in st['steps']:
            for d in sp.get('pre') or []:
                yield f'stage {st["id"]} step {sp["id"]} pre', d
            for d in sp.get('post') or []:
                yield f'stage {st["id"]} step {sp["id"]} post', d
            for o in sp['obj']:
                for d in (o.get('pre') or []) + (o.get('post') or []):
                    yield f'stage {st["id"]} step {sp["id"]} obj {o["id"]}', d


def conditions_of(e):
    def groups(gs, where):
        for g in gs or []:
            for c in g:
                yield where, c
    yield from groups(e.get('start'), 'start')
    yield from groups(e.get('fail'), 'fail')
    for r in e.get('global') or []:
        yield from groups(r.get('c'), 'global rule')
    for st in e['stages']:
        yield from groups(st.get('fail'), f'stage {st["id"]} fail')
        for where, d in event_directives({'stages': [st]}):
            yield from groups(d.get('c'), where)


SPAWNER_VALUES = {
    'StartSpawner': [0], 'EndSpawner': [0], 'KillSpawner': [0], 'DespawnAll': [0, 1], 'NpcMoveToWayPoint': [0], 'FollowPc': [0],
    'StopFollowPc': [0], 'NpcRotate': [0, 1], 'PlayChatbubble': [0], 'PlaySocialAction': [0], 'StopSocialAction': [0], 'SetAggroPc': [0],
    'SetAggroNpc': [0, 1], 'ChangeRelationshipEntity': [0], 'AddAbnormal': [2], 'RemoveAbnormal': [1], 'ChangeEnvObjState': [0],
    'DynamicTerrainBlock': [0], 'SummonAroundTarget': [0, 3],
}


def split(text):
    return [t.strip() for t in (text or '').split(',') if t.strip()]


def audit_event(world: World, vocab, name, e, f: Findings, owner_area):
    m = e['map']
    sm = str(m)
    owner = name
    # Spawners a script mentions (to skip the same missing name in every directive): name -> first place.
    missing_reported = set()
    moved = defaultdict(set)   # spawner -> routes moved along
    summoned = set()
    for where, d in event_directives(e):
        v = d['v']
        if d['t'] == 'NpcMoveToWayPoint':
            moved[(v[0] or '').lower()].add((v[1] or '').lower())
        if d['t'] == 'SummonAroundTarget' and len(v) > 3:
            summoned.add((v[3] or '').lower())

    # Directives
    for where, d in event_directives(e):
        t, v = d['t'], d['v']
        step = where
        if t not in vocab['directives'] and t not in vocab['noop'] and t != 'Delay':
            f.add(WARN, 'directive-unimplemented', owner, step, f'directive {t} is not implemented (logged and skipped)', f'values {v}', owner_area)
            continue
        for i in SPAWNER_VALUES.get(t, []):
            if i >= len(v):
                continue
            for n in split(v[i]):
                if n.lower() == 'all' or (t == 'NpcRotate' and i == 1 and not n):
                    continue
                if t == 'ChangeEnvObjState' and any(world.env.get(o['env'], {}).get('name', '').lower() == n.lower() for o in world.objects.get(sm, [])):
                    continue
                if t == 'SummonAroundTarget' and i == 3 and n.lower() in world.env_names():
                    continue   # an EnvObjData template (a recovery orb): summoned as an object (GameSession.EnvBoxes.cs)
                if t == 'SummonAroundTarget' and i == 3 and n.startswith('Basic_'):
                    f.add(WARN, 'summon-object', owner, step, f'SummonAroundTarget of the object {n}: no EnvObjData template of that name', '', owner_area)
                    continue
                if world.spawner_kind(m, n) is None and n.lower() not in missing_reported:
                    missing_reported.add(n.lower())
                    near = world.npc_prefix(m, n)
                    f.add(WARN, 'spawner-missing', owner, step, f'{t}: spawner {n} is not on map {m}' + (' (only "<name>_..." spawns)' if near else ''),
                          'world-npcs/world-objects have no entry of that exact name', owner_area)
        if t == 'NpcMoveToWayPoint' and len(v) > 1 and v[1] and v[1].lower() not in world.routes.get(sm, ()):
            f.add(WARN, 'route-missing', owner, step, f'NpcMoveToWayPoint {v[0]}: route {v[1]} is not on map {m}', 'world-npcs routes', owner_area)
        if t == 'PCTeleport':
            p = v[0] if v else ''
            if p not in world.me['points'].get(sm, {}):
                tp = world.me['teleports'].get(p)
                if tp is None:
                    f.add(WARN, 'point-missing', owner, step, f'PCTeleport to {p}: no location in the client data (Teleport.dat row without a placed actor): '
                          'the player is not moved, its cutscenes play in place', 'map-events points/teleports', owner_area)
                elif tp.get('at') is None:
                    target = tp['map']
                    d2 = world.dungeon_by_map.get(target)
                    ok = world.arrival_point(world.me['dungeons'][str(d2)])[0] if d2 else None
                    if ok is None and not world.map_points.get(str(target)):
                        f.add(BLOCK, 'point-missing', owner, step, f'PCTeleport to {p} (map {target}): no arrival point there', 'Teleport.dat PlayerStart, no MapData point', owner_area)
            if len(v) > 1 and v[1] and v[1] not in world.me['teleport_effects']:
                f.add(WARN, 'effect-missing', owner, step, f'PCTeleport effect {v[1]} unknown (sent as 0)', 'map-events teleport_effects', owner_area)
        if t == 'MapExit' and len(v) > 1 and v[1]:
            f.add(WARN, 'mapexit-cutscene', owner, step, f'MapExit plays cutscene {v[1]} before the exit: the server ignores it (IMapEventHost.MapExit takes no cutscene)',
                  f'values {v}', owner_area)
    # Conditions
    for where, c in conditions_of(e):
        t = c['t']
        if t not in vocab['conditions']:
            f.add(WARN, 'condition-unimplemented', owner, where, f'condition {t} is never true on the server', f'values {c["v"]}', owner_area)
        if t in ('IncompleteQuestStep', 'NotIncompleteQuestStep'):
            qs = world.step_of(c['v'][0] if c['v'] else '')
            if qs is None or not world.quest_step_exists(*qs):
                f.add(BLOCK if where == 'start' else WARN, 'queststep-missing', owner, where, f'{t} {c["v"][0] if c["v"] else ""}: no such quest step',
                      'quests.json step ids', owner_area)

    # Talk partners removed while their talk is still open (user 2026-11-09: "the NPC already moved on, nobody to talk to"): an
    # EndSpawner/KillSpawner/DespawnAll of the AskNpc/CloseNpc target in the step's pre-directives or another objective's post, with
    # no other spawner of that NPC started in the step.
    for st in e['stages']:
        for sp in st['steps']:
            running = list(sp['pre']) + [d for o in sp['obj'] if o['t'] not in ('AskNpc', 'CloseNpc') for d in (o.get('post') or [])]
            started = [n for d in running if d['t'] in ('StartSpawner', 'SummonAroundTarget') for n in split((d['v'] or [''])[0 if d['t'] == 'StartSpawner' else 3] if d['v'] else '')]
            for o in sp['obj']:
                if o['t'] not in ('AskNpc', 'CloseNpc') or o.get('opt'):
                    continue
                names = split(o['v'][0] if o['v'] else '')
                for d in running:
                    if d['t'] in ('EndSpawner', 'KillSpawner', 'DespawnAll') and any(name_matches(n, x) or name_matches(x, n) for n in names for x in split(d['v'][0] if d['v'] else '')):
                        if any(name_matches(n, x) for n in names for x in started if x.lower() != (d['v'][0] or '').lower()):
                            continue   # swapped for another spawn of the same NPC
                        gone = {x.lower() for x in split(d['v'][0] if d['v'] else '')}
                        if any(entry[1] in (o.get('npcs') or []) and entry[0].lower() not in gone for entry in world.npc_maps.get(sm, [])):
                            continue   # another placement of the same NPC stays to talk to
                        f.add(BLOCK, 'talk-partner-removed', owner, f'stage {st["id"]} ({st["key"]}) step {sp["id"]}',
                              f'{o["t"]} {o["v"][0]}: {d["t"]} {d["v"][0]} removes the talk partner while the talk is open', '', owner_area)

    # Objectives
    for st in e['stages']:
        for sp in st['steps']:
            step = f'stage {st["id"]} ({st["key"]}) step {sp["id"]}'
            for o in sp['obj']:
                check_event_objective(world, vocab, e, o, step, f, owner, owner_area, moved, summoned)


def check_event_objective(world, vocab, e, o, step, f, owner, area, moved, summoned):
    m = e['map']
    sm = str(m)
    t, v = o['t'], o['v']
    names = split(v[0] if v else '')
    optional = o.get('opt', False)
    sev = WARN if optional else BLOCK
    label = f'{t} {v[0] if v else ""}'.strip() + (' (optional)' if optional else '')
    if t in vocab['client']:
        return
    if t in vocab['unsupported']:
        f.add(WARN if optional else TIMEOUT, 'objective-unimplemented', owner, step, f'{label}: not tracked by the server, finished by the 30 s timeout', f'values {v}', area)
        return
    if t in ('KillNpc', 'HpRemaining', 'KillSpawner', 'AskNpc', 'CloseNpc'):
        ids = set(o.get('npcs') or [])
        placed = ids & world.npc_ids.get(sm, set())
        exact = [n for n in names if n.lower() in world.npc_names.get(sm, ())]
        prefix = [n for n in names if world.npc_prefix(m, n)]
        if t == 'KillSpawner':
            if not exact:
                f.add(sev, 'spawner-missing', owner, step, f'{label}: no spawn entry of that name on map {m}', 'world-npcs', area)
            return
        if not (placed or exact or prefix):
            f.add(sev, 'npc-missing', owner, step, f'{label}: NPC not on map {m}', f'npc ids {sorted(ids)} not placed there', area)
            return
        if t == 'KillNpc':
            count = int(v[1]) if len(v) > 1 and str(v[1]).isdigit() and int(v[1]) > 0 else 1
            entries = [x for x in world.npc_maps.get(sm, []) if x[1] in ids or any(name_matches(n, x[0]) for n in names)]
            if len(entries) < count and not any(n.lower() in summoned for n in names) and not {x[0].lower() for x in entries} & summoned:
                f.add(WARN, 'kill-count', owner, step, f'{label}: needs {count} kills, {len(entries)} placed and none summoned (needs respawns)', '', area)
        return
    if t == 'NpcArriveWaypoint':
        spawner = names[0] if names else ''
        route = v[1] if len(v) > 1 else ''
        if not world.npc_prefix(m, spawner):
            f.add(sev, 'npc-missing', owner, step, f'{label}: NPC {spawner} not on map {m}', 'world-npcs', area)
        elif route and route.lower() not in world.routes.get(sm, ()):
            f.add(sev, 'route-missing', owner, step, f'{label}: route {route} not on map {m}: the NPC never arrives', 'world-npcs routes', area)
        elif not any(name_matches(spawner, s) or s == spawner.lower() for s in moved) or (route and not any(route.lower() in r for s, r in moved.items() if name_matches(spawner, s) or s == spawner.lower())):
            f.add(sev, 'route-unused', owner, step, f'{label}: no NpcMoveToWayPoint sends {spawner} along {route or "a route"}', '', area)
        return
    if t == 'UseEnvObj':
        objs = world.objects.get(sm, [])
        exact, fuzzy, destroy_only = [], [], []
        for ob in objs:
            tmpl = world.env.get(ob['env'], {}).get('name', '')
            by_template = any(n.lower() == tmpl.lower() for n in names)
            by_spawn = any(name_matches(n, ob['name']) for n in names)
            if by_template or by_spawn:
                # Destroyed objects (relationship 15) and bomb towers report the spawn name only (EnvDestroy.cs, Bombs.cs).
                if ob.get('relationship') == 15 and not by_spawn and not DESTROY_USES_TEMPLATE and not fuzzy_names(names, ob['name']):
                    destroy_only.append(ob['name'])
                else:
                    exact.append(ob['name'])
            elif fuzzy_names(names, ob['name']):
                fuzzy.append(ob['name'])
        count = int(v[1]) if len(v) > 1 and str(v[1]).isdigit() and int(v[1]) > 0 else 1
        if exact:
            capacity = sum((world.env.get(ob['env'], {}).get('count') or 10 ** 6) for ob in objs if ob['name'] in exact)
            if capacity < count and not optional:
                f.add(WARN, 'use-count', owner, step, f'{label}: needs {count} uses, {len(exact)} object(s) placed', ', '.join(exact[:4]), area)
            return
        if destroy_only:
            f.add(sev, 'destroy-name', owner, step, f'{label}: the destructible object {destroy_only[0]} reports its spawn name, not the template: never counts',
                  'GameSession.EnvDestroy.cs MapEventUsedObject(o.Name)', area)
            return
        if fuzzy:
            f.add(FUZZY, 'object-fuzzy', owner, step, f'{label}: matches only by the fuzzy kind word', f'objects {", ".join(fuzzy[:4])}', area)
            return
        f.add(sev, 'object-missing', owner, step, f'{label}: no object on map {m} (template or spawn name)', 'world-objects + env-usages', area)
        return
    if t == 'EnterVolumePC':
        tr = names[0] if names else ''
        if tr.lower() not in world.triggers.get(sm, {}):
            f.add(sev, 'trigger-missing', owner, step, f'{label}: trigger volume not on map {m}', 'world-objects triggers', area)
        return
    if t == 'EnterSubZone':
        z = names[0] if names else ''
        vol = world.me['volumes'].get(sm)
        if z.lower() not in world.subzones.get(sm, ()) and not (vol and z.lower() in world.subzones.get(str(vol['base']), ())):
            f.add(sev, 'subzone-missing', owner, step, f'{label}: subzone not on map {m}', 'subzones.json', area)
        return
    if t in ('PCCombatStart',):
        return
    if t in ('KillNpcRelationshipEntity', 'KillNpcSubtype', 'KillNpcCreatureType'):
        f.add(WARN, 'objective-approx', owner, step, f'{label}: any hostile kill counts (the relationship/subtype is not compared)', f'values {v}', area)
        return
    if t == 'UnableComplete':
        return
    if t == 'ClearWave' and (v[0] if v else '') not in (world.me.get('waves') or {}):
        f.add(WARN if optional else TIMEOUT, 'objective-unimplemented', owner, step, f'{label}: wave not in WaveStepData, finished by the 30 s timeout', f'values {v}', area)
        return
    if t not in vocab['counted']:
        f.add(sev, 'objective-unimplemented', owner, step, f'{label}: objective type not handled at all', f'values {v}', area)


# ---------------------------------------------------------------- dungeons and doors

def audit_dungeons(world: World, f: Findings, quest_targets):
    me = world.me
    entries = defaultdict(list)
    for base, l in me['entries'].items():
        for x in l:
            entries[x['dungeon']].append(f'trigger {x["trigger"]} on {base}')
    for npc, x in me['npc_entries'].items():
        if x.get('dungeon'):
            where = sorted(world.npc_where.get(int(npc), []))
            entries[x['dungeon']].append(f'NPC {npc} on {where}' if where else f'NPC {npc} (not placed!)')
    for m, l in world.objects.items():
        for o in l:
            for d in o.get('portal') or []:
                entries[d].append(f'portal {o["name"]} on {m}')
            u = world.env.get(o['env'])
            if u and u['usage'] in ('EnterInstanceLayer', 'EnterDungeon', 'PartyDungeon') and not o.get('portal'):
                val = u.get('value') or ''
                if val.isdigit():
                    entries[int(val)].append(f'door {o["name"]} on {m}')
                    if val not in me['dungeons']:
                        f.add(BLOCK, 'door-dungeon', o['name'], f'map {m}', f'{u["usage"]} door names dungeon {val}: not in Dungeon.dat', u['name'], ('zz', int(m)))
                    elif me['dungeons'][val]['map'] not in world.served:
                        f.add(WARN, 'door-unserved', o['name'], f'map {m}', f'{u["usage"]} door into {me["dungeons"][val]["name"]} (map {me["dungeons"][val]["map"]}): map not served, the door refuses',
                              u['name'], ('zz', int(m)))
                else:
                    f.add(WARN, 'door-value', o['name'], f'map {m}', f'{u["usage"]} door value {val!r} is not a dungeon id and no portal is listed: the door does nothing', u['name'], ('zz', int(m)))
            if u and u['usage'] == 'MoveMap':
                p = u.get('value') or ''
                ok = p in world.me['points'].get(m, {}) or (world.me['teleports'].get(p) or {}).get('at') is not None
                if not ok:
                    f.add(BLOCK, 'exit-point', o['name'], f'map {m}', f'exit door (MoveMap) point {p}: not a point of map {m} nor a Teleport.dat location: the door does nothing',
                          f'{u["name"]}; session-notes "exit door: no point"', ('zz', int(m)))
    for i, d in me['dungeons'].items():
        di = int(i)
        m = d['map']
        if m not in world.served:
            continue
        events = me['maps'].get(str(m), [])
        quest = quest_targets.get(m)
        has_entry = bool(entries.get(di)) or str(m) in world.instances or quest
        owner = f'dungeon {di} {d["name"]} (map {m})'
        area = ('dungeon', m)
        if not has_entry and d['type'] not in ('Matching', 'Raid', 'Guild', 'PersonalAgit', 'Growth'):
            f.add(WARN, 'dungeon-no-entry', owner, 'entry', 'no NPC, trigger, door, portal or quest leads in (staff only)', '', area)
        vol = me['volumes'].get(str(m))
        at, which = world.arrival_point(d)
        if at is None:
            f.add(BLOCK if has_entry and not vol else WARN, 'dungeon-no-arrival', owner, 'arrival', 'no arrival point: the player lands at his old coordinates on the new map', 'map-events points / map-points', area)
        elif vol and not in_volume(vol, at):
            f.add(WARN, 'arrival-outside', owner, 'arrival', f'arrival point {which} {at} is outside the layer volume (entered from another map / quest teleport: the client leaves the layer at once)',
                  'map-events volumes', area)
        if not vol and d['type'] not in ('Matching', 'Guild', 'PersonalAgit', 'Growth', 'AbyssArtifact'):
            outs = [x for n in events for _, x in event_directives(me['events'][n]) if x['t'] in ('MapExit', 'DungeonClear', 'PCTeleport')]
            exit_obj = any((world.env.get(o['env']) or {}).get('usage') == 'MoveMap' or o['name'].endswith('_ExitDoor') for o in world.objects.get(str(m), []))
            if not outs and not exit_obj and str(m) not in world.instances:
                f.add(WARN, 'dungeon-no-exit', owner, 'exit', 'no MapExit/DungeonClear in its scripts and no exit door: only relog leaves', '', area)


# ---------------------------------------------------------------- quests

QUEST_HANDLED = {'KillNpc', 'AskNpc', 'ClearMapEvent', 'UseEnvObj', 'EnterVolumePC', 'CompleteCelviceTalk', 'CloseNote', 'UseFakeItem', 'CollectItem',
                 'EnterSubZone', 'CloseNpc', 'ClearQuest', 'KillPCAbyssGrade', 'CollectFakeItem', 'ClearQuestType', 'ClearQuestExplorationType', 'PCLevel', 'KillPC', 'EngraveTeleportArtifact'}


def related_maps(world, m):
    """A quest map and the maps that show it: its instance layers (base = m) and dungeons."""
    out = {m}
    for k, b in world.bases.items():
        if b == m:
            out.add(k)
    return out


def event_starts_for(world, e, quest_id, order):
    """Whether the event can start while quest_id is at step order: no quest-step condition, or a start group needing it."""
    groups = e.get('start') or []
    if not groups:
        return True
    for g in groups:
        needs = [world.step_of(c['v'][0]) for c in g if c['t'] == 'IncompleteQuestStep' and c['v']]
        if not needs or (quest_id, order) in needs:
            return True
    return False


def audit_quests(world: World, f: Findings, area_of):
    clears = set()
    for code, q in world.quests.items():
        area = area_of(code)
        owner = f'{code} {q["title"] or ""}'.strip()
        qmap = q['map']
        maps = related_maps(world, qmap)
        if q['acquire'] == 'Npc' and q.get('start_npc') and not world.npc_where.get(q['start_npc']):
            f.add(BLOCK, 'quest-npc-missing', owner, 'start', f'start NPC {q.get("start_npc_name")} ({q["start_npc"]}) is not placed anywhere', 'world-npcs', area)
        if q.get('finish_npc') and q['complete'] != 'Auto' and not world.npc_where.get(q['finish_npc']):
            f.add(BLOCK, 'quest-npc-missing', owner, 'finish', f'finish NPC {q.get("finish_npc_name")} ({q["finish_npc"]}) is not placed anywhere', 'world-npcs', area)
        rewards = q.get('rewards') or {}
        for it in (rewards.get('items') or []) + (rewards.get('select') or []):
            if str(it[0]) not in world.items and it[0] != 930100001:
                f.add(WARN, 'reward-item', owner, 'reward', f'reward item {it[0]} unknown to the server (gamedata/items)', '', area)
        for s in q['steps']:
            step = f'step {s["order"]}'
            if len(s['objectives']) > 6:
                f.add(BLOCK, 'quest-goals', owner, step, f'{len(s["objectives"])} objectives: only the first 6 count', '', area)
            for idx, o in enumerate(s['objectives']):
                t = o['type']
                optional = o.get('optional', False)
                sev = WARN if optional else BLOCK
                omap = o.get('map') or qmap
                label = f'{t} ' + str(o.get('target') or o.get('npc_name') or ','.join(o.get('npc_names') or o.get('env_names') or []) or '')
                if t not in QUEST_HANDLED:
                    f.add(sev, 'quest-objective-unimplemented', owner, step, f'{label}: objective type never counts on the server', json.dumps(o, ensure_ascii=False)[:160], area)
                    continue
                if t in ('KillNpc', 'CollectFakeItem'):
                    ids = o.get('npcs') or []
                    where = set().union(*[world.npc_where.get(i, set()) for i in ids]) if ids else set()
                    if not where:
                        f.add(sev, 'quest-npc-missing', owner, step, f'{label}: none of NPC ids {ids} is placed on any map', 'world-npcs', area)
                    elif not (where & related_maps(world, omap)) and not any(world.bases.get(w) == omap or w in world.served for w in where):
                        f.add(WARN, 'quest-npc-elsewhere', owner, step, f'{label}: placed only on maps {sorted(where)[:5]}, not on {omap}', '', area)
                elif t in ('AskNpc', 'CloseNpc'):
                    npc = o.get('npc')
                    where = world.npc_where.get(npc, set())
                    if not where:
                        f.add(sev, 'quest-npc-missing', owner, step, f'{label}: NPC {npc} is not placed on any map', 'world-npcs', area)
                    elif not any(w in world.served or world.bases.get(w, 0) in (0, w) or w == omap for w in where):
                        f.add(WARN, 'quest-npc-elsewhere', owner, step, f'{label}: NPC only on unserved maps {sorted(where)[:5]}', '', area)
                elif t == 'UseEnvObj':
                    envs = o.get('envs') or []
                    where = set().union(*[world.env_where.get(i, set()) for i in envs]) if envs else set()
                    if not where:
                        f.add(sev, 'quest-object-missing', owner, step, f'{label}: object (EnvObjData {envs}) is not placed on any map', 'world-objects', area)
                elif t == 'EnterVolumePC':
                    tr = (o.get('target') or '').lower()
                    on = [mm for mm, ts in world.triggers.items() if tr in ts]
                    if not on:
                        f.add(sev, 'quest-trigger-missing', owner, step, f'{label}: trigger volume on no map', 'world-objects triggers', area)
                    elif str(omap) not in on:
                        f.add(WARN, 'quest-trigger-elsewhere', owner, step, f'{label}: trigger on map(s) {on[:4]}, quest says {omap}', '', area)
                elif t == 'EnterSubZone':
                    z = (o.get('target') or '').lower()
                    if not world.subzone_maps.get(z):
                        f.add(sev, 'quest-subzone-missing', owner, step, f'{label}: subzone on no map', 'subzones.json', area)
                elif t == 'ClearMapEvent':
                    target = o.get('target') or ''
                    clears.add(target)
                    e = world.me['events'].get(target)
                    if e is None:
                        f.add(sev, 'quest-event-missing', owner, step, f'{label}: map event not in the client scripts we hold', 'map-events.json', area)
                        continue
                    if e['map'] not in world.served:
                        f.add(sev, 'quest-event-unserved', owner, step, f'{label}: its map {e["map"]} ({world.map_name(e["map"])}) is not served: cannot be entered', 'map-events served_maps', area)
                        continue
                    if not event_starts_for(world, e, q['id'], s['order']):
                        needs = [c['v'][0] for g in e['start'] for c in g if c['t'] == 'IncompleteQuestStep']
                        f.add(WARN, 'quest-event-start', owner, step, f'{label}: the event starts only at quest steps {needs[:4]}, not at {q["id"]} step {s["order"]}', '', area)
                    pays = any(d['t'] == 'DungeonClear' for _, d in event_directives(e)) or e['map'] in {int(k) for k in world.instances}
                    if not pays:
                        f.add(sev, 'quest-event-noclear', owner, step, f'{label}: the script has no DungeonClear: the quest objective is never paid', '', area)
                    d = world.dungeon_by_map.get(e['map'])
                    if d is None:
                        f.add(sev, 'quest-event-nodungeon', owner, step, f'{label}: map {e["map"]} has no Dungeon.dat row: no entry, no quest teleport', '', area)
                elif t == 'CollectItem':
                    if str(o.get('item')) not in world.items:
                        f.add(sev, 'quest-item', owner, step, f'{label}: item {o.get("item")} ({o.get("item_name")}) unknown to the server', 'gamedata/items', area)
                elif t == 'EngraveTeleportArtifact':
                    if o.get('target') not in world.artifacts:
                        f.add(sev, 'quest-artifact', owner, step, f'{label}: Kibelisk not placed', 'teleport-artifacts.json', area)
                elif t == 'ClearQuest':
                    if o.get('target') not in world.quests:
                        f.add(sev, 'quest-clearquest', owner, step, f'{label}: no such quest', '', area)
    return clears


# ---------------------------------------------------------------- order of play

def play_order(world: World):
    """Quest code -> (rank, area). Each race's hero chain in order (Elyos HQ1..., Asmodian HQ2...), then by level, map, code."""
    order = {}
    rank = 0
    for start in ('HQ2100010', 'HQ1100010'):
        code = start
        seen = set()
        while code and code in world.quests and code not in seen:
            seen.add(code)
            order[code] = rank
            rank += 1
            code = world.quests[code].get('next')
    rest = sorted((c for c in world.quests if c not in order), key=lambda c: (world.quests[c]['level'], world.quests[c]['map'], c))
    for c in rest:
        order[c] = 100000 + world.quests[c]['level'] * 1000 + len(order)
    return order


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=str(ROOT / 'research/local/content-audit.md'))
    ap.add_argument('--json')
    ap.add_argument('--tables')
    ap.add_argument('--walk', help='ContentWalk result file (CONTENT_WALK_OUT of tests/Aion.Native.Checks)')
    args = ap.parse_args()
    tables = Path(args.tables) if args.tables else None
    world = World(tables)
    vocab = engine_vocabulary(ENGINE.read_text(encoding='utf-8'))
    order = play_order(world)
    f = Findings()

    def area_of(code):
        q = world.quests[code]
        return (order[code], q['map'], code)

    # Events: their place in play is the first quest that clears them or whose step starts them.
    event_quest = {}
    for code in sorted(world.quests, key=lambda c: order[c]):
        q = world.quests[code]
        for s in q['steps']:
            for o in s['objectives']:
                if o['type'] == 'ClearMapEvent':
                    event_quest.setdefault(o.get('target'), code)
    for name, e in world.me['events'].items():
        for g in e.get('start') or []:
            for c in g:
                if c['t'] == 'IncompleteQuestStep' and c['v']:
                    qs = world.step_of(c['v'][0])
                    if qs and qs[0] in world.quest_by_id:
                        code = world.quest_by_id[qs[0]]
                        if name not in event_quest or order[code] < order[event_quest[name]]:
                            event_quest[name] = code

    quest_targets = {}
    for name, code in event_quest.items():
        e = world.me['events'].get(name)
        if e:
            quest_targets.setdefault(e['map'], code)

    counts = Counter()
    for name, e in world.me['events'].items():
        if e['map'] not in world.served:
            continue
        code = event_quest.get(name)
        area = area_of(code) if code else (900000 + world.me['dungeons'].get(str(world.dungeon_by_map.get(e['map'])), {}).get('min_level', 99) * 100, e['map'], name)
        audit_event(world, vocab, name, e, f, area)
        counts['event steps'] += sum(len(st['steps']) for st in e['stages'])
        counts['events'] += 1
    audit_quests(world, f, area_of)
    counts['quest steps'] = sum(len(q['steps']) for q in world.quests.values())
    counts['quests'] = len(world.quests)
    audit_dungeons(world, f, quest_targets)

    # The dynamic walk (tests/Aion.Native.Checks ContentWalk, CONTENT_WALK_OUT=<file>): steps that did not advance, objectives only
    # the 30 s timeout finished. Event -> area as the static findings.
    walked = {}
    if args.walk and Path(args.walk).exists():
        for line in Path(args.walk).read_text(encoding='utf-8').splitlines():
            parts = line.split('\t')
            if len(parts) < 8:
                continue
            name, status, stuck = parts[0], parts[2], parts[5]
            walked[name] = status
            code = event_quest.get(name)
            e = world.me['events'].get(name)
            if not e:
                continue
            area = area_of(code) if code else (900000 + world.me['dungeons'].get(str(world.dungeon_by_map.get(e['map'])), {}).get('min_level', 99) * 100, e['map'], name)
            if status == 'STUCK' and stuck and 'UnableComplete' not in stuck:
                where, _, why = stuck.partition(': ')
                f.add(BLOCK, 'walk-stuck', name, where if why else 'start', f'DYNAMIC: the step does not advance — {why or where}', 'ContentWalk', area)
            for t in [x for x in parts[6][len('timeouts ['):-1].split(' | ') if x]:
                where, _, what = t.partition(': ')
                if what.split(' ')[1] in vocab['unsupported']:
                    continue   # already listed as an unimplemented objective
                f.add(TIMEOUT, 'walk-timeout', name, where, f'DYNAMIC: {what}', 'ContentWalk', area)

    # One line per broken step: its worst severity first, the findings joined.
    by_step = defaultdict(list)
    for x in f.items:
        if x['kind'] in SUMMARY_ONLY:
            continue
        by_step[(x['owner'], x['step'])].append(x)
    worst = {k: min(v, key=lambda x: RANK[x['severity']])['severity'] for k, v in by_step.items()}
    sev_counts = Counter(worst.values())

    def area_key(a):
        if a is None:
            return (10 ** 9, 0, '')
        if a[0] == 'zz':
            return (10 ** 8, a[1], '')
        if a[0] == 'dungeon':
            return (10 ** 7, a[1], '')
        return a

    def heading(a):
        if a is None:
            return 'Other'
        if a[0] == 'zz':
            return 'Doors and portals (all maps)'
        if a[0] == 'dungeon':
            return 'Dungeons and instance layers (entry, arrival, exit)'
        if a[0] >= 900000:
            return 'Map events no quest leads to (party/seal/field content)'
        code = a[2]
        q = world.quests.get(code)
        name = f'map {a[1]} {world.map_name(a[1])}'.strip()
        if a[0] < 100000:
            return ('Asmodian' if code.startswith('HQ2') else 'Elyos') + f' story ({code} {q["title"] if q else ""}), {name}'
        return f'Level {q["level"]} content, {name}' if q else name

    total_steps = counts['event steps'] + counts['quest steps']
    lines = ['# Content audit 2026-11-09', '']
    lines.append(f'Generated by `python tools/content_audit.py --walk <ContentWalk output>`. Checked {counts["quests"]} quests / {counts["quest steps"]} quest steps and '
                 f'{counts["events"]} map events / {counts["event steps"]} event steps on served maps ({total_steps} steps), every dungeon entry/arrival/exit, '
                 f'every door and portal; {len(walked)} events also played through the engine (ContentWalk: {sum(1 for v in walked.values() if v == "complete")} complete).')
    lines.append('')
    lines.append(f'Steps with a finding: {len(by_step)} — BLOCK {sev_counts[BLOCK]}, FUZZY {sev_counts[FUZZY]}, TIMEOUT {sev_counts[TIMEOUT]}, WARN {sev_counts[WARN]}. '
                 'BLOCK = cannot advance / never counts; FUZZY = only the fuzzy name rules connect it; TIMEOUT = the server finishes it after 30 s; '
                 'WARN = a directive does nothing or a detail differs, the step still advances.')
    lines.append('')
    lines.append('## Unimplemented types (counts over all served events / all quests)')
    agg = Counter()
    for x in f.items:
        if x['kind'] in ('directive-unimplemented', 'objective-unimplemented', 'condition-unimplemented', 'quest-objective-unimplemented', 'objective-approx', 'summon-object'):
            agg[(x['kind'], x['what'].replace('directive ', '').replace('condition ', '').split(' ')[0].split(':')[0])] += 1
    for (k, w), n in sorted(agg.items(), key=lambda kv: -kv[1]):
        lines.append(f'- {k}: {w} x{n}')
    lines.append('')
    lines.append('## Findings by kind')
    for k, n in Counter(x['kind'] for x in f.items).most_common():
        lines.append(f'- {k}: {n}')
    lines.append('')
    lines.append('## Broken steps, in the order a player meets them')
    lines.append('')
    lines.append('Format: [worst severity] quest or event · step · what fails (evidence). A step with several findings lists them after " | ".')
    current = None
    steps = sorted(by_step.items(), key=lambda kv: (area_key(kv[1][0]['area']), kv[0][0], RANK[worst[kv[0]]], kv[0][1]))
    for (owner, step), xs in steps:
        head = heading(xs[0]['area'])
        if head != current:
            lines.append('')
            lines.append(f'### {head}')
            current = head
        xs = sorted(xs, key=lambda x: RANK[x['severity']])
        whats = []
        for x in xs:
            text = x['what'] + (f' ({x["evidence"]})' if x['evidence'] and x['severity'] != WARN else '')
            if text not in whats:
                whats.append(text)
        shown = ' | '.join(whats[:6]) + (f' | +{len(whats) - 6} more' if len(whats) > 6 else '')
        lines.append(f'- [{worst[(owner, step)]}] {owner} · {step} · {shown}')
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_text('\n'.join(lines) + '\n', encoding='utf-8')
    if args.json:
        Path(args.json).write_text(json.dumps({'counts': counts, 'severity': sev_counts, 'findings': f.items}, ensure_ascii=False, indent=1), encoding='utf-8')
    print(f'{total_steps} steps checked; {len(by_step)} with findings ({dict(sev_counts)}); {len(f.items)} findings -> {args.out}')


if __name__ == '__main__':
    main()
