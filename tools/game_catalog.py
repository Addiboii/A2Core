"""Build/query/browse a local extracted-data catalogue. Python standard library only."""
import argparse
from collections import defaultdict, Counter
from contextlib import contextmanager, closing
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import sqlite3
from urllib.parse import urlsplit, parse_qs, unquote

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / 'research/local'
DEFAULT_OUTPUT = LOCAL / 'game-catalog-v2'
# Exported icons/meshes from tools/wardrobe.py; optional for the catalogue itself.
WARDROBE = LOCAL / 'wardrobe'
WARDROBE_TYPES = {'.glb': 'model/gltf-binary', '.json': 'application/json; charset=utf-8', '.webp': 'image/webp',
                  '.js': 'text/javascript; charset=utf-8'}
# The character viewer module is shared with the account host's staff dashboard.
VIEWER = ROOT / 'server/Aion.Host/wwwroot/wardrobe-viewer.js'
SOURCES = {
    'items': 'reuse-table-probe/table-010.json',
    'skills': 'reuse-table-probe/table-007.json',
    'effects': 'reuse-table-probe/table-008.json',
    'levels': 'reuse-table-probe/table-009.json',
    'acquire': 'starter-skills-tables/table-002.json',
    'lists': 'starter-skills-tables/table-003.json',
    'strings': 'starter-skills-tables/table-008.json',
    'general_strings': 'catalog-strings-20261013/table-000.json',
    'english': 'catalog-localization-20261013/localization-000.json',
}
LIMITATION = ('Entries are from the extracted client build, including internal variants and possibly unused content. '
              'Availability in the live game and working server behavior are unknown. Numeric values retain table units.')


def value(obj):
    return obj.get('Value') if isinstance(obj, dict) else obj


def short(obj):
    return str(obj or 'Unknown').split('::')[-1]


def rows(data):
    data = data['Properties']['Data']
    return data if isinstance(data, list) else [data]


def group(data, field):
    result = defaultdict(list)
    for row in data:
        result[value(row.get(field))].append(row)
    return result


def make_entries(data):
    english = data['english']['Entries']
    tables = {k: rows(v) for k, v in data.items() if k != 'english'}
    strings = {r['Name']: r for r in tables['strings']}
    general_strings = {r['Name']: r for r in tables['general_strings']}
    effects = group(tables['effects'], 'SkillEffectGroupId')
    levels = group(tables['levels'], 'SkillLvGroupId')
    acquire = group(tables['acquire'], 'SkillId')
    lists = defaultdict(list)
    for row in tables['lists']:
        for skill in row.get('SkillIdList', []):
            lists[value(skill)].append(row)

    def translate(ref):
        key = ref.get('Key') if isinstance(ref, dict) else ref
        if key in general_strings:
            key = general_strings[key].get('Body', {}).get('Key')
        return english.get(key), key

    for kind in ('items', 'skills'):
        for row in tables[kind]:
            identity = value(row['ID'])
            links = {}
            if kind == 'items':
                name, name_key = translate(row.get('Desc'))
                description, desc_key = translate(row.get('DescLong'))
                internal = row.get('Name', str(identity))
                category = short(row.get('EquipCategory') or row.get('ItemType'))
                classes = []  # Do not infer class restrictions from weapon names.
                facts = {k: row[k] for k in ('ItemType', 'EquipCategory', 'ArmorType', 'ItemGrade',
                         'ItemRace', 'ItemLevel', 'PermitLevelMin', 'PermitLevelMax', 'MainStats',
                         'SubStats', 'Costume_res_key_m', 'Costume_res_key_f', 'ExtractionSkins') if k in row}
            else:
                internal = row.get('SkillString_Key', str(identity))
                text = strings.get(internal, {})
                name, name_key = translate(text.get('SkillName'))
                description, desc_key = translate(text.get('SkillDescEffect'))
                links['acquisition'] = acquire.get(identity, [])
                links['skill_lists'] = lists.get(identity, [])
                classes = sorted({short(r.get('ClassType')) for r in links['acquisition'] + links['skill_lists']})
                category = short(row.get('SkillType'))
                level_group = row.get('SkillLvGroupId')
                links['levels'] = {'group': level_group, 'rows': levels.get(level_group, [])
                                   if level_group not in (None, 'None', '') else []}
                links['effects'] = []
                for effect in row.get('SkillEffectTimeDataList', []):
                    key = value(effect.get('SkillEffectGroupId'))
                    if key:
                        matches = effects.get(key, [])
                        links['effects'].append({'group': key, 'status': 'resolved' if matches else 'unresolved',
                                                 'reference': effect, 'rows': matches})
                facts = {k: v for k, v in row.items() if k.startswith('NeedCost') or k in (
                    'NeedCoolTime', 'CastingTime', 'TargetProcessType', 'NeedMainWeaponTypes',
                    'SkillType', 'SkillSubType', 'SkillLvMax', 'AnimationName', 'SkillIcon')}
            yield {'kind': kind, 'id': identity, 'name': name or internal, 'internal_name': internal,
                   'name_status': 'localized' if name else 'unresolved', 'name_key': name_key,
                   'description': description, 'description_key': desc_key, 'category': category,
                   'classes': classes, 'facts': facts, 'relationships': links,
                   'source': SOURCES[kind], 'raw': row}


