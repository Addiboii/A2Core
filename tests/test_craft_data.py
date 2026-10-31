import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))

import craft_data  # noqa: E402

IDS = {'Wing_A': 602460001, 'Wing_B': 602460002, 'Enhance_Stone': 930100023, 'Power_Shard': 533460001,
       'Power_Shard_Superior': 533460004, 'Stone_Block': 930100030, 'Crit_Item': 533460009, 'Fail_Item': 533460010,
       'Sword_A': 110120003, 'Sword_B': 110220003, 'Ore': 632510012}


def table(rows):
    return {'Properties': {'Data': rows}}


MORPH = {
    'ID': {'Value': 31036009}, 'Name': 'Morph_Superior_Shard', 'QualificationRace': 'ERace::Light', 'RegisterType': 'EConversionRegisterType::Default',
    'RegisterValue': 0, 'SuccessProp': 10_000, 'ConversionCountType': 'ERecipeCountType::Try', 'ConversionCount': 7,
    'Product': 'Power_Shard_Superior', 'ProductCount': 10, 'LimitServerCount': 0, 'LimitAccountCount': 0, 'LimitCharacterCount': 7,
    'LimitResetCycleType': 'EResetCycleType::Weekly', 'MultiSelectComponent': 'None',
    'ComponentList': [{'Item': 'Power_Shard', 'Count': 700, 'ReplacementItem': 'None'}, {'Item': 'Enhance_Stone', 'Count': 70, 'ReplacementItem': 'None'}],
}
WING = {
    'ID': {'Value': 31116004}, 'QualificationRace': 'ERace::All', 'RegisterType': 'EConversionRegisterType::Default', 'SuccessProp': 0,
    'ConversionCountType': 'ERecipeCountType::None', 'ConversionCount': 0, 'Product': 'Enhance_Stone', 'ProductCount': 10000,
    'LimitServerCount': 20, 'LimitAccountCount': 0, 'LimitCharacterCount': 0, 'LimitResetCycleType': 'EResetCycleType::Weekly',
    'MultiSelectComponent': 'Wings', 'ComponentList': [],
}
WEAPON = {
    'ID': {'Value': 41033001}, 'QualificationRace': 'ERace::Dark', 'RegisterType': 'EConversionRegisterType::BlacksmithingMastery', 'RegisterValue': 1,
    'SuccessProp': 5_000, 'ConversionCountType': 'ERecipeCountType::Success', 'ConversionCount': 3, 'Product': 'Power_Shard_Superior',
    'ProductCount': 1, 'CriticalSuccessProp': 1_000, 'CriticalSuccessProduct': 'Crit_Item', 'CriticalSuccessProductCount': 2,
    'FailRewardProp': 10_000, 'FailRewardProduct': 'Fail_Item', 'FailRewardProductCount': 1,
    'LimitServerCount': 100, 'LimitAccountCount': 10, 'LimitCharacterCount': 5, 'LimitResetCycleType': 'EResetCycleType::Daily',
    'ComponentList': [{'Item': 'Sword_A', 'Count': 1, 'ReplacementItem': 'Swords'}, {'Item': 'Ore', 'Count': 2, 'ReplacementItem': 'None'}],
}
REPLACEMENTS = [
    {'ID': {'Value': 1}, 'Name': 'Wings', 'ReplacementItemList': [{'Item': 'Wing_A', 'Count': 1}, {'Item': 'Wing_B', 'Count': 1}]},
    {'ID': {'Value': 2}, 'Name': 'Swords', 'ReplacementItemList': [{'Item': 'Sword_A', 'Count': 1}, {'Item': 'Sword_B', 'Count': 1}, {'Item': 'Unknown', 'Count': 1}]},
]


class ConversionRecipeParsingTests(unittest.TestCase):
    def parse(self, *rows):
        return craft_data.conversion_recipes_from_rows(list(rows), REPLACEMENTS, IDS)

    def test_alternate_materials_come_from_the_multi_select_replacement_group(self):
        wing = self.parse(WING)['31116004']
        self.assertEqual(wing[3], [])
        self.assertEqual(wing[4], [[602460001, 1], [602460002, 1]])

    def test_component_replacement_groups_are_kept_per_component(self):
        recipe = self.parse(WEAPON)['41033001']
        self.assertEqual(recipe[3], [[110120003, 1], [632510012, 2]])
        self.assertEqual(recipe[10]['replace'], {'110120003': [[110120003, 1], [110220003, 1]]})

    def test_weekly_server_limit_survives(self):
        wing = self.parse(WING)['31116004']
        self.assertEqual(wing[7:10], [3, 2, 20])

    def test_character_weekly_limit_and_use_count(self):
        recipe = self.parse(MORPH)['31036009']
        self.assertEqual(recipe[:3], [1, 533460004, 10])
        self.assertEqual(recipe[3], [[533460001, 700], [930100023, 70]])
        self.assertEqual(recipe[7:10], [1, 2, 7])
        self.assertEqual(recipe[10]['use'], [1, 7])

    def test_every_limit_scope_is_kept_and_the_widest_is_the_fixed_column(self):
        recipe = self.parse(WEAPON)['41033001']
        self.assertEqual(recipe[7:10], [3, 1, 100])
        self.assertEqual(recipe[10]['limits'], [[3, 100], [2, 10], [1, 5]])

    def test_success_critical_and_fail_reward(self):
        recipe = self.parse(WEAPON)['41033001']
        self.assertEqual(recipe[6], 50_000_000)
        self.assertEqual(recipe[10]['crit'], [10_000_000, 533460009, 2])
        self.assertEqual(recipe[10]['fail'], [100_000_000, 533460010, 1])
        self.assertEqual(self.parse(WING)['31116004'][6], 100_000_000)

    def test_a_recipe_with_an_unknown_product_is_skipped(self):
        self.assertEqual(self.parse({**MORPH, 'Product': 'Nope'}), {})

    def test_all_race_recipes_register_for_both_races(self):
        conversions = craft_data.default_conversions([MORPH, WING, WEAPON])
        self.assertEqual(conversions, {'1': [31036009, 31116004], '2': [31116004, 41033001]})


class TableDiscoveryTests(unittest.TestCase):
    def test_the_replacement_table_is_found_by_its_fields_wherever_it_is_exported(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'table-017.json').write_text(json.dumps(table([MORPH])), encoding='utf-8')
            (root / 'table-020.json').write_text(json.dumps(table(REPLACEMENTS)), encoding='utf-8')
            (root / 'table-021.json').write_text(json.dumps(table([{'Foo': 1}])), encoding='utf-8')
            self.assertEqual(craft_data.find_table(root, None, 'ReplacementItemList'), REPLACEMENTS)
            self.assertEqual(craft_data.find_table(root, 'table-017.json', 'LimitCharacterCount', 'ComponentList'), [MORPH])
            self.assertEqual(craft_data.find_table(root, None, 'NoSuchField'), [])


if __name__ == '__main__':
    unittest.main()
