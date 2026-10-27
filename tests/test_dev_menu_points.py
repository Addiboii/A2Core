import json
import unittest

from tools import dev_menu, map_points


class DevMenuTests(unittest.TestCase):
    def test_server_lines_only_and_disabled_rows_dropped(self):
        rows = {'Wing': {'bEnabled': True, 'ConsoleCommand': 's AcquireItem 532400001 1 0'},
                'Mixed': {'bEnabled': True, 'ConsoleCommand': 's setInvincible true\r\nc.devhelper\r\ns  fp  99'},
                'Speed': {'bEnabled': True, 'ConsoleCommand': 's SelfFixStat attackspeed 0 | s SelfFixStat skillspeed 0'},
                'ClientOnly': {'bEnabled': True, 'ConsoleCommand': 'c.devhelper'},
                'Off': {'bEnabled': False, 'ConsoleCommand': 's hp 1'}}
        self.assertEqual(dev_menu.buttons(rows), {
            'Wing': ['AcquireItem 532400001 1 0'], 'Mixed': ['setInvincible true', 'fp 99'],
            'Speed': ['SelfFixStat attackspeed 0', 'SelfFixStat skillspeed 0']})

    def test_embedded_menu_has_the_wing_buttons(self):
        data = json.loads(dev_menu.DATA.read_text(encoding='utf-8'))['buttons']
        self.assertEqual(data['Get_Wing_L_Default'], ['AcquireItem 532400001 1 0'])


class MapPointTests(unittest.TestCase):
    def test_start_prefers_whole_word_start_then_home_then_respawn(self):
        self.assertEqual(map_points.start({'L_Starter_Exit': 0, 'L_Region_Respawn': 0, 'L_Region_Home': 0}), 'L_Region_Home')
        self.assertEqual(map_points.start({'A_Home': 0, 'B_Start': 0}), 'B_Start')
        self.assertEqual(map_points.start({'Only_Point': 0}), 'Only_Point')
        self.assertIsNone(map_points.start({}))

    def test_points_from_mapdata(self):
        table = {'Properties': {'Data': {'MapId': 7, 'TeleportDataMap': [
            {'Key': 'k', 'Value': {'LabelName': 'X_Home', 'Location': {'X': 1.25, 'Y': -2.0, 'Z': 3.04}}}]}}}
        self.assertEqual(map_points.points(table), (7, {'X_Home': [1.2, -2.0, 3.0]}))


if __name__ == '__main__':
    unittest.main()