def build(output=DEFAULT_OUTPUT, local=LOCAL):
    output, local = Path(output), Path(local)
    output.mkdir(parents=True, exist_ok=True)
    # A new directory avoids replacing a catalogue currently served by another process.
    dbpath = output / 'catalog.sqlite'
    if dbpath.exists():
        raise ValueError('Catalogue already exists; choose a new --output directory')
    data, sources = {}, {}
    for key, relative in SOURCES.items():
        raw = (local / relative).read_bytes()
        data[key] = json.loads(raw)
        sources[key] = {'path': relative, 'sha256': hashlib.sha256(raw).hexdigest()}
    counts, localized = Counter(), Counter()
    unresolved_effects = set()
    with closing(sqlite3.connect(dbpath)) as db, (output / 'entries.jsonl').open('w', encoding='utf-8') as export:
        db.execute('CREATE TABLE entries(kind TEXT, id INTEGER, name TEXT, category TEXT, classes TEXT, search TEXT, detail TEXT, PRIMARY KEY(kind,id))')
        for entry in make_entries(data):
            encoded = json.dumps(entry, ensure_ascii=False, separators=(',', ':'))
            search = ' '.join([str(entry['id']), entry['name'], entry['internal_name'], entry['category'],
                               ' '.join(entry['classes']), entry['description'] or '']).casefold()
            db.execute('INSERT INTO entries VALUES (?,?,?,?,?,?,?)', (
                entry['kind'], entry['id'], entry['name'], entry['category'],
                json.dumps(entry['classes']), search, encoded))
            export.write(encoded + '\n')
            counts[entry['kind']] += 1
            localized[entry['kind']] += entry['name_status'] == 'localized'
            for link in entry['relationships'].get('effects', []):
                if link['status'] == 'unresolved':
                    unresolved_effects.add(link['group'])
        db.execute('CREATE INDEX category_index ON entries(kind,category)')
        manifest = {'schema_version': 1, 'counts': dict(counts), 'localized_names': dict(localized),
                    'unresolved_effect_groups': sorted(unresolved_effects), 'sources': sources,
                    'limitation': LIMITATION}
        db.execute('CREATE TABLE metadata(value TEXT)')
        db.execute('INSERT INTO metadata VALUES (?)', (json.dumps(manifest),))
        db.commit()
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    return manifest


@contextmanager
def connect(path):
    with closing(sqlite3.connect(Path(path).resolve().as_uri() + '?mode=ro', uri=True)) as db:
        yield db


def search(path, query='', kind='', category='', class_name='', offset=0, limit=50):
    if kind not in ('', 'items', 'skills') or offset < 0 or not 1 <= limit <= 100:
        raise ValueError('Invalid search parameters')
    clauses, params = [], []
    for column, val in [('kind', kind), ('category', category)]:
        if val:
            clauses.append(column + '=?')
            params.append(val)
    if query:
        clauses.append('instr(search, ?) > 0')
        params.append(query.casefold())
    if class_name:
        clauses.append('EXISTS(SELECT 1 FROM json_each(entries.classes) WHERE value=?)')
        params.append(class_name)
    where = ' WHERE ' + ' AND '.join(clauses) if clauses else ''
    with connect(path) as db:
        count = db.execute('SELECT count(*) FROM entries' + where, params).fetchone()[0]
        found = db.execute('SELECT kind,id,name,category,classes FROM entries' + where +
                           ' ORDER BY kind,id LIMIT ? OFFSET ?', params + [limit, offset]).fetchall()
    return {'total': count, 'offset': offset, 'rows': [dict(kind=r[0], id=r[1], name=r[2],
              category=r[3], classes=json.loads(r[4])) for r in found]}


def detail(path, kind, identity):
    with connect(path) as db:
        row = db.execute('SELECT detail FROM entries WHERE kind=? AND id=?', (kind, identity)).fetchone()
    return json.loads(row[0]) if row else None


def wardrobe_file(root, relative):
    """Resolve an exported wardrobe file, or None outside root or for unserved types."""
    base = Path(root).resolve()
    target = (base / unquote(relative)).resolve()
    if not target.is_relative_to(base) or target.suffix.lower() not in WARDROBE_TYPES or not target.is_file():
        return None
    return target


