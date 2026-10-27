import json
import unittest
import uuid
from tools.configure_starters import statements
from tools.game_admin import parse_command


class BackendToolsTests(unittest.TestCase):
    def test_admin_parse_and_retry_identity(self):
        character, request = uuid.uuid4(), uuid.uuid4()
        path, command = parse_command(f'/item {character} build 101 2', request)
        self.assertEqual(path, '/api/admin/commands')
        self.assertEqual(command['requestId'], str(request))
        self.assertEqual(command['quantity'], 2)
        self.assertEqual(parse_command(f'/teleport {character} 1000 1.5 -2 3')[1]['x'], 1.5)
        self.assertEqual(parse_command(f'/disable {character}')[0], f'/api/admin/accounts/{character}/disable')
        with self.assertRaises(ValueError):
            parse_command('/level untrusted 99')
        with self.assertRaises(ValueError):
            parse_command(f'/role {character} admin')

    def test_unverified_profiles_are_disabled(self):
        profiles = {
            '1:1': dict(race=1, **{'class': 'Gladiator'}, skills=[10], slots=[[0, 10]]),
            '2:1': dict(race=2, **{'class': 'Gladiator'}, skills=[10], slots=[[0, 10]]),
            '17:1': dict(race=1, **{'class': 'Novice'}, skills=[10], slots=[[0, 10]])}
        script = statements('test', profiles)
        rows = [line for line in script.splitlines() if line.startswith('INSERT INTO game.starter_profiles')]
        self.assertIn(',true,', rows[0])
        # Dark starts at Ishalgen's Region 03 home point (map 1100, from MapData).
        self.assertIn(',true,', rows[1])
        self.assertIn('"mapId": 1100', rows[1])
        # A class without a recovered begin weapon stays disabled with no spawn.
        self.assertIn(',false,', rows[2])
        self.assertIn('"mapId": null', rows[2])
        self.assertIn('"resources": {}', rows[2])
        # Enabled profiles are never rewritten; disabled ones may take the new policy.
        self.assertIn('WHERE NOT game.starter_profiles.enabled', script)

    def test_light_classes_get_their_own_begin_weapon(self):
        profiles = {'2:1': dict(race=1, **{'class': 'Templar'}, skills=[10], slots=[[0, 10]])}
        script = statements('test', profiles)
        self.assertIn(',true,', script)
        self.assertIn('"templateId": 110260001', script)
        self.assertIn("VALUES('test',110260001,1,1,ARRAY[2]", script)
