import json
from pathlib import Path
import sqlite3
import tempfile
import threading
import unittest
from http.server import ThreadingHTTPServer
from urllib.request import urlopen
from urllib.error import HTTPError

from tools import wardrobe
from tools.game_catalog import handler, wardrobe_file

PLAYER = wardrobe.PLAYER


def item(identity, category, key_m='None', key_f='None', icon='None'):
    return {'kind': 'items', 'id': identity, 'name': f'Item {identity}', 'category': category,
            'raw': {'IconRes': icon, 'Costume_res_key_m': key_m, 'Costume_res_key_f': key_f,
                    'ItemGrade': 'EItemGrade::Unique', 'ItemLevel': 10, 'ItemRace': 'ERace::All'}}


PACKAGES = [
    'AION2/Content/UI/Resource/Texture/Item/Weapon/Icon_WP_SW_0001',
    PLAYER + 'GF/Armor/0010/GF_0010_T05/GF_0010_T05_Body',
    PLAYER + 'GF/Armor/0010/GF_0010_T05/GF_0010_T05_Body_Clip',
    PLAYER + 'GM/Armor/0010/GM_0010_T05/GM_0010_T05_Body',
    PLAYER + 'Weapon/SW/SW_0001/SW_0001',
    PLAYER + 'Weapon/OR/OR_0001/OR_0001_L',
    PLAYER + 'Weapon/OR/OR_0001/OR_0001_R',
    PLAYER + 'GM/ACC/ER/0018/GM_ER_0018_L',
]
ENTRIES = [
    item(1, 'Torso', 'GM_0010_T05', 'GF_0010_T05'),
    item(2, 'Sword', 'SW_0001', 'SW_0001', 'Icon_WP_SW_0001'),
    item(3, 'Orb', 'OR_0001', 'OR_0001'),
    item(4, 'Earring', 'GM_ER_0018', 'GF_ER_0018'),
    item(5, 'Usable', icon='Icon_Missing'),
]


def by_name():
    found = {}
    for package in PACKAGES:
        found.setdefault(package.rsplit('/', 1)[-1].lower(), []).append(package)
    return found


class PlanTests(unittest.TestCase):
    def test_costume_packages_by_category(self):
        names = by_name()
        self.assertEqual(wardrobe.costume_packages(names, 'Torso', 'GF_0010_T05', 'GF'), [PACKAGES[1]])
        self.assertEqual(wardrobe.costume_packages(names, 'Orb', 'OR_0001', 'GF'), PACKAGES[5:7])
        self.assertEqual(wardrobe.costume_packages(names, 'Earring', 'GM_ER_0018', 'GM'), [PACKAGES[7]])
        self.assertEqual(wardrobe.costume_packages(names, 'Earring', 'GM_ER_0018', 'GF'), [])
        self.assertEqual(wardrobe.costume_packages(names, 'Torso', 'None', 'GF'), [])

    def test_icon_variants_are_candidates_by_observed_naming_drift(self):
        self.assertEqual(wardrobe.icon_variants('Icon_Item_Currency_Get_Title_02_001', 'L'), ['Icon_Currency_Get_Title_02_001'])
        self.assertEqual(wardrobe.icon_variants('Icon_GM_0001_T04_Boots', 'D'),
                         ['Icon_Equip_AR_D_0001_T04_Boots', 'Icon_Equip_AR_L_0001_T04_Boots'])
        self.assertEqual(wardrobe.icon_variants('Icon_WP_BW_0001_T03', 'L'),
                         ['Icon_Equip_WP_L_BW_0001_T03', 'Icon_Equip_WP_D_BW_0001_T03'])
        self.assertEqual(wardrobe.icon_variants('Icon_ACC_NL_0018', 'L'), [])

    def test_plan_lists_icons_meshes_base_parts_and_missing(self):
        job = wardrobe.plan(by_name(), ENTRIES)
        self.assertEqual(job['icons'], [
            {'name': 'Icon_Missing', 'out': 'icons/Icon_Missing.webp', 'max': 256},
            {'package': PACKAGES[0], 'out': 'icons/Icon_WP_SW_0001.webp', 'max': 256}])
        packages = [m['package'] for m in job['meshes']]
        self.assertEqual(len(packages), len(set(packages)))
        self.assertIn(PLAYER + 'Weapon/SW/SW_0001/SW_0001', packages)
        self.assertNotIn(PACKAGES[2], packages)
        self.assertTrue(packages[0].startswith(PLAYER + 'GF/Basebody/'))
        self.assertEqual(job['missing'], {'icons': ['Icon_Missing'], 'meshes': ['GF:Earring:GF_ER_0018']})
        self.assertEqual([s['out'] for s in job['sockets']], ['sockets/GF.json', 'sockets/GM.json'])
        self.assertIn({'package': PLAYER + 'GM/Ani/Common/BW/GM_BW_LobbyIdle', 'out': 'poses/GM/BW.json'}, job['poses'])
        self.assertEqual(wardrobe.mesh_out(PACKAGES[4]), 'meshes/Weapon/SW/SW_0001/SW_0001')


