import unittest

from tools import equipment_rules

# (creation ID, race, weapon type): two Light and two Dark classes plus a weaponless Novice.
PROFILES = [(1, 1, 'Greatsword'), (2, 1, 'Sword'), (9, 2, 'Greatsword'), (10, 2, 'Sword'), (17, 1, None)]


class EquipmentRuleTests(unittest.TestCase):
    def test_torso_goes_to_slot_5_with_its_level_for_every_class(self):
        raw = {'ItemType': 'EItemType::Equip', 'EquipCategory': 'EEquipCategory::Torso', 'PermitLevelMin': 45, 'ItemRace': 'ERace::All'}
        self.assertEqual(equipment_rules.rules(raw, PROFILES), [(5, 45, [1, 2, 9, 10, 17])])

    def test_race_items_and_paired_slots(self):
        raw = {'ItemType': 'EItemType::Equip', 'EquipCategory': 'EEquipCategory::Ring', 'PermitLevelMin': 0, 'ItemRace': 'ERace::Dark'}
        self.assertEqual(equipment_rules.rules(raw, PROFILES), [(13, 1, [9, 10]), (14, 1, [9, 10])])

    def test_weapons_go_to_the_main_hand_of_their_classes_only(self):
        raw = {'ItemType': 'EItemType::Equip', 'EquipCategory': 'EEquipCategory::Sword', 'PermitLevelMin': 10, 'ItemRace': 'ERace::All'}
        self.assertEqual(equipment_rules.rules(raw, PROFILES), [(1, 10, [2, 10])])
        raw = {'ItemType': 'EItemType::Equip', 'EquipCategory': 'EEquipCategory::Greatsword', 'ItemRace': 'ERace::Light'}
        self.assertEqual(equipment_rules.rules(raw, PROFILES), [(1, 1, [1])])

    def test_guarder_is_every_classes_off_hand_and_unused_weapons_are_skipped(self):
        raw = {'ItemType': 'EItemType::Equip', 'EquipCategory': 'EEquipCategory::Guarder', 'ItemRace': 'ERace::All'}
        self.assertEqual(equipment_rules.rules(raw, PROFILES), [(2, 1, [1, 2, 9, 10, 17])])
        self.assertEqual(equipment_rules.rules({'ItemType': 'EItemType::Equip', 'EquipCategory': 'EEquipCategory::Gauntlet'}, PROFILES), [])

    def test_arcana_slots_and_skipped_rows(self):
        raw = {'ItemType': 'EItemType::Equip', 'EquipCategory': 'EEquipCategory::ArcanaBell'}
        self.assertEqual(equipment_rules.rules(raw, PROFILES), [(44, 1, [1, 2, 9, 10, 17])])
        self.assertEqual(equipment_rules.rules({'ItemType': 'EItemType::Usable'}, PROFILES), [])
        self.assertEqual(equipment_rules.rules({'ItemType': 'EItemType::Equip', 'EquipCategory': 'EEquipCategory::Costume'}, PROFILES), [])

    def test_profiles_give_every_playable_class_its_weapon(self):
        weapons = {weapon for _, _, weapon in equipment_rules.profiles()}
        self.assertTrue({'Greatsword', 'Sword', 'Dagger', 'Mace', 'Bow', 'Orb', 'Magicbook', 'Staff'} <= weapons)

    def test_old_rules_from_this_tool_are_replaced(self):
        sql = equipment_rules.statements('b1', [], PROFILES)
        self.assertIn("DELETE FROM game.equipment_rules WHERE build_id='b1' AND evidence LIKE '%(tools/equipment_rules.py)'", sql)


if __name__ == '__main__':
    unittest.main()
