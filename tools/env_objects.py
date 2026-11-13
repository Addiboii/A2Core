"""Add every EnvObjData row (name, usage, use count, finish duration, despawn, reward once, life time, range) to tools/data/env-usages.json.

    python tools/env_objects.py research/local/tables-<stamp>/Table_EnvObjData.json
Existing door entries keep their sequence/effect; the source table is the client's EnvObjData.dat export.
"""
import json, sys, pathlib
ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/data/env-usages.json'
table = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
def rows(t):
    if isinstance(t, list):
        for x in t:
            if isinstance(x, dict) and 'ID' in x and 'Name' in x: yield x
            elif isinstance(x, (list, dict)): yield from rows(x)
    elif isinstance(t, dict):
        for v in t.values():
            if isinstance(v, (list, dict)): yield from rows(v)
data = json.loads(OUT.read_text(encoding='utf-8'))
objs = data['objects']
n = 0
for r in rows(table):
    i = r['ID']['Value'] if isinstance(r['ID'], dict) else r['ID']
    k = str(i)
    usage = str(r.get('Usage', '')).split('::')[-1]
    value = r.get('UsageValue')
    value = None if value in (None, 'None', '') else str(value)
    e = objs.get(k) or {'name': r['Name'], 'usage': usage, 'value': value, 'sequence': None}
    e['count'] = int(r.get('InteractCount') or 0)
    e['finish_ms'] = int(r.get('FinishDuration') or 0)
    e['despawn'] = bool(r.get('bDespawn'))
    # bGetRewardOnce (each player takes the object's RewardData once; the object stays usable for the others), LifeTime (ms a summoned
    # object such as a recovery orb lives) and InteractRange: the dungeon boxes and orbs (GameSession.EnvBoxes.cs). Written only when set.
    for key, field in (('reward_once', 'bGetRewardOnce'), ('lifetime_ms', 'LifeTime'), ('range', 'InteractRange')):
        value = r.get(field)
        if key == 'reward_once':
            if value: e[key] = True
            else: e.pop(key, None)
        elif int(value or 0) > 0 and (key != 'range' or e.get('lifetime_ms')):
            e[key] = int(value)
        else:
            e.pop(key, None)
    # HitLimit of the destructible objects (RelationshipEntity EnvObj_Destroy: hits that break one, e.g. the Balaur Communicator 3).
    if str(r.get('RelationshipEntity', '')).endswith('EnvObj_Destroy') and int(r.get('HitLimit') or 0) > 0:
        e['hit_limit'] = int(r['HitLimit'])
    else:
        e.pop('hit_limit', None)
    objs[k] = e
    n += 1
data['source'] = 'EnvObjData.dat (client table, exported 2026-11-09): every object; effect = TeleportEffect.dat id of the door sequence; count/finish_ms/despawn/reward_once/lifetime_ms/range = InteractCount/FinishDuration/bDespawn/bGetRewardOnce/LifeTime/InteractRange (range only for summoned objects); hit_limit = HitLimit of the EnvObj_Destroy objects'
OUT.write_text(json.dumps(data, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
print(n, 'objects')
