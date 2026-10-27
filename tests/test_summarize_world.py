import json
from pathlib import Path
import tempfile
import unittest
from tools.summarize_world import summarize


class WorldSummaryTests(unittest.TestCase):
    def test_resolves_local_component_and_keeps_absent_fields_unknown(self):
        with tempfile.TemporaryDirectory() as tmp:
            p=Path(tmp)
            spawn={'Name':'Spawn','Properties':{'SpawnUnitId':-42,'RootComponent':{'ObjectPath':'/Game/Map/Test.1'}}}
            component={'Name':'Root','Properties':{'RelativeLocation':{'X':1,'Y':2,'Z':3}}}
            for i,obj in enumerate((spawn,component)):(p/f'{i}.json').write_text(json.dumps(obj))
            manifest={'packages':[{'path':'AION2/Content/Map/Test','status':'inspected','exports':[
                {'index':0,'status':'decoded','file':'0.json','ExportType':'AionSpawnUnitSingle'},
                {'index':1,'status':'decoded','file':'1.json','ExportType':'SceneComponent'}]}]}
            (p/'summary.json').write_text(json.dumps(manifest))
            r=summarize(p)['spawns'][0]
            self.assertTrue(r['root_component_resolved'])
            self.assertEqual(r['observed_properties']['SpawnUnitId'],-42)
            self.assertNotIn('SpawnActorType',r['observed_properties'])
            self.assertNotIn('RelativeRotation',r['transform'])
            spawn['Properties']['RootComponent']['ObjectPath']='/Game/Elsewhere.1'
            (p/'0.json').write_text(json.dumps(spawn))
            self.assertFalse(summarize(p)['spawns'][0]['root_component_resolved'])
