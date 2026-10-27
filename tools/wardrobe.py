"""Plan, export and index item icons and wearable meshes for the wardrobe preview.

The wardrobe is a local research viewer: it pairs catalogue items with the
icon and costume packages their table rows name. It does not establish in-game
equip rules, attachment sockets or shader equivalence. Outputs stay under the
ignored research/local/ directory.

    python tools/wardrobe.py plan      # write research/local/wardrobe/job.json
    python tools/wardrobe.py export    # run tools/WardrobeExport (resumable)
    python tools/wardrobe.py index     # write research/local/wardrobe/wardrobe.json
    python tools/wardrobe.py vendor    # fetch pinned three.js modules for local serving
"""
import argparse
import hashlib
from contextlib import closing
import json
import re
import sqlite3
import subprocess
import sys
from pathlib import Path
from urllib.request import urlopen

try:
    from . import class_skills
except ImportError:
    import class_skills

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / 'research/local'
OUTPUT = LOCAL / 'wardrobe'
ENTRIES = LOCAL / 'game-catalog-v2/entries.jsonl'
ASSETS = LOCAL / 'asset-catalog.sqlite'
EXPORTER = ROOT / 'tools/WardrobeExport/bin/Release/net10.0/WardrobeExport.dll'
EXPORT_INPUTS = {
    'paks': 'G:/SteamLibrary/steamapps/common/AION2/Aion2/Content/Paks',
    'key': LOCAL / 'archive-key-validated.json',
    'oodle': LOCAL / 'tooling/oodle-2.9.16/oodle-data-shared.dll',
    'usmap': LOCAL / 'tooling/mappings/AION2-5.3.2.0-2.0.6.0.usmap',
}
PLAYER = 'AION2/Content/Character/Player/'
GENDERS = {'f': 'GF', 'm': 'GM'}
# Item category -> wardrobe slot and the armor package suffix it names.
ARMOR = {'Helmet': 'Helmet', 'Torso': 'Body', 'Shoulder': 'Shoulder', 'Gloves': 'Glove',
         'Pants': 'Pants', 'Boots': 'Boots', 'Cape': 'Cape'}
WEAPONS = {'Sword', 'Greatsword', 'Dagger', 'Bow', 'Magicbook', 'Orb', 'Mace', 'Staff'}
ACCESSORIES = {'Necklace', 'Earring'}
# Shown when an armor slot is empty. Gloves, shoulders, helmet and cape have no
# separate base part; the base body already covers arms and hands.
BASE_PARTS = {'Body': 'Basebody/{g}_BaseBody', 'Pants': 'Basebody/{g}_BasePants',
              'Boots': 'Basebody/{g}_BaseBoots', 'Head': 'Basebody/{g}_BaseHead'}
DEFAULT_HAIR = {'GF': 'Hair/GF_Hair_012/GF_Hair_012', 'GM': 'Hair/GM_Hair_001/GM_Hair_001'}
# Character creation options (AssetProbe Json dump of UnrealData/PcCustomizeFeatureTable_Material):
# per gender, HairType/HeadType index -> mesh resource and SkinColor index -> colour. The stored
# customization bytes give these indices (Aion.Core CharacterAppearance; value - 100).
CUSTOMIZE_TABLE = LOCAL / 'exports/customize-tables-20261014/PcCustomizeFeatureTable_Material.json'
CUSTOMIZE_ROWS = {'GM': 'Male', 'GF': 'Female'}
CUSTOMIZE_MESHES = {'HairType': 'Hair', 'HeadType': 'Head'}
# Weapon attach bones (GswordWeapon, BowWeapon, UnOR, ...) exist only on the root mesh skeleton.
ATTACH_BONES = 'Weapon|^Un'
# Lobby idle animation per weapon type (Ani/Common/<type>/G?_<type>_LobbyIdle); the
# viewer applies one frame as a static pose. NoWeapon is used when no weapon is held.
POSE_TYPES = ('NoWeapon', 'SW', 'GS', 'DA', 'MA', 'BW', 'ST', 'MB', 'OR')


# three.js r160 (MIT) served from the wardrobe directory, so pages work offline and
# under the account host's script-src 'self' policy. Bare 'three' imports and the
# addon-relative utility import are rewritten to the flat vendor directory.
THREE_VERSION = '0.160.0'
THREE_FILES = {'three.module.js': 'build/three.module.js', 'OrbitControls.js': 'examples/jsm/controls/OrbitControls.js',
               'GLTFLoader.js': 'examples/jsm/loaders/GLTFLoader.js',
               'BufferGeometryUtils.js': 'examples/jsm/utils/BufferGeometryUtils.js', 'LICENSE': 'LICENSE'}


