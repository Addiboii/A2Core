import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

import expedition_data  # noqa: E402


class ExpeditionDataTests(unittest.TestCase):
    def test_tickets_and_dungeon_uses(self):
        tables = {
            'ContentsTicket': [
                {'ID': {'Value': 80000001}, 'Name': 'Contents_Ticket_Awaken', 'TicketType': 'ETicketType::Count',
                 'RechargeMaxCount': 3, 'WeeklyRechargeDay': 'EDayOfWeekType::Wednesday',
                 'WeeklyRechargeTime': {'Ticks': 576000000000}, 'WeeklyRechargeCount': 3,
                 'OwnerType': 'EContentsTicketOwnerType::Character'},
                {'ID': {'Value': 3}, 'Name': 'Contents_Ticket_Abyss', 'TicketType': 'ETicketType::Time',
                 'RechargeMaxTime': 25200, 'DailyRechargeCount': 0, 'OwnerType': 'EContentsTicketOwnerType::Server'}],
            'MapEntrance': [
                {'Name': 'Enter_Awaken_01', 'ContentsTicketList': [
                    {'Name': 'Contents_Ticket_Awaken', 'Count': 1, 'DeductType': 'ETicketDeductType::Enter', 'bCheckEnterCondition': True},
                    {'Name': 'Unknown_Ticket', 'Count': 1, 'DeductType': 'ETicketDeductType::Enter'}]},
                {'Name': 'Enter_Quiet', 'ContentsTicketList': []}],
            'Dungeon': [{'ID': {'Value': 910011}, 'Name': 'Awaken_01'}, {'ID': {'Value': 5}, 'Name': 'Quiet'}],
        }
        out = expedition_data.build(tables)
        self.assertEqual(out['tickets']['80000001'], {'name': 'Contents_Ticket_Awaken', 'type': 'Count', 'max': 3,
                                                      'weekly': [3, 57600, 3]})
        self.assertEqual(out['tickets']['3'], {'name': 'Contents_Ticket_Abyss', 'type': 'Time', 'max': 25200, 'owner': 'Server'})
        self.assertEqual(out['dungeons'], {'910011': [[80000001, 1, 'Enter']]})


if __name__ == '__main__':
    unittest.main()