class VendorTests(unittest.TestCase):
    def test_vendor_rewrites_bare_imports_and_records_hashes(self):
        sources = {'build/three.module.js': b"export const A = 1;",
                   'examples/jsm/loaders/GLTFLoader.js': (b"import { A } from 'three';\n"
                                                          b"import { toTrianglesDrawMode } from '../utils/BufferGeometryUtils.js';")}
        fetch = lambda url: sources.get(url.split('three@' + wardrobe.THREE_VERSION + '/')[1], b'// other')
        with tempfile.TemporaryDirectory() as temp:
            manifest = wardrobe.vendor(Path(temp), fetch)
            loader = (Path(temp) / 'vendor/three/GLTFLoader.js').read_text(encoding='utf-8')
            self.assertIn("from './three.module.js';", loader)
            self.assertIn("from './BufferGeometryUtils.js';", loader)
            self.assertNotIn("'three'", loader)
            self.assertEqual(set(manifest['files']), set(wardrobe.THREE_FILES))
            self.assertEqual(len(manifest['files']['LICENSE']['sha256']), 64)


class IndexTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.output = Path(temp.name)
        for relative in ['icons/Icon_WP_SW_0001.webp', 'meshes/Weapon/SW/SW_0001/SW_0001.json',
                         'meshes/GF/Basebody/GF_BaseBody.json', 'sockets/GF.json', 'poses/GF/SW.json']:
            (self.output / relative).parent.mkdir(parents=True, exist_ok=True)
            (self.output / relative).write_text('{}')
        self.assets = self.output / 'assets.sqlite'
        with sqlite3.connect(self.assets) as db:
            db.execute('CREATE TABLE assets (path TEXT)')
            db.executemany('INSERT INTO assets VALUES (?)', [('../../../' + p + '.uasset',) for p in PACKAGES])
        db.close()

    def test_index_links_exported_files_only(self):
        result = wardrobe.index(self.output, ENTRIES, wardrobe.load_packages(self.assets))
        items = {i['id']: i for i in result['items']}
        self.assertEqual(items[2]['icon'], 'icons/Icon_WP_SW_0001.webp')
        self.assertEqual(items[2]['parts'], {'GF': ['meshes/Weapon/SW/SW_0001/SW_0001.json'],
                                             'GM': ['meshes/Weapon/SW/SW_0001/SW_0001.json']})
        self.assertEqual(items[1]['parts'], {'GF': [], 'GM': []})
        self.assertEqual(items[5]['slot'], None)
        self.assertEqual(items[5]['icon'], None)
        self.assertEqual(result['base']['GF']['Body'], 'meshes/GF/Basebody/GF_BaseBody.json')
        self.assertIsNone(result['base']['GF']['Pants'])
        self.assertEqual(result['attach'], {'GF': 'sockets/GF.json', 'GM': None})
        self.assertEqual(result['poses'], {'GF': {'SW': 'poses/GF/SW.json'}, 'GM': {}})