def vendor_source(text):
    return (text.replace("from 'three';", "from './three.module.js';")
                .replace("from '../utils/BufferGeometryUtils.js';", "from './BufferGeometryUtils.js';"))


def vendor(output=OUTPUT, fetch=lambda url: urlopen(url, timeout=60).read()):
    target = output / 'vendor/three'
    target.mkdir(parents=True, exist_ok=True)
    manifest = {'version': THREE_VERSION, 'files': {}}
    for name, source in THREE_FILES.items():
        url = f'https://cdn.jsdelivr.net/npm/three@{THREE_VERSION}/{source}'
        raw = fetch(url)
        manifest['files'][name] = {'url': url, 'sha256': hashlib.sha256(raw).hexdigest()}
        (target / name).write_text(vendor_source(raw.decode('utf-8')), encoding='utf-8')
    (target / 'manifest.json').write_text(json.dumps(manifest, indent=1), encoding='utf-8')
    return manifest


def slot_of(category):
    if category in ARMOR:
        return ARMOR[category]
    if category in WEAPONS:
        return 'Weapon'
    if category in ACCESSORIES:
        return category
    return None


def load_packages(path=ASSETS):
    """Map lower-case package names to full package paths (without extension)."""
    by_name = {}
    with closing(sqlite3.connect(Path(path).resolve().as_uri() + '?mode=ro', uri=True)) as db:
        for (raw,) in db.execute("SELECT path FROM assets WHERE path LIKE '%.uasset'"):
            package = raw.replace('../../../', '', 1)[:-len('.uasset')]
            by_name.setdefault(package.rsplit('/', 1)[-1].lower(), []).append(package)
    return by_name


def find(by_name, name, prefix):
    return next((p for p in by_name.get(name.lower(), ()) if p.startswith(prefix)), None)


def costume_packages(by_name, category, key, gender):
    """Package paths for one item's costume key, or [] when none is found."""
    if not key or key == 'None':
        return []
    if category in ARMOR:
        found = find(by_name, f'{key}_{ARMOR[category]}', PLAYER + gender + '/')
        return [found] if found else []
    prefix = PLAYER + ('Weapon/' if category in WEAPONS else gender + '/ACC/')
    single = find(by_name, key, prefix)
    if single:
        return [single]
    return [p for p in (find(by_name, f'{key}_{side}', prefix) for side in 'LR') if p]


def icon_variants(icon, race):
    """Similarly named textures for an IconRes absent from the catalogue (candidates only).

    Observed drift: Icon_Item_Currency_X exists as Icon_Currency_X; armor icons
    Icon_GM_0001_T04_Boots exist as Icon_Equip_AR_L_0001_T04_Boots and weapon icons
    Icon_WP_BW_0001_T03 as Icon_Equip_WP_L_BW_0001_T03 (L/D by race).
    """
    found = ['Icon_' + icon[len('Icon_Item_'):]] if icon.startswith('Icon_Item_') else []
    armor = re.fullmatch(r'Icon_G[MF]_(\d{4}_T\d\d_\w+)', icon)
    weapon = re.fullmatch(r'Icon_WP_(\w+)', icon)
    for kind, match in (('AR', armor), ('WP', weapon)):
        if match:
            found += [f'Icon_Equip_{kind}_{side}_{match[1]}' for side in dict.fromkeys([race, 'L', 'D'])]
    return found


def customize_sets(path=CUSTOMIZE_TABLE):
    """{gender: CustomizeFeatureDataSet} from the dumped table, or {} when it is not exported."""
    if not Path(path).exists():
        return {}
    rows = json.loads(Path(path).read_text(encoding='utf-8'))[0]['Rows']
    return {g: rows[row]['CustomizeFeatureDataSet'] for g, row in CUSTOMIZE_ROWS.items() if row in rows}


def customize_packages(by_name, gender, dataset):
    """{(field, index): package} for every hair/head option of one gender."""
    found = {}
    for field, folder in CUSTOMIZE_MESHES.items():
        for entry in dataset.get(field, []):
            name = entry['Value'].get('ResourceName')
            package = find(by_name, name, PLAYER + gender + '/' + folder + '/') if name and name != 'None' else None
            if package:
                found[(field, int(entry['Key']))] = package
    return found


