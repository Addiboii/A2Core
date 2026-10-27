import unittest

from tools.chat_service import ChatService, ItemCatalog
from tools.starter_casts import build_resource_update


class ChatTests(unittest.TestCase):
    def setUp(self):
        self.now = 0
        self.events = []
        self.resources = dict(hp=10, hp_max=1500, mp=0, mp_max=200,
                              sp=2, sp_max=100000)
        self.chat = ChatService(gm_accounts=['admin'], audit=self.events.append,
                                clock=lambda: self.now,
                                catalog=ItemCatalog([{'ID': {'Value': 110160001},
                                                      'Name': 'Starter_Greatsword'}]))
        self.add('a', 'admin', self.resources)
        self.add('b', 'player', {})
        self.add('c', 'remote', {}, map_id=2000)

    def add(self, session, account, resources, map_id=1000):
        self.chat.register(session, account=account, name='Name_' + session,
                           world=1, map_id=map_id, state={'resources': resources})

    def test_room_routing_and_move(self):
        self.assertEqual([x.recipient for x in self.chat.receive('a', 'hello')], ['a', 'b'])
        self.chat.move('b', world=2, map_id=1000)
        self.assertEqual([x.recipient for x in self.chat.receive('a', 'hello')], ['a'])

    def test_unauthenticated_and_disconnected(self):
        self.chat.disconnect('a')
        with self.assertRaises(PermissionError):
            self.chat.receive('a', '/gm restore')

    def test_permission_is_not_display_name(self):
        before = dict(self.resources)
        result = self.chat.receive('b', '/gm restore')
        self.assertIn('permission', result[0].text)
        self.assertEqual(self.resources, before)
        self.assertEqual(self.events[-1]['result'], 'denied')
        self.assertEqual(len(result), 1)

    def test_restore_state_and_wire_agree(self):
        result = self.chat.receive('a', '/gm restore')[0]
        self.assertEqual((self.resources['hp'], self.resources['mp'], self.resources['sp']),
                         (1500, 200, 100000))
        self.assertEqual(result.packets, (build_resource_update(self.resources),))
        self.assertEqual(self.events[-1]['result'], 'applied')

    def test_audit_failure_prevents_mutation(self):
        def unavailable(event):
            raise OSError('audit disk unavailable')
        self.chat.audit = unavailable
        with self.assertRaises(OSError):
            self.chat.receive('a', '/gm restore')
        self.assertEqual(self.resources['hp'], 10)

    def test_missing_audit_prevents_mutation(self):
        self.chat.audit = None
        self.assertIn('audit', self.chat.receive('a', '/gm restore')[0].text)
        self.assertEqual(self.resources['hp'], 10)

    def test_search_and_no_command_broadcast(self):
        result = self.chat.receive('a', '/gm item search greatsword')
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0].recipient, 'a')
        self.assertIn('110160001', result[0].text)
        self.assertNotIn('greatsword', str(self.events))

    def test_limit_shared_across_sessions_and_reconnects(self):
        self.add('d', 'player', {})
        for _ in range(5):
            self.chat.receive('b', 'hi')
        self.chat.disconnect('b')
        self.assertIn('wait', self.chat.receive('d', 'hi')[0].text)
        self.now = 5
        self.assertEqual(len(self.chat.receive('d', 'hi')), 2)

    def test_invalid_input_and_unsupported_commands(self):
        for text in ['a' * 101, '', '\n', 'a\x00b', '   ']:
            self.assertEqual(self.chat.receive('a', text)[0].channel, 'system')
        self.assertIn('syntax', self.chat.receive('a', '/gm "')[0].text)
        self.assertIn('Unavailable', self.chat.receive('a', '/gm level 50')[0].text)
        self.assertEqual(self.resources['hp'], 10)


if __name__ == '__main__':
    unittest.main()
