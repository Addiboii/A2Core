"""Quest data for the development server from the client's own quest tables.

Source tables (WorldProbe, GAME_Aion2 reader, selection research/local/quest-selection.json,
output research/local/quest-tables/; row structs and enums from the 5.3.2.0 usmap):
- Quest.dat (QuestTableItem): code (Name, e.g. HQ1100010), numeric ID, race, type, unlock and
  recommended level, acquire type/NPC/map/talk, complete type/NPC/map/talk, give-up flag,
  repeat type, NextQuestName, AcquirableConditions (ClearQuest = prerequisite).
- QuestStep.dat (QuestStepTableItem): per quest code, Order, Start/ClearNpcTalk and GoalList
  (QuestGoalData: EQuestObjectiveType Type, Value string array, MapId, QuestMovePointName).
- QuestReward.dat (Group = quest numeric ID): ExpReward, ItemRewards (Gold = Kina 930100001,
  AbyssPoint, point charges), ItemNormalRewards, ItemSelectRewards, RandomReward package.
- QuestClassReward.dat: extra per-class items by the same Group.
- ContentsUnlock.dat: UnlockQuestNameLight/Dark gate contents behind quest completion.
- QuestString.dat + en-US L10NString: title, step and objective texts.
Names are resolved with NpcData.dat (npc names -> NpcData IDs, the npc_id of world-npcs.json),
Item.dat (item names -> item IDs) and EnvObjData.dat (env object names -> IDs).

The server rules for these values (drop chance, location radius, what counts as a talk) are
not in the tables; the objective value interpretations below marked CANDIDATE are ours.

    python tools/quests.py build       # writes tools/data/quests.json
    python tools/quests.py check       # spawn cross-check against tools/data/world-npcs.json
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/quests.json'
TABLES = ROOT / 'research/local/quest-tables'
NPCDATA = ROOT / 'research/local/npc-table/table-000.json'
ENVOBJ = ROOT / 'research/local/world/gameplay-tables/table-000.json'
LOCALIZATION = ROOT / 'research/local/catalog-localization-20261013/localization-000.json'
SPAWNS = ROOT / 'tools/data/world-npcs.json'
KINA_ITEM = 930100001
KINA_NAME = 'Gold'

# Objective types whose Value array names NPCs (comma separated list allowed).
NPC_LIST_TYPES = {'KillNpc', 'CollectFakeItem'}
NPC_TYPES = {'AskNpc', 'CloseNpc'}


def _enum(value):
    return value.split('::', 1)[1] if isinstance(value, str) and '::' in value else value


def _id(value):
    return value.get('Value') if isinstance(value, dict) else value


def _none(value):
    return None if value in (None, '', 'None') else value


def _int(value, default=0):
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def table_rows(path):
    data = json.loads(Path(path).read_text(encoding='utf-8'))
    return [row for rows in data['Properties'].values() for row in rows]


def tables_by_name(directory=TABLES):
    """Map table basename (e.g. 'QuestStep') to its decoded rows from a WorldProbe output."""
    summary = json.loads((Path(directory) / 'summary.json').read_text(encoding='utf-8'))
    out = {}
    for package in summary['packages']:
        if package.get('status') == 'table_decoded':
            out[package['path'].rsplit('/', 1)[-1][:-4]] = table_rows(Path(directory) / package['file'])
    return out


def _names(value):
    return [part.strip() for part in value.split(',') if part.strip()]


def objective(goal, npc_ids, item_ids, env_ids):
    """One QuestGoalData -> compact objective. Unresolved names keep id None."""
    kind = _enum(goal['Type'])
    v = list(goal.get('Value') or []) + [''] * 5
    out = {'type': kind}
    if kind in NPC_LIST_TYPES:
        names = _names(v[0])
        out['npcs'] = [npc_ids.get(n) for n in names]
        out['npc_names'] = names
        if kind == 'KillNpc':
            out['count'] = _int(v[1], 1)
        else:  # CANDIDATE: v1 drop chance per 10000 kills, v2 required count
            out['chance'] = _int(v[1])
            out['count'] = _int(v[2], 1)
    elif kind in NPC_TYPES:
        out['npc'] = npc_ids.get(v[0])
        out['npc_name'] = v[0]
        if _none(v[1]):
            out['talk'] = v[1]
        out['count'] = 1
    elif kind == 'CollectItem':
        out['item'] = item_ids.get(v[0])
        out['item_name'] = v[0]
        out['count'] = _int(v[1], 1)
        if v[2]:
            out['flag'] = v[2]          # CANDIDATE: TRUE = items are taken on completion
    elif kind == 'UseEnvObj':
        names = _names(v[0])
        out['envs'] = [env_ids.get(n) for n in names]
        out['env_names'] = names
        out['count'] = _int(v[1], 1)
    elif kind in ('ClearMapEvent', 'ClearQuest'):
        out['target'] = v[0]
        out['count'] = _int(v[1], 1)
    elif kind in ('EnterSubZone', 'CompleteCelviceTalk', 'CloseNote', 'UseFakeItem',
                  'EngraveTeleportArtifact'):
        out['target'] = v[0]
        out['count'] = 1
    elif kind == 'EnterVolumePC':
        out['target'] = v[0]
        out['value'] = _int(v[1])       # CANDIDATE: radius (cm) around the trigger
        out['count'] = 1
    elif kind == 'PCLevel':
        out['level'] = _int(v[0])
        out['count'] = 1
    elif kind == 'ClearQuestType':
        out['target'] = v[0]
        out['count'] = _int(v[1], 1)
    elif kind == 'ClearQuestExplorationType':
        out['target'] = v[0]
        out['count'] = _int(v[2], 1)
    else:  # KillPC, KillPCAbyssGrade and any type not seen in this build: raw values
        out['value'] = [x for x in goal.get('Value') or []]
        out['count'] = 1
    map_id = _id(goal.get('MapId'))
    if map_id:
        out['map'] = map_id
    move_point = _none(goal.get('QuestMovePointName'))
    if move_point:
        out['move_point'] = move_point
    if goal.get('bOptional'):
        out['optional'] = True
    return out


def _items(entries, item_ids):
    return [[item_ids.get(e['Item'], e['Item']), e['Count']] for e in entries]


def rewards(row, class_rows, item_ids):
    out = {'exp': row.get('ExpReward', 0) if row else 0, 'kina': 0, 'items': []}
    if row:
        for entry in row.get('ItemRewards', []) + row.get('ItemNormalRewards', []):
            if entry['Item'] == KINA_NAME:
                out['kina'] += entry['Count']
            else:
                out['items'].append([item_ids.get(entry['Item'], entry['Item']), entry['Count']])
        if row.get('ItemSelectRewards'):
            out['select'] = _items(row['ItemSelectRewards'], item_ids)
        if _none(row.get('RandomReward')):
            out['random'] = row['RandomReward']
    for crow in class_rows:
        items = _items(crow.get('ItemRewards', []) + crow.get('ItemNormalRewards', []), item_ids)
        if items:
            out.setdefault('class_items', {})[_enum(crow['Class'])] = items
    return out


def _text(text, entry):
    key = entry.get('Key') if isinstance(entry, dict) else None
    return text.get(key) if key and key != 'None' else None


def convert(quests, steps, reward_rows=(), class_rewards=(), strings=(), unlocks=(),
            npc_ids=None, item_ids=None, env_ids=None, text=None):
    """Decoded table rows -> {'quests': {code: quest}, 'unlocks': [...]}."""
    npc_ids, item_ids, env_ids, text = npc_ids or {}, item_ids or {}, env_ids or {}, text or {}
    by_quest_steps = {}
    for step in steps:
        by_quest_steps.setdefault(step['QuestName'], []).append(step)
    reward_by_group = {str(r['Group']): r for r in reward_rows}
    class_by_group = {}
    for r in class_rewards:
        class_by_group.setdefault(str(r['Group']), []).append(r)
    string_by_name = {s['Name']: s for s in strings}
    previous = {}
    for q in quests:
        nxt = _none(q.get('NextQuestName'))
        if nxt:
            previous.setdefault(nxt, []).append(q['Name'])

    out = {}
    for q in quests:
        code = q['Name']
        qid = _id(q['ID'])
        strings_row = string_by_name.get(q.get('QuestText'), {})
        conditions = [{'type': _enum(c['Type']), 'value': c['Value']} for c in q.get('AcquirableConditions', [])]
        acquire, complete = _enum(q['AcquireType']), _enum(q['CompleteType'])
        quest = {
            'id': qid,
            'title': _text(text, strings_row.get('Title')),
            'type': _enum(q['Type']),
            'race': _enum(q['Race']),
            'level': q.get('UnlockLevel', 0),
            'recommended_level': q.get('RecommendedLevel', 0),
            'part': _none(q.get('Part')),
            'map': _id(q.get('AcquireMapId')) or _id(q.get('CompleteMapId')),
            'acquire': acquire,
            'start_npc': None, 'start_npc_name': None,
            'complete': complete,
            'finish_npc': None, 'finish_npc_name': None,
            'finish_map': _id(q.get('CompleteMapId')),
            'giveup': bool(q.get('bEnableGiveup')),
            'repeat': _enum(q.get('RepeatType', 'EQuestRepeatType::None')),
            'prerequisites': [c['value'] for c in conditions if c['type'] == 'ClearQuest'],
            'previous': sorted(previous.get(code, [])),
            'next': _none(q.get('NextQuestName')),
        }
        other = [c for c in conditions if c['type'] != 'ClearQuest']
        if other:
            quest['conditions'] = other
        if acquire == 'Npc':
            quest['start_npc_name'] = q['AcquireValue']
            quest['start_npc'] = npc_ids.get(q['AcquireValue'])
        if _none(q.get('AcquireBeforeNpcTalk')):
            quest['start_talk'] = q['AcquireBeforeNpcTalk']
        if complete == 'Npc':
            quest['finish_npc_name'] = q['CompleteValue']
            quest['finish_npc'] = npc_ids.get(q['CompleteValue'])
        if _none(q.get('CompleteNpcTalk')):
            quest['finish_talk'] = q['CompleteNpcTalk']
        if _none(q.get('CompleteTeleportName')):
            quest['finish_teleport'] = q['CompleteTeleportName']
        if q.get('AcquireItemList'):
            quest['acquire_items'] = _items(q['AcquireItemList'], item_ids) \
                if isinstance(q['AcquireItemList'][0], dict) else q['AcquireItemList']

        desc = strings_row.get('ObjDescArray', [])
        quest_steps = []
        for index, step in enumerate(sorted(by_quest_steps.get(code, []), key=lambda s: s['Order'])):
            objectives = [objective(g, npc_ids, item_ids, env_ids) for g in step.get('GoalList', [])]
            if index < len(desc):
                texts = desc[index].get('ObjDescList', [])
                for i, obj in enumerate(objectives):
                    t = _text(text, texts[i]) if i < len(texts) else None
                    if t:
                        obj['text'] = t
            # id: the QuestStep row ID that map event and quest conditions name (IncompleteQuestStep, ClearQuestStep); 71 of
            # 2553 are not quest id * 100 + order (HQ1101040's only step is 110104002).
            entry = {'order': step['Order'], 'objectives': objectives}
            if (step.get('ID') or {}).get('Value'):
                entry['id'] = step['ID']['Value']
            if index < len(desc):
                t = _text(text, desc[index].get('StepDesc'))
                if t:
                    entry['text'] = t
            talkers = [o['npc'] for o in objectives if o['type'] in NPC_TYPES]
            if len(talkers) == 1:
                entry['npc'] = talkers[0]
            for key, field in (('start_talk', 'StartNpcTalk'), ('clear_talk', 'ClearNpcTalk')):
                if _none(step.get(field)):
                    entry[key] = step[field]
            if step.get('bUnifiedObjectives'):
                entry['unified_score'] = step.get('UnifiedObjectiveScore', 0)
            fail = _enum(step.get('FailConditionType', 'EQuestFailCondition::None'))
            if fail and fail != 'None':
                entry['fail'] = fail
                entry['fail_step'] = step.get('FailGoToStep', 0)
            quest_steps.append(entry)
        quest['steps'] = quest_steps
        quest['rewards'] = rewards(reward_by_group.get(str(qid)), class_by_group.get(str(qid), []), item_ids)
        out[code] = quest

    unlock_list = []
    for u in unlocks:
        light, dark = _none(u.get('UnlockQuestNameLight')), _none(u.get('UnlockQuestNameDark'))
        if light or dark:
            unlock_list.append({'id': _id(u['ID']), 'type': _enum(u['ContentUnlockType']),
                                'level': u.get('UnlockLevel', 0), 'light': light, 'dark': dark})
    return {'quests': out, 'unlocks': unlock_list}


def name_ids(path):
    return {row['Name']: _id(row['ID']) for row in table_rows(path)}


def build(tables=TABLES, npc_table=NPCDATA, env_table=ENVOBJ, localization=LOCALIZATION):
    t = tables_by_name(tables)
    text = {}
    if Path(localization).exists():
        text = json.loads(Path(localization).read_text(encoding='utf-8'))['Entries']
    item_ids = {row['Name']: _id(row['ID']) for row in t['Item']}
    result = convert(t['Quest'], t['QuestStep'], t['QuestReward'], t.get('QuestClassReward', []),
                     t.get('QuestString', []), t.get('ContentsUnlock', []),
                     name_ids(npc_table), item_ids, name_ids(env_table), text)
    return {'source': {
        'tables': 'Quest/QuestStep/QuestReward/QuestClassReward/QuestString/ContentsUnlock .dat '
                  '(research/local/quest-tables, WorldProbe)',
        'ids': 'npc ids = NpcData.dat ID (world-npcs npc_id); item ids = Item.dat ID '
               '(unresolved names kept as strings); env ids = EnvObjData.dat ID',
        'kina_item': KINA_ITEM,
        'candidate': 'CollectFakeItem chance per 10000, EnterVolumePC value radius, CollectItem flag',
    }, **result}


def spawn_check(data, spawns=SPAWNS, map_id=1000, race='Light'):
    """Count quests of a map/race whose start/finish NPCs are among the map's spawns."""
    try:
        from .world_npcs import expand
    except ImportError:
        from world_npcs import expand
    spawned = {s['npc_id'] for s in expand(json.loads(Path(spawns).read_text(encoding='utf-8')))['maps'].get(str(map_id), [])}
    report = {'quests': 0, 'npc_start': 0, 'start_spawned': 0, 'npc_finish': 0, 'finish_spawned': 0,
              'objective_npcs': 0, 'objective_npcs_spawned': 0, 'missing': set()}
    for code, q in data['quests'].items():
        if q['map'] != map_id or q['race'] not in (race, 'All'):
            continue
        report['quests'] += 1
        for key in ('start', 'finish'):
            name = q[key + '_npc_name']
            if name:
                report['npc_' + key] += 1
                if q[key + '_npc'] in spawned:
                    report[key + '_spawned'] += 1
                else:
                    report['missing'].add(name)
        for step in q['steps']:
            for o in step['objectives']:
                if o.get('map', map_id) != map_id:
                    continue
                ids = o.get('npcs') or ([o['npc']] if 'npc' in o else [])
                names = o.get('npc_names') or ([o['npc_name']] if 'npc_name' in o else [])
                for npc, name in zip(ids, names):
                    report['objective_npcs'] += 1
                    if npc in spawned:
                        report['objective_npcs_spawned'] += 1
                    else:
                        report['missing'].add(name)
    report['missing'] = sorted(report['missing'])
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build', 'check'])
    args = parser.parse_args()
    if args.command == 'build':
        data = build()
        DATA.write_text(json.dumps(data, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
        print('wrote %s: %d quests, %d unlocks, %d bytes' % (DATA, len(data['quests']), len(data['unlocks']),
                                                             DATA.stat().st_size))
    else:
        data = json.loads(DATA.read_text(encoding='utf-8'))
        print(json.dumps(spawn_check(data), indent=1))


if __name__ == '__main__':
    main()