def handler(path, wardrobe=WARDROBE):
    icons = {'mtime': None, 'by_id': {}}

    def item_icons():
        index = Path(wardrobe) / 'wardrobe.json'
        try:
            mtime = index.stat().st_mtime
        except OSError:
            return {}
        if icons['mtime'] != mtime:
            items = json.loads(index.read_text(encoding='utf-8'))['items']
            icons.update(mtime=mtime, by_id={i['id']: '/wardrobe/files/' + i['icon'] for i in items if i['icon']})
        return icons['by_id']

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            url = urlsplit(self.path)
            args = {k: v[0] for k, v in parse_qs(url.query).items()}
            status, mime = 200, 'application/json; charset=utf-8'
            try:
                if url.path == '/':
                    body = (ROOT / 'tools/catalog_browser.html').read_bytes()
                    mime = 'text/html; charset=utf-8'
                elif url.path == '/api/search':
                    body = search(path, args.get('q', ''), args.get('kind', ''), args.get('category', ''),
                                  args.get('class', ''), int(args.get('offset', 0)))
                    by_id = item_icons()
                    for row in body['rows']:
                        if row['kind'] == 'items' and row['id'] in by_id:
                            row['icon'] = by_id[row['id']]
                elif url.path == '/api/detail':
                    body = detail(path, args.get('kind', ''), int(args.get('id', '0')))
                    if body is None:
                        status, body = 404, {'error': 'Entry not found'}
                elif url.path == '/api/meta':
                    with connect(path) as db:
                        body = json.loads(db.execute('SELECT value FROM metadata').fetchone()[0])
                        body['categories'] = db.execute('SELECT DISTINCT kind,category FROM entries ORDER BY kind,category').fetchall()
                        body['classes'] = [r[0] for r in db.execute('SELECT DISTINCT value FROM entries,json_each(entries.classes) ORDER BY value')]
                elif url.path == '/wardrobe-viewer.js':
                    body, mime = VIEWER.read_bytes(), WARDROBE_TYPES['.js']
                elif url.path == '/wardrobe':
                    body = (ROOT / 'tools/wardrobe.html').read_bytes()
                    mime = 'text/html; charset=utf-8'
                elif url.path == '/wardrobe/data' or url.path.startswith('/wardrobe/files/'):
                    relative = 'wardrobe.json' if url.path == '/wardrobe/data' else url.path[len('/wardrobe/files/'):]
                    target = wardrobe_file(wardrobe, relative)
                    if target is None:
                        status, body = 404, {'error': 'Wardrobe file not found'}
                    else:
                        body, mime = target.read_bytes(), WARDROBE_TYPES[target.suffix.lower()]
                else:
                    status, body = 404, {'error': 'Not found'}
            except (ValueError, TypeError):
                status, body = 400, {'error': 'Invalid parameters'}
            if not isinstance(body, bytes):
                body = json.dumps(body, ensure_ascii=False).encode('utf-8')
            self.send_response(status)
            self.send_header('Content-Type', mime)
            self.send_header('Content-Length', str(len(body)))
            self.send_header('X-Content-Type-Options', 'nosniff')
            self.end_headers()
            self.wfile.write(body)
    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    create = sub.add_parser('build')
    create.add_argument('--output', type=Path, default=DEFAULT_OUTPUT)
    create.add_argument('--local', type=Path, default=LOCAL)
    for name in ('search', 'serve'):
        child = sub.add_parser(name)
        child.add_argument('--database', type=Path, default=DEFAULT_OUTPUT / 'catalog.sqlite')
        if name == 'search':
            child.add_argument('query', nargs='?', default='')
            child.add_argument('--kind', choices=['items', 'skills'], default='')
            child.add_argument('--class', dest='class_name', default='')
            child.add_argument('--category', default='')
        else:
            child.add_argument('--port', type=int, default=8775)
            child.add_argument('--wardrobe', type=Path, default=WARDROBE)
    args = parser.parse_args()
    if args.command == 'build':
        print(json.dumps(build(args.output, args.local), indent=2))
    elif args.command == 'search':
        print(json.dumps(search(args.database, args.query, args.kind, args.category, args.class_name), indent=2))
    else:
        with connect(args.database) as db:
            db.execute('SELECT count(*) FROM entries').fetchone()
        with ThreadingHTTPServer(('127.0.0.1', args.port), handler(args.database, args.wardrobe)) as server:
            print(f'Catalogue: http://127.0.0.1:{server.server_port}', flush=True)
            server.serve_forever()


if __name__ == '__main__':
    main()