def mesh_out(package):
    return 'meshes/' + package[len(PLAYER):] if package.startswith(PLAYER) else 'meshes/' + package


def items(path=ENTRIES):
    with open(path, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry['kind'] == 'items':
                yield entry


def plan(by_name, entries):
    icons, meshes, missing = {}, {}, {'icons': set(), 'meshes': set()}
    for entry in entries:
        raw = entry['raw']
        icon = raw.get('IconRes')
        if icon and icon != 'None' and icon not in icons:
            package = find(by_name, icon, 'AION2/Content/UI/')
            record = {'package': package, 'out': f'icons/{icon}.webp', 'max': 256}
            if not package:
                missing['icons'].add(icon)
                race = 'D' if 'Dark' in str(raw.get('Costume_res_Material')) else 'L'
                variant = next((v for v in icon_variants(icon, race) if find(by_name, v, 'AION2/Content/UI/')), None)
                # Without a variant the exporter searches all mounted files (including Paks).
                record = ({'package': find(by_name, variant, 'AION2/Content/UI/'), 'variant': variant} if variant
                          else {'name': icon}) | {'out': record['out'], 'max': 256}
            icons[icon] = record
        if not slot_of(entry['category']):
            continue
        for g, gender in GENDERS.items():
            key = raw.get('Costume_res_key_' + g)
            found = costume_packages(by_name, entry['category'], key, gender)
            if not found and key and key != 'None':
                missing['meshes'].add(f'{gender}:{entry["category"]}:{key}')
            for package in found:
                meshes[package] = {'package': package, 'out': mesh_out(package)}
    for gender in GENDERS.values():
        for part in [*BASE_PARTS.values(), DEFAULT_HAIR[gender]]:
            package = PLAYER + gender + '/' + part.format(g=gender)
            meshes[package] = {'package': package, 'out': mesh_out(package)}
    for gender, dataset in customize_sets().items():
        for package in customize_packages(by_name, gender, dataset).values():
            meshes[package] = {'package': package, 'out': mesh_out(package)}
    # Base parts and hair first so a partial run already shows a character.
    # Class skill icons for the staff dashboard (Skill/ICON_* names from Skill.dat).
    for definition in class_skills.data()['skills'].values():
        name = (definition.get('icon') or '').rsplit('/', 1)[-1]
        package = find(by_name, name, 'AION2/Content/UI/') if name else None
        if package and name not in icons:
            icons[name] = {'package': package, 'out': f'skill-icons/{name}.webp', 'max': 128}
    ordered = sorted(meshes.values(), key=lambda m: ('Basebody' not in m['package'] and '/Hair/' not in m['package'], m['package']))
    sockets = [{'package': PLAYER + f'{g}/Basebody/{g}_BaseBody', 'out': f'sockets/{g}.json',
                'bones_from': PLAYER + f'{g}/{g}_RootMesh', 'bone_pattern': ATTACH_BONES} for g in GENDERS.values()]
    poses = [{'package': PLAYER + f'{g}/Ani/Common/{t}/{g}_{t}_LobbyIdle', 'out': f'poses/{g}/{t}.json'}
             for g in GENDERS.values() for t in POSE_TYPES]
    return {'icons': sorted(icons.values(), key=lambda i: i['out']), 'meshes': ordered, 'sockets': sockets,
            'poses': poses, 'texture_max': 1024,
            'missing': {k: sorted(v) for k, v in missing.items()}}


def index(output=OUTPUT, entries=None, by_name=None):
    """Join exported files back to catalogue items for the browser."""
    entries = list(items()) if entries is None else entries
    def part(package):
        manifest = output / (mesh_out(package) + '.json')
        return mesh_out(package) + '.json' if manifest.exists() else None
    result = {'base': {}, 'attach': {}, 'poses': {}, 'items': []}
    job = output / 'job.json'
    variants = {i['out'] for i in json.loads(job.read_text(encoding='utf-8'))['icons'] if i.get('variant')} if job.exists() else set()
    for gender in GENDERS.values():
        parts = {slot: part(PLAYER + gender + '/' + p.format(g=gender)) for slot, p in BASE_PARTS.items()}
        parts['Hair'] = part(PLAYER + gender + '/' + DEFAULT_HAIR[gender])
        result['base'][gender] = parts
        result['attach'][gender] = f'sockets/{gender}.json' if (output / f'sockets/{gender}.json').exists() else None
        result['poses'][gender] = {t: f'poses/{gender}/{t}.json' for t in POSE_TYPES if (output / f'poses/{gender}/{t}.json').exists()}
    for entry in entries:
        raw, category = entry['raw'], entry['category']
        icon = raw.get('IconRes')
        icon_file = f'icons/{icon}.webp' if icon and (output / f'icons/{icon}.webp').exists() else None
        record = {'id': entry['id'], 'name': entry['name'], 'category': category, 'slot': slot_of(category),
                  'grade': str(raw.get('ItemGrade', '')).split('::')[-1], 'level': raw.get('ItemLevel'),
                  'race': str(raw.get('ItemRace', '')).split('::')[-1], 'icon': icon_file,
                  'icon_variant': icon_file in variants, 'parts': {}}
        if record['slot']:
            by_name = by_name or load_packages()
            for g, gender in GENDERS.items():
                found = [part(p) for p in costume_packages(by_name, category, raw.get('Costume_res_key_' + g), gender)]
                record['parts'][gender] = [p for p in found if p]
        result['items'].append(record)
    # Creation options for the stored-character preview: index -> exported part / colour.
    result['customize'] = {}
    by_name = by_name or load_packages()
    for gender, dataset in customize_sets().items():
        packages = customize_packages(by_name, gender, dataset)
        options = {folder.lower(): {str(index): part(package) for (f, index), package in packages.items() if f == field}
                   for field, folder in CUSTOMIZE_MESHES.items()}
        options['skin'] = {e['Key']: '#' + e['Value']['Skin_Color']['Hex'].lower()
                           for e in dataset.get('SkinColor', []) if e['Value'].get('Skin_Color', {}).get('Hex')}
        result['customize'][gender] = options
    result['skills'] = {}
    for skill_id, definition in class_skills.data()['skills'].items():
        name = (definition.get('icon') or '').rsplit('/', 1)[-1]
        icon = f'skill-icons/{name}.webp' if name and (output / f'skill-icons/{name}.webp').exists() else None
        result['skills'][skill_id] = {'name': definition['name'], 'icon': icon, 'type': definition['type']}
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['plan', 'customize-job', 'export', 'index', 'vendor'])
    parser.add_argument('--output', type=Path, default=OUTPUT)
    parser.add_argument('--job', type=Path, help='job file for export (default: <output>/job.json)')
    args = parser.parse_args(argv)
    output = args.output.resolve()
    if not output.is_relative_to(LOCAL.resolve()):
        parser.error('output must be under research/local/')
    output.mkdir(parents=True, exist_ok=True)
    if args.command == 'plan':
        job = plan(load_packages(), items())
        (output / 'job.json').write_text(json.dumps(job, indent=1), encoding='utf-8')
        print(f"{len(job['icons'])} icons, {len(job['meshes'])} meshes; {len(job['missing']['icons'])} icon names "
              f"not in the asset catalogue (variant/Pak lookup applied), {len(job['missing']['meshes'])} costume keys unresolved")
    elif args.command == 'customize-job':
        # Only the creation hair/head meshes, for an incremental export into the same output.
        by_name = load_packages()
        meshes = sorted({p for g, d in customize_sets().items() for p in customize_packages(by_name, g, d).values()})
        job = {'meshes': [{'package': p, 'out': mesh_out(p)} for p in meshes], 'texture_max': 1024}
        (output / 'customize-job.json').write_text(json.dumps(job, indent=1), encoding='utf-8')
        print(f"{len(meshes)} creation meshes; export with: wardrobe.py export --job {output / 'customize-job.json'}")
    elif args.command == 'export':
        if not EXPORTER.exists():
            sys.exit('Build tools/WardrobeExport first (see research/wardrobe.md).')
        job = args.job or output / 'job.json'
        command = ['dotnet', str(EXPORTER), *(str(EXPORT_INPUTS[k]) for k in ('paks', 'key', 'oodle', 'usmap')), str(job), str(output)]
        sys.exit(subprocess.call(command, cwd=ROOT))
    elif args.command == 'vendor':
        print(f"three.js {vendor(output)['version']} written to {output / 'vendor/three'}")
    else:
        result = index(output)
        (output / 'wardrobe.json').write_text(json.dumps(result, separators=(',', ':')), encoding='utf-8')
        wearable = [i for i in result['items'] if i['slot']]
        print(f"{len(result['items'])} items, {sum(bool(i['icon']) for i in result['items'])} with icons, "
              f"{sum(any(i['parts'].values()) for i in wearable)}/{len(wearable)} wearable items with meshes")


if __name__ == '__main__':
    main()