class CustomizeTests(unittest.TestCase):
    def test_creation_options_map_to_meshes_and_colours(self):
        def option(key, name=None, hex_=None):
            value = {'ResourceName': name} if name else {'Skin_Color': {'Hex': hex_}}
            return {'Key': str(key), 'Value': value}
        table = [{'Rows': {'Male': {'CustomizeFeatureDataSet': {
            'HairType': [option(1, 'GM_Hair_014'), option(2, 'GM_Hair_999')],
            'HeadType': [option(1, 'GM_Head_001')],
            'SkinColor': [option(72, hex_='CBD0DA')]}}}}]
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'table.json'
            path.write_text(json.dumps(table), encoding='utf-8')
            sets = wardrobe.customize_sets(path)
            names = {'gm_hair_014': [PLAYER + 'GM/Hair/GM_Hair_014/GM_Hair_014'],
                     'gm_head_001': [PLAYER + 'GM/Head/GM_Head_001/GM_Head_001']}
            found = wardrobe.customize_packages(names, 'GM', sets['GM'])
        self.assertEqual(found, {('HairType', 1): PLAYER + 'GM/Hair/GM_Hair_014/GM_Hair_014',
                                 ('HeadType', 1): PLAYER + 'GM/Head/GM_Head_001/GM_Head_001'})
        self.assertNotIn('GF', sets)  # only rows present in the table
        self.assertEqual(wardrobe.customize_sets(Path('missing.json')), {})


class ServeTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.wardrobe = self.root / 'wardrobe'
        (self.wardrobe / 'icons').mkdir(parents=True)
        (self.wardrobe / 'icons/a.webp').write_bytes(b'RIFF')
        (self.wardrobe / 'job.txt').write_text('not served')
        (self.root / 'secret.json').write_text('{}')
        (self.wardrobe / 'wardrobe.json').write_text(json.dumps({'base': {}, 'items': [{'id': 7, 'icon': 'icons/a.webp'}]}))
        self.db = self.root / 'catalog.sqlite'
        with sqlite3.connect(self.db) as db:
            db.execute('CREATE TABLE entries (kind, id, name, category, classes, search, detail)')
            db.execute("INSERT INTO entries VALUES ('items', 7, 'Seven', 'Sword', '[]', 'seven', '{}')")
        db.close()

    def test_wardrobe_file_rejects_traversal_and_unserved_types(self):
        self.assertEqual(wardrobe_file(self.wardrobe, 'icons/a.webp'), (self.wardrobe / 'icons/a.webp').resolve())
        for relative in ['../secret.json', '..%2fsecret.json', 'job.txt', 'icons/missing.webp', 'icons']:
            self.assertIsNone(wardrobe_file(self.wardrobe, relative))

    def test_http_routes_and_search_icons(self):
        server = ThreadingHTTPServer(('127.0.0.1', 0), handler(self.db, self.wardrobe))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            base = 'http://127.0.0.1:' + str(server.server_port)
            with urlopen(base + '/wardrobe') as response:
                self.assertIn(b'Wardrobe preview', response.read())
            with urlopen(base + '/wardrobe-viewer.js') as response:
                self.assertTrue(response.headers['Content-Type'].startswith('text/javascript'))
                self.assertIn(b'export function createViewer', response.read())
            with urlopen(base + '/wardrobe/data') as response:
                self.assertEqual(json.loads(response.read())['items'][0]['id'], 7)
            with urlopen(base + '/wardrobe/files/icons/a.webp') as response:
                self.assertEqual(response.headers['Content-Type'], 'image/webp')
            with urlopen(base + '/api/search?q=seven') as response:
                self.assertEqual(json.loads(response.read())['rows'][0]['icon'], '/wardrobe/files/icons/a.webp')
            for route in ['/wardrobe/files/..%2fsecret.json', '/wardrobe/files/job.txt']:
                with self.assertRaises(HTTPError) as error:
                    urlopen(base + route)
                self.assertEqual(error.exception.code, 404)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


if __name__ == '__main__':
    unittest.main()
