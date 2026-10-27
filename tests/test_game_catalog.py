import json
from pathlib import Path
import tempfile
import threading
import unittest
from http.server import ThreadingHTTPServer
from urllib.request import urlopen
from urllib.error import HTTPError

from tools.game_catalog import SOURCES, build, detail, handler, search


class CatalogTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        data = {
            'items': [{'ID': {'Value': 1}, 'Name': 'internal_sword', 'Desc': {'Key': 'SWORD'},
                       'EquipCategory': 'EEquipCategory::Greatsword', 'MainStats': [{'Value': 10}]},
                      {'ID': {'Value': 2}, 'Name': '<script>unused</script>'}],
            'skills': [{'ID': {'Value': 1}, 'SkillString_Key': 'SKILL', 'NeedCostSp': 30000,
                        'SkillLvGroupId': 'level_a', 'SkillEffectTimeDataList': [
                            {'SkillEffectGroupId': {'Value': 12}}, {'SkillEffectGroupId': {'Value': 99}}]},
                       {'ID': {'Value': 2}, 'SkillString_Key': 'unknown'}],
            'effects': [{'ID': {'Value': 8}, 'SkillEffectGroupId': {'Value': 12}, 'EffectType': 'Damage'}],
            'levels': [{'SkillLvGroupId': 'level_a', 'SkillLv': 2}],
            'acquire': [{'SkillId': {'Value': 1}, 'ClassType': 'ECharacterClass::Gladiator', 'NeedCharacterLevel': 1}],
            'lists': [],
            'strings': [{'Name': 'SKILL', 'SkillName': {'Key': 'skill_name'}}],
            'general_strings': [{'Name': 'SWORD', 'Body': {'Key': 'sword_name'}}],
        }
        for key, relative in SOURCES.items():
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            payload = {'Entries': {'sword_name': 'Worn Sword', 'skill_name': 'Dodge'}} if key == 'english' else {'Properties': {'Data': data[key]}}
            path.write_text(json.dumps(payload), encoding='utf-8')
        self.output = self.root / 'output'
        self.manifest = build(self.output, self.root)
        self.db = self.output / 'catalog.sqlite'

    def test_complete_export_and_unresolved_evidence(self):
        self.assertEqual(self.manifest['counts'], {'items': 2, 'skills': 2})
        self.assertEqual(self.manifest['unresolved_effect_groups'], [99])
        self.assertEqual(len((self.output / 'entries.jsonl').read_text().splitlines()), 4)
        self.assertEqual(detail(self.db, 'items', 1)['name'], 'Worn Sword')
        self.assertEqual(detail(self.db, 'items', 2)['name_status'], 'unresolved')

    def test_relationships_do_not_infer_class_from_weapon(self):
        skill = detail(self.db, 'skills', 1)
        self.assertEqual(skill['classes'], ['Gladiator'])
        self.assertEqual(skill['facts']['NeedCostSp'], 30000)
        self.assertEqual(skill['relationships']['levels']['rows'][0]['SkillLv'], 2)
        self.assertEqual(skill['relationships']['effects'][0]['rows'][0]['EffectType'], 'Damage')
        self.assertEqual(detail(self.db, 'items', 1)['classes'], [])

    def test_filters_literal_queries_and_pagination(self):
        self.assertEqual(search(self.db, 'WORN')['total'], 1)
        self.assertEqual(search(self.db, class_name='Gladiator')['rows'][0]['name'], 'Dodge')
        self.assertEqual(search(self.db, kind='items', category='Greatsword')['total'], 1)
        self.assertEqual(search(self.db, "%' OR 1=1 --")['total'], 0)
        self.assertEqual(search(self.db, offset=2, limit=1)['rows'][0]['kind'], 'skills')
        with self.assertRaises(ValueError):
            search(self.db, offset=-1)

    def test_refuses_to_overwrite_existing_catalog(self):
        with self.assertRaises(ValueError):
            build(self.output, self.root)

    def test_http_browser_search_detail_and_missing_route(self):
        server = ThreadingHTTPServer(('127.0.0.1', 0), handler(self.db))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            base = 'http://127.0.0.1:' + str(server.server_port)
            for route in ('/', '/api/meta', '/api/search?q=Worn', '/api/detail?kind=skills&id=1'):
                with urlopen(base + route) as response:
                    self.assertEqual(response.status, 200)
                    body = response.read()
                    if route == '/':
                        self.assertIn(b'textContent', body)
                    else:
                        self.assertIsInstance(json.loads(body), dict)
            for route, status in [('/../../HANDOFF.md', 404), ('/api/detail?kind=items&id=999', 404),
                                  ('/api/search?offset=-1', 400)]:
                with self.assertRaises(HTTPError) as error:
                    urlopen(base + route)
                self.assertEqual(error.exception.code, status)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()
