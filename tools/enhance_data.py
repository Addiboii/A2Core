"""Gear enhancement data for the server, straight from the client tables of the full export (client-full-20261109-0958).

Covers what tools/enchant_data.py (Enchant / EnchantEffect) does not: amplification past the enchant maximum
(ExceedEnchant), Enhance Potential (ItemSurpass), the stats both name (AdditionalStat), manastone rolls
(ImprintMagicStoneProb), succession / inheritance (ItemSuccession, ItemSuccessionProb), soul add (ItemSoulAdd) and the
godstones (GodstoneData, GodstoneCombine, GodstoneGacha). Item names resolve through Item.dat of the same export.

Output tools/data/enhance.json:
  exceed:     group -> one row per exceed level 0, 1, ...: [success /10000, fail correction, fail penalty, Kina,
              [[item id, count], ...], additional stat name of that level ("None" at 0)]; the first row with success 0 is the top
  surpass:    group -> rows per surpass level: [success, fail correction, Kina, [[item id, count], ...], additional stat name]
  additional: AdditionalStat name -> [[stat, value], ...] (only names the two tables use; stat = EStat name without prefix)
  imprint:    ImprintGroupName -> [[stat, weight, [[grade value, chance], ...]], ...] (StatInfos in table order: the
              position + 1 is the EItemMagicStoneGrade the client gets)
  succession: group -> {source group: [[succession count, Kina, [[item id, count], ...]], ...]}
  succession_prob: ESuccessionType name -> [chance at count 0, 1, ...]
  soul_add:   group -> [success, Kina, item id, count]
  godstones:  id -> [category (EEquipCategory name), grade (EItemGrade name), abnormal id, type, can be a combine material]
  godstone_combine: material grade -> [[godstone id, weight], ...] (GodstoneCombine Group = the grade of the materials)
  godstone_gacha:   GetGodstone group -> [[godstone id, weight], ...]
  items:      item id -> [SuccessionGroup, SoulAddGroup] (Item.dat; only items with one of them)

    python tools/enhance_data.py [--tables <export Table dir>]
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/enhance.json'
TABLES = Path('D:/A2Core-local/client-full-20261109-0958/tables/AION2/Content/Data/Table')


def _rows(tables, name):
    data = json.loads((Path(tables) / f'{name}.dat.json').read_text(encoding='utf-8'))
    return (data[0] if isinstance(data, list) else data)['Properties']['Data']


def _enum(value):
    return str(value).split('::')[-1]


def build(tables=TABLES):
    item_rows = _rows(tables, 'Item')
    ids = {r['Name']: r['ID']['Value'] for r in item_rows}
    items = {str(r['ID']['Value']): [r.get('SuccessionGroup') or 'None', r.get('SoulAddGroup') or 'None'] for r in item_rows
             if (r.get('SuccessionGroup') or 'None') != 'None' or (r.get('SoulAddGroup') or 'None') != 'None'}

    def costs(items):
        return [[ids[c['Item']], int(c['Count'])] for c in items or [] if c.get('Item') in ids and int(c.get('Count') or 0) > 0]

    exceed, surpass, used = {}, {}, set()
    for row in sorted(_rows(tables, 'ExceedEnchant'), key=lambda r: (r['Group'], r['CurrentLevel'])):
        exceed.setdefault(row['Group'], []).append([int(row['SuccessProb']), int(row['FailCorrectionProb']), int(row['FailPenalty']),
                                                    int(row['CostGold']), costs(row['CostItems']), row['AdditionalStatName']])
        used.add(row['AdditionalStatName'])
    for row in sorted(_rows(tables, 'ItemSurpass'), key=lambda r: (r['Group'], r['CurrentLevel'])):
        surpass.setdefault(row['Group'], []).append([int(row['SuccessProb']), int(row['FailCorrectionProb']), int(row['CostGold']),
                                                     costs(row['CostItems']), row['AdditionalStatName']])
        used.add(row['AdditionalStatName'])
    additional = {r['Name']: [[_enum(s['Type']), int(s['Value'])] for s in r['AdditionalStats']]
                  for r in _rows(tables, 'AdditionalStat') if r['Name'] in used}

    imprint = {}
    for row in sorted(_rows(tables, 'ImprintMagicStoneProb'), key=lambda r: r['ID']['Value']):
        imprint.setdefault(row['ImprintGroupName'], []).append(
            [_enum(row['AddStatType']), int(row['AddStatProb']), [[int(s['StatValue']), int(s['StatProb'])] for s in row['StatInfos']]])

    succession = {}
    for row in sorted(_rows(tables, 'ItemSuccession'), key=lambda r: (r['Group'], r['SourceGroup'], r['SuccessionCount'])):
        succession.setdefault(row['Group'], {}).setdefault(row['SourceGroup'], []).append(
            [int(row['SuccessionCount']), int(row['CostGold']), costs(row['CostItem'])])
    succession_prob = {}
    for row in sorted(_rows(tables, 'ItemSuccessionProb'), key=lambda r: (r['Type'], r['SuccessionCount'])):
        succession_prob.setdefault(_enum(row['Type']), []).append(int(row['SuccessProb']))
    soul_add = {r['Group']: [int(r['SuccessProb']), int(r['CostGold']), ids.get(r['CostItem'], 0), int(r['CostItemCount'])]
                for r in _rows(tables, 'ItemSoulAdd')}

    godstones = {str(r['GodstoneId']['Value']): [_enum(r['Category']), _enum(r['Grade']), int(r['OptAbnormalId']), int(r['GodstoneType']),
                                                  bool(r['bCanCombineMaterial'])] for r in _rows(tables, 'GodstoneData')}
    combine, gacha = {}, {}
    for row in _rows(tables, 'GodstoneCombine'):
        combine.setdefault(row['Group'], []).append([int(row['GodstoneId']), int(row['Prob'])])
    for row in _rows(tables, 'GodstoneGacha'):
        gacha.setdefault(row['GodstoneGroupId'], []).append([int(row['GodstoneId']), int(row['GodstoneProb'])])

    return {
        'source': 'client-full-20261109-0958: ExceedEnchant, ItemSurpass, AdditionalStat, ImprintMagicStoneProb, ItemSuccession(+Prob), '
                  'ItemSoulAdd, GodstoneData, GodstoneCombine, GodstoneGacha (tools/enhance_data.py)',
        'exceed': exceed, 'surpass': surpass, 'additional': additional, 'imprint': imprint, 'succession': succession,
        'succession_prob': succession_prob, 'soul_add': soul_add, 'godstones': godstones, 'godstone_combine': combine,
        'godstone_gacha': gacha, 'items': items,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--tables', default=str(TABLES))
    args = parser.parse_args()
    data = build(args.tables)
    DATA.write_text(json.dumps(data, separators=(',', ':'), ensure_ascii=False) + '\n', encoding='utf-8')
    print(f"{DATA}: exceed {len(data['exceed'])}, surpass {len(data['surpass'])}, additional {len(data['additional'])}, "
          f"imprint {len(data['imprint'])}, succession {len(data['succession'])}, soul add {len(data['soul_add'])}, godstones {len(data['godstones'])}")


if __name__ == '__main__':
    main()
