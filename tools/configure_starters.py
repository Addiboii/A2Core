"""Import versioned development starter policy; never invent missing race spawns.

Light (race 1) starts at the user-marked Poeta point (map 1000, Region 03). Dark (race 2) starts at
Ishalgen's D_Starter_Region_03_Home (map 1100, MapData TeleportDataMap), the Dark starter map's
Region 03 home point (CANDIDATE first spawn, from the client's own map data).
"""
import argparse
import json
try:
    from .postgres_dev import ROOT, sql, literal
    from .class_skills import starter_weapon
except ImportError:
    from postgres_dev import ROOT, sql, literal
    from class_skills import starter_weapon


# Race -> (starter map, position).
STARTS = {1: (1000, dict(x=7259.90771484375, y=-18223.455078125, z=22404.498046875)),
          2: (1100, dict(x=8617.2, y=-232.9, z=7001.7))}


def statements(build, profiles):
    lines = ['BEGIN; SET LOCAL ROLE aion_owner;']
    for key, profile in profiles.items():
        creation, gender = map(int, key.split(':'))
        # Classes with a recovered begin weapon, at their race's starter point.
        weapon = starter_weapon(profile['class'], profile['race'])
        start = STARTS.get(profile['race'])
        enabled = start is not None and weapon is not None
        definition = dict(worldId=1204, mapId=start[0] if start else None,
                          position=start[1] if start else {},
                          resources=dict(hp=1500, hpMax=1500, mp=200, mpMax=200, sp=100000, spMax=100000),
                          skills=profile['skills'], slots=profile['slots'], items=[],
                          evidence=dict(skills='tools/data/starter-skills.json; recovered level-one acquisition',
                                        resources='development settings; MP 200 is user estimate, not recovered base stat',
                                        position='Light: user-marked Poeta point; Dark: Ishalgen D_Starter_Region_03_Home (MapData)'),
                          characterClass=profile['class'])
        if enabled:
            definition['items'] = [dict(templateId=weapon, quantity=1, equipmentSlot=1)]
        # Keep unverified spawn/resources out of disabled profiles.
        if not enabled:
            definition['mapId'] = None
            definition['position'] = {}
            definition['resources'] = {}
        values = ','.join((literal('dev-v1-' + key), literal(build), str(creation),
                           str(profile['race']), str(gender), str(enabled).lower(), literal(json.dumps(definition))))
        # Enabled profiles are never rewritten; disabled ones take the current policy.
        lines.append('INSERT INTO game.starter_profiles VALUES(' + values + ') ON CONFLICT(profile_id) DO UPDATE'
                     ' SET enabled=EXCLUDED.enabled, definition=EXCLUDED.definition'
                     ' WHERE NOT game.starter_profiles.enabled;')
        if enabled:
            evidence = literal(f"{profile['class']} {'Light' if profile['race'] == 1 else 'Dark'} begin weapon (Item.dat equip_wp_*_c_begin_001a_b); native visibility unverified")
            lines.append(f"INSERT INTO game.equipment_rules VALUES({literal(build)},{weapon},1,1,ARRAY[{creation}],{evidence})"
                         f" ON CONFLICT(build_id,template_id,slot) DO UPDATE SET creation_ids=ARRAY(SELECT DISTINCT unnest("
                         f"game.equipment_rules.creation_ids || EXCLUDED.creation_ids) ORDER BY 1);")
    lines.append('COMMIT;')
    return '\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build-id')
    args = parser.parse_args()
    builds = sql('SELECT build_id FROM catalog.builds ORDER BY build_id;').stdout.splitlines()
    build = args.build_id or (builds[0] if len(builds) == 1 else None)
    if build not in builds:
        raise ValueError('Choose an imported catalogue with --build-id')
    profiles = json.loads((ROOT / 'tools/data/starter-skills.json').read_text())
    needed = sorted({skill for p in profiles.values() for skill in p['skills']})
    found = {int(value) for value in sql(f'SELECT template_id FROM catalog.skills WHERE build_id={literal(build)};').stdout.splitlines()}
    if set(needed) - found:
        raise ValueError('Imported catalogue is missing starter skills')
    weapons = {starter_weapon(p['class'], p['race']) for p in profiles.values() if p['race'] == 1} - {None}
    stocked = {int(v) for v in sql(f'SELECT template_id FROM catalog.items WHERE build_id={literal(build)};').stdout.split()}
    if weapons - stocked:
        raise ValueError('Imported catalogue is missing Light starter weapons')
    sql(statements(build, profiles))
    print(f'Configured {len(profiles)} profiles; Light class profiles with begin weapons enabled.')


if __name__ == '__main__':
    main()
