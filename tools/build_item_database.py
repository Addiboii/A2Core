#!/usr/bin/env python3
"""The staff dashboard's item database, built only from your own AION2 client (local use only, never into git).

    python tools/build_item_database.py [--steps data,icons,copy] [--tables <folder>] [--ue-tables <folder>]
                                        [--out research/local/item-db] [--icons-out research/local/item-icons]
                                        [--dest-data <folder or user@host:path>] [--dest-icons <folder or user@host:path>]

Steps (data and icons by default; copy only with --steps ...,copy):
  data   every row of the client item table (Item.dat, 9,000+ items) with the related tables: names and descriptions in
         the client's 8 languages (L10NString), grade/category/type labels (EnumString), stat names and units
         (StatCorrectionNumber), class limits (weapons: PcData ReferMainWeaponType), random sub-stat pools and soul
         binding, set bonuses (ItemSet/ItemSetEffect), enchant/exceed/surpass/soul-add tables (+ per-level stats from
         EnchantEffect/AdditionalStat), box contents (BoxReward/BoxClassReward/RewardPackageItem), crafting
         (CraftRecipe), shops (GameShopGoods/GameShopGoodsList/GameShop, RandomShopGoods) and the client's own
         "where to get" routes (ItemGetRoute: monsters, quests, dungeons, raids, crafts, shops, boxes, gathering...).
         Output in --out:
           index.json         every item, one compact row each (fields named in "fields"), plus label tables
           names/<lang>.json  item id -> name in that language (search in another language)
           items/<id>.json    everything about one item (the dashboard's detail panel), incl. the raw client row
           icons.json         IconRes key -> client texture (UnrealData/UI_ItemIconTable), input of the icons step
  icons  tools/ItemIconExport: decodes those textures from the client archives -> --icons-out/<IconRes>.webp
         (128 px). Needs the client Paks, the archive key report, an Oodle DLL and the usmap (as tools/FullExtract).
         Files already there are kept: delete the folder to redo them.
  copy   copies the data folder to --dest-data and the icons to --dest-icons (a folder, or user@host:path through scp).
         The host reads Aion:ItemDatabasePath, else <Aion:DataDir>/data/item-db; icons Aion:ItemIconsPath, else
         <Aion:DataDir>/data/item-icons (when present), else <Aion:DataDir>/item-icons.

Sources: --tables is a table export: FullExtract's tables/ folder (AION2/Content/Data/Table/<Name>.dat.json and
AION2/Content/L10N/Text/<lang>/L10NString.dat.json) or a dump_tables.ps1 folder (Table_<Name>.json,
Table_L10N_<lang>_L10NString.json). --ue-tables is FullExtract's ue-tables/ folder (for UI_ItemIconTable).
Default: the newest research/local/client-full-*/.
The extracted names, texts and icons are the game's own data: keep them on your own machines.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LANGS = ['en-US', 'de-DE', 'fr-FR', 'es-ES', 'pt-BR', 'ru-RU', 'ja-JP', 'ko-KR']
GRADES = ['Common', 'Rare', 'Legend', 'Unique', 'Epic', 'Mythic', 'Special']
TYPES = ['Equip', 'Usable', 'Misc', 'Currency']
RACE = {'Light': 1, 'Dark': 2}
# Index row layout (index.json "fields"); the dashboard reads the rows by these positions.
FIELDS = ['id', 'name', 'grade', 'type', 'category', 'itemLevel', 'levelMin', 'levelMax', 'race', 'classes', 'icon',
          'tier', 'stats', 'trade', 'stack', 'flags', 'key']
# flags bits in the index row
F_ENCHANT, F_SOULBIND, F_SET, F_BOX, F_CRAFTED, F_SOURCES, F_SELL, F_DELETE, F_STORAGE = 1, 2, 4, 8, 16, 32, 64, 128, 256


def enum(value) -> str:
    """'EItemGrade::Unique' -> 'Unique'."""
    return str(value).split('::')[-1] if value is not None else ''


def ident(value) -> int:
    return int(value['Value']) if isinstance(value, dict) else int(value)


def nonempty(value) -> bool:
    return value not in (None, '', 'None', 0, [], {}, False)


class Tables:
    """Reads a FullExtract tables/ folder or a dump_tables.ps1 folder."""

    def __init__(self, root: Path):
        self.root = root
        self.full = (root / 'AION2' / 'Content').is_dir()
        self.cache: dict[str, list] = {}

    def rows(self, name: str) -> list:
        if name not in self.cache:
            path = (self.root / 'AION2/Content/Data/Table' / f'{name}.dat.json') if self.full else (self.root / f'Table_{name}.json')
            if not path.exists():
                print(f'  missing table {name} ({path})', file=sys.stderr)
                self.cache[name] = []
            else:
                props = json.loads(path.read_text(encoding='utf-8'))['Properties']
                self.cache[name] = props.get('Data', props) if isinstance(props, dict) else props
        return self.cache[name]

    def strings(self, lang: str) -> dict[str, str]:
        files = [self.root / 'AION2/Content/L10N/Text' / lang / 'L10NString.dat.json',
                 self.root / 'AION2/Content/Data/Table/L10N' / lang / 'L10NString.dat.json'] if self.full \
            else [self.root / f'Table_L10N_{lang}_L10NString.json']
        out: dict[str, str] = {}
        for file in files:
            if file.exists():
                out.update(json.loads(file.read_text(encoding='utf-8')).get('Entries', {}))
        return out


class Text:
    """Localised strings: key (or {'Key': ...}) -> text per language."""

    def __init__(self, tables: Tables):
        self.langs = {}
        for lang in LANGS:
            entries = tables.strings(lang)
            if entries:
                self.langs[lang] = entries
        if 'en-US' not in self.langs:
            sys.exit('No en-US L10NString table in --tables.')

    def get(self, key, lang='en-US') -> str | None:
        if isinstance(key, dict):
            key = key.get('Key')
        if not key or key == 'None':
            return None
        entries = self.langs.get(lang, {})
        value = entries.get(key) or entries.get(f'String_{key}_body')
        return value.strip() if value else None

    def all(self, key) -> dict[str, str]:
        out = {}
        for lang in self.langs:
            value = self.get(key, lang)
            if value:
                out[lang] = value
        return out


def build_data(tables: Tables, ue_tables: Path | None, out: Path) -> dict:
    started = time.time()
    text = Text(tables)
    en = text.get
    items = tables.rows('Item')
    if not items:
        sys.exit('No Item table in --tables.')
    by_key = {r['Name']: r for r in items}
    id_of = {r['Name']: ident(r['ID']) for r in items}

    def ref(name, count=None):
        """An item reference by internal name: [id, count] (or [name, count] when the item is not in the table)."""
        return [id_of.get(name, name), count] if count is not None else id_of.get(name, name)

    # ---- labels ----
    enum_label = {}
    for r in tables.rows('EnumString'):
        enum_label[(r['Enum'], r['Value'])] = en(r['String'])
    stat_info = {}
    for r in tables.rows('StatCorrectionNumber'):
        stat = enum(r['StatName'])
        stat_info[stat] = {'name': en(f"StatName_{stat}") or en(r.get('Desc')) or stat,
                           'pct': r.get('IndicationStatWindow', '').endswith('Percent') and r.get('DivideNumber', 0) > 0,
                           'unit': r.get('StatUnit', 1) or 1, 'order': r.get('Order', 0)}
    used_stats: set[str] = set()

    def stat(name) -> str:
        s = enum(name)
        used_stats.add(s)
        return s

    classes_by_weapon: dict[str, list[str]] = defaultdict(list)
    playable = []
    for r in tables.rows('PcData'):
        cls = enum(r['Class'])
        if cls in ('Novice', 'Test', 'Tutorial', 'None') or cls in playable:
            continue
        playable.append(cls)
        classes_by_weapon[enum(r['ReferMainWeaponType'])].append(cls)

    # ---- related tables ----
    sets: dict[str, dict] = {}
    for r in tables.rows('ItemSet'):
        s = sets.setdefault(r['SetName'], {'name': r['SetName'], 'label': text.all(r.get('SetNameDesc')), 'type': enum(r.get('SetType')),
                                           'items': [], 'effects': []})
        s['items'].extend(ref(x) for x in r.get('SetItems', []))
    for r in tables.rows('ItemSetEffect'):
        if r['SetName'] in sets:
            sets[r['SetName']]['effects'].append({'count': r['SetEquipCount'],
                                                  'stats': [[stat(x.get('StatType') or x.get('Stat') or x.get('Key')), x.get('StatValue', x.get('Value'))] for x in r.get('OptStats', [])],
                                                  'abnormal': ident(r['OptAbnormalId']) if nonempty(r.get('OptAbnormalId')) and ident(r['OptAbnormalId']) else None})

    def level_table(rows_name, group_key='Group'):
        groups = defaultdict(list)
        for r in tables.rows(rows_name):
            groups[r[group_key]].append(r)
        for g in groups.values():
            g.sort(key=lambda r: r.get('CurrentLevel', r.get('Level', 0)))
        return groups

    enchant = level_table('Enchant')
    exceed = level_table('ExceedEnchant')
    surpass = level_table('ItemSurpass')
    enchant_effect = level_table('EnchantEffect')
    additional = {r['Name']: [[stat(x['Type']), x['Value']] for x in r.get('AdditionalStats', [])] for r in tables.rows('AdditionalStat')}
    soul_add = {r['Group']: r for r in tables.rows('ItemSoulAdd')}
    soulbind_cost = {r['Name']: r for r in tables.rows('SoulBindCost')}
    substat_skills = defaultdict(list)
    for r in tables.rows('ItemEquipSubstatSkill'):
        substat_skills[r['Group']].append(r)

    def costs(rows):
        return [[ref(c['Item']), c['Count']] for c in rows or []]

    def upgrade_rows(rows, with_stats=False):
        out = []
        for r in rows:
            row = {'level': r.get('CurrentLevel', 0), 'success': r.get('SuccessProb'), 'kina': r.get('CostGold'),
                   'items': costs(r.get('CostItems'))}
            if r.get('FailCorrectionProb'):
                row['failBonus'] = r['FailCorrectionProb']
            if r.get('FailPenalty'):
                row['failPenalty'] = r['FailPenalty']
            if with_stats and nonempty(r.get('AdditionalStatName')):
                row['stats'] = additional.get(r['AdditionalStatName'], [])
            out.append(row)
        return out

    # Packages and boxes
    packages = {r['PackageName']: [[ref(x['ItemName']), x.get('ItemProb'), x.get('ItemCountMin'), x.get('ItemCountMax')] for x in r.get('Items', [])]
                for r in tables.rows('RewardPackageItem')}

    def package_list(rows):
        out = []
        for p in rows or []:
            name = p.get('Package') or p.get('Item') or p.get('PackageName')
            out.append({'name': name, 'prob': p.get('Prob', p.get('PackageProb')), 'count': p.get('Count'), 'items': packages.get(name, [])})
        return out

    boxes: dict[str, list] = defaultdict(list)
    for r in tables.rows('BoxReward'):
        boxes[r['Group']].append({'race': enum(r.get('Race')), 'class': None, 'all': [ref(x['Item'], x.get('Count')) for x in r.get('Items', [])],
                                  'select': [ref(x['Item'], x.get('Count')) for x in r.get('SelectItems', [])],
                                  'packages': package_list(r.get('PackageItems')), 'multiUse': r.get('MultiUseMax'),
                                  'levels': [r.get('LevelMin'), r.get('LevelMax')]})
    for r in tables.rows('BoxClassReward'):
        boxes[r['Group']].append({'race': enum(r.get('Race')), 'class': enum(r.get('Class')), 'all': [ref(x['Item'], x.get('Count')) for x in r.get('Items', [])],
                                  'select': [ref(x['Item'], x.get('Count')) for x in r.get('SelectItems', [])],
                                  'packages': package_list(r.get('PackageItems')), 'levels': [r.get('LevelMin'), r.get('LevelMax')]})
    contained_in: dict[int, set] = defaultdict(set)

    # Crafting
    recipes = {}
    used_in: dict[int, list] = defaultdict(list)
    crafted_by: dict[int, list] = defaultdict(list)
    for r in tables.rows('CraftRecipe'):
        rid = ident(r['ID'])
        rec = {'id': rid, 'name': r['Name'], 'category': enum(r.get('Category')), 'race': enum(r.get('QualificationRace')),
               'mastery': r.get('MasteryLevel'), 'product': ref(r['Product'], r.get('ProductCount')),
               'components': [ref(c['Item'], c.get('Count')) for c in r.get('ComponentList', [])], 'fee': r.get('CraftingFee')}
        recipes[rid] = rec
        if isinstance(rec['product'][0], int):
            crafted_by[rec['product'][0]].append(rid)
        for c in rec['components']:
            if isinstance(c[0], int):
                used_in[c[0]].append(rid)
    conversions = {}
    for r in tables.rows('ConversionRecipe'):
        conversions[ident(r['ID'])] = r

    # Shops (NPC GameShop goods; random shop)
    goods_list_of = defaultdict(list)
    for r in tables.rows('GameShopGoodsList'):
        for g in r.get('GoodsList', []):
            goods_list_of[g].append(r)
    shop_of_list = defaultdict(list)
    for r in tables.rows('GameShop'):
        for gl in r.get('GoodsList', []):
            shop_of_list[gl].append(r)
    sold_at: dict[int, list] = defaultdict(list)
    for r in tables.rows('GameShopGoods'):
        iid = id_of.get(r.get('ItemName'))
        if iid is None:
            continue
        shops = sorted({en(s.get('StringBuyShop')) or s['Name'] for gl in goods_list_of.get(r['Name'], []) for s in shop_of_list.get(gl['Name'], [])})
        sold_at[iid].append({'goods': r['Name'], 'shops': shops, 'bundle': r.get('ItemBundleCount'),
                             'price': [[enum(p['ItemCurrencyType']), p['ItemPrice']] for p in r.get('ItemPriceList', [])],
                             'limit': r.get('ItemLimitCount') if r.get('bItemIsLimited') else None})
    random_shop: dict[int, list] = defaultdict(list)
    for r in tables.rows('RandomShopGoods'):
        iid = id_of.get(r.get('ItemName'))
        if iid is not None:
            random_shop[iid].append(r.get('GoodsGroup'))

    # Names of things the routes point at
    npc_name = {ident(r['ID']): en(r.get('Desc')) or r['Name'] for r in tables.rows('NpcData')}
    map_name = {ident(r['ID']): en(r.get('Desc')) or r['Name'] for r in tables.rows('Map')}
    subzone_name = {ident(r['ID']): en(r.get('DisplayName')) for r in tables.rows('Subzone')}
    quest_title = {r['Name']: en(r.get('Title')) for r in tables.rows('QuestString')}
    quest_name = {ident(r['ID']): quest_title.get(r.get('QuestText')) or r['Name'] for r in tables.rows('Quest')}
    dungeon_name = {ident(r['ID']): en(r.get('Title')) or r['Name'] for r in tables.rows('Dungeon')}
    env_name = {ident(r['ID']): en(r.get('Desc')) or r['Name'] for r in tables.rows('EnvObjData')}
    title_name = {ident(r['ID']): en(r.get('Desc')) or r['Name'] for r in tables.rows('Title')}
    skill_string = {r['Name']: en(r.get('SkillName')) for r in tables.rows('SkillString')}
    skill_name = {ident(r['ID']): skill_string.get(r.get('SkillString_Key')) for r in tables.rows('Skill')}
    battle_pass = {ident(r['ID']): en(r.get('Title')) or r['Name'] for r in tables.rows('BattlePass')}
    vehicle_name = {ident(r['ID']): en(r.get('Desc')) or r['Name'] for r in tables.rows('VehicleList')}
    wing_name = {ident(r['ID']): en(r.get('Desc')) or r['Name'] for r in tables.rows('Wing')}
    godstone_name = {ident(r['GodstoneId']): en(r.get('NameString')) for r in tables.rows('GodstoneData')}

    def place(region):
        if not region:
            return None
        m = map_name.get(ident(region['MapId'])) if nonempty(region.get('MapId')) else None
        s = subzone_name.get(ident(region['SubzoneId'])) if nonempty(region.get('SubzoneId')) else None
        return ' - '.join(x for x in (m, s) if x) or None

    def keyed_ids(value):
        """[{'Key': k, 'Value': {'TableIds': [...]}}] -> [(k, [ids])]."""
        out = []
        for entry in value or []:
            v = entry.get('Value') or {}
            ids = [ident(x) for x in v.get('TableIds', [])] if isinstance(v, dict) else []
            out.append((enum(entry.get('Key')) if not isinstance(entry.get('Key'), dict) else ident(entry['Key']), ids))
        return out

    routes: dict[int, list] = {}
    for r in tables.rows('ItemGetRoute'):
        iid = ident(r['ItemId'])
        src = []

        def add(kind, entries, limit=40):
            entries = [e for e in dict.fromkeys(e for e in entries if e)]
            if entries:
                src.append({'kind': kind, 'entries': entries[:limit], 'more': max(0, len(entries) - limit)})

        # Monsters: one entry per monster with its places ("Faded Papis (Altgard - Silent Hill; Altgard - Crypt)").
        monsters: dict[str, list] = {}
        for m in r.get('MonsterGetRoutes') or []:
            places = monsters.setdefault(str(npc_name.get(ident(m['NpcId']), ident(m['NpcId']))), [])
            where = place(m.get('RegionInfo'))
            if where and where not in places:
                places.append(where)
        add('Monsters', [name + (f" ({'; '.join(p[:6])}{' ...' if len(p) > 6 else ''})" if p else '') for name, p in monsters.items()])
        add('Quests', [quest_name.get(ident(q['QuestId']), str(ident(q['QuestId']))) + ('' if enum(q.get('Class')) in ('None', '') else f" ({enum(q['Class'])})")
                       for q in r.get('QuestGetRoutes') or []])
        add('Party dungeons', [f"{dungeon_name.get(i, i)} ({k})" for k, ids in keyed_ids(r.get('PartyDungeonGetRoutes')) for i in ids])
        add('Raids', [str(dungeon_name.get(ident(x), ident(x))) for x in r.get('RaidInfo') or []])
        add('Daily/growth dungeons', [str(dungeon_name.get(ident(x), ident(x))) for e in r.get('DailyInfo') or []
                                      for k in ('GrowthDungeonIds', 'DailyDungeonIds') for x in (e.get('Value') or {}).get(k, [])])
        add('Awakening dungeons', [str(dungeon_name.get(i, i)) for _, ids in keyed_ids(r.get('AwakenInfo')) for i in ids])
        add('Crafting', [f"{recipes[ident(c['CraftRecipeId'])]['name'] if ident(c['CraftRecipeId']) in recipes else ident(c['CraftRecipeId'])} @ {npc_name.get(ident(c['NpcId']), '?')}" + (f" ({place(c)})" if place(c) else '')
                         for c in r.get('CraftGetRoutes') or []])
        add('Substance morph', [conversions[ident(c['ConversionRecipeId'])]['Name'] if ident(c['ConversionRecipeId']) in conversions else str(ident(c['ConversionRecipeId']))
                                for c in r.get('ConversionRecipeGetRoutes') or []])
        add('Gathering', [f"{env_name.get(ident(g['EnvObjId']), ident(g['EnvObjId']))}" + (f" ({place(g.get('RegionInfo'))})" if place(g.get('RegionInfo')) else '')
                          for g in r.get('GatherGetRoutes') or []])
        add('NPC shops', [f"{npc_name.get(ident(e['Key']), ident(e['Key']))}" + (f" ({map_name.get(ident(e['Value']['MapIdList'][0]))})" if (e.get('Value') or {}).get('MapIdList') else '')
                          for e in r.get('NPCShopInfo') or []])
        add('Coin shops', [str(e.get('Key')) for e in r.get('CoinShopInfo') or []])
        add('Random shop', [f'goods {ident(x)}' for x in r.get('RandomShopInfo') or []])
        boxes_in = [ident(x) for e in (r.get('RewardBoxInfo') or {}).get('BoxDatas', []) for x in (e.get('Value') or {}).get('TableIds', [])]
        boxes_in += [ident(x) for e in (r.get('RewardBoxInfo') or {}).get('BoxClassDatas', []) for x in (e.get('Value') or {}).get('TableIds', [])]
        for b in boxes_in:
            contained_in[iid].add(b)
        add('Achievements', [f"achievement {ident(a['TableId'])} step {a.get('Step')}" for a in r.get('AchievementInfo') or []])
        add('Battle pass', [battle_pass.get(ident(x), str(ident(x))) for x in r.get('BattlePassInfo') or []])
        add('Attendance', [f"{k}: {len(ids)} days" for k, ids in keyed_ids(r.get('AttendanceInfo'))])
        add('Field events', [en(x) or x for e in r.get('FieldEventInfo') or [] for x in (e.get('Value') or {}).get('FiledEvents', [])])
        add('Hidden cubes', [enum(x) for x in r.get('HiddenCubeInfo') or []])
        add('Monoliths', [map_name.get(ident(e['Key']), str(ident(e['Key']))) for e in r.get('MonolithInfo') or []])
        add('Rankings', [f"{enum(e.get('Race'))} {', '.join(enum(c.get('Key')) for c in e.get('ContentsTypes', []))}" for e in r.get('RankingInfo') or []])
        add('Ascension', [k for k, _ in keyed_ids(r.get('AscensionInfo'))])
        add('Dismantling', [ref_name for ref_name in (str(ident(x)) for x in r.get('DecomposeItemInfos') or [])])
        add('Period collection', [str(ident(x)) for x in r.get('PeriodCollectionInfo') or []])
        for flag, label in (('bVehicle', 'Pets'), ('bArena', 'Arena')):
            if r.get(flag):
                src.append({'kind': label, 'entries': ['yes'], 'more': 0})
        if (r.get('BossChallengeInfo') or {}).get('bBossChallenge'):
            src.append({'kind': 'Boss challenge', 'entries': [enum(r['BossChallengeInfo'].get('Race'))], 'more': 0})
        for key, label in (('ExplorationSealInfo', 'Exploration seals'), ('ExplorationGarrisionInfo', 'Garrisons')):
            ids = [ident(x) for x in (r.get(key) or {}).get('TableIds', [])]
            if ids:
                src.append({'kind': label, 'entries': [map_name.get(i, str(i)) for i in ids], 'more': 0})
        if src:
            routes[iid] = src

    # Box items: which box group they open, and the reverse "contained in" for everything inside.
    box_of_item = {}
    for r in items:
        if enum(r.get('UseEffect')) == 'RewardBox' and r.get('UseEffectValues'):
            group = r['UseEffectValues'][0]
            if group in boxes:
                box_of_item[ident(r['ID'])] = group
                for b in boxes[group]:
                    for entry in b['all'] + b['select']:
                        if isinstance(entry[0], int):
                            contained_in[entry[0]].add(ident(r['ID']))
                    for p in b['packages']:
                        for entry in p['items']:
                            if isinstance(entry[0], int):
                                contained_in[entry[0]].add(ident(r['ID']))

    # Icons: IconRes -> texture (UI_ItemIconTable).
    icon_table = {}
    if ue_tables:
        table = ue_tables / 'AION2/Content/UnrealData/UI_ItemIconTable.json'
        if table.exists():
            for key, row in json.loads(table.read_text(encoding='utf-8'))[0]['Rows'].items():
                path = (row.get('ResourceObject') or {}).get('AssetPathName')
                if path:
                    icon_table[key] = path
        else:
            print(f'  missing {table}: no icons.json', file=sys.stderr)

    # ---- per item ----
    (out / 'items').mkdir(parents=True, exist_ok=True)
    (out / 'names').mkdir(parents=True, exist_ok=True)
    index_rows = []
    names = {lang: {} for lang in text.langs}
    icons_used = {}
    categories: dict[str, dict] = {}
    for r in items:
        iid = ident(r['ID'])
        kind = enum(r['ItemType'])
        category = enum(r.get('EquipCategory') or r.get('UsableCategory') or r.get('MiscCategory') or r.get('CurrencyCategory') or '') or 'None'
        enum_name = {'Equip': 'EEquipCategory', 'Usable': 'EUsableCategory', 'Misc': 'EMiscCategory', 'Currency': 'ECurrencyCategory'}.get(kind)
        categories.setdefault(f'{kind}:{category}', {'type': kind, 'key': category, 'label': enum_label.get((enum_name, category)) or category, 'count': 0})['count'] += 1
        item_names = text.all(r.get('Desc'))
        for lang, value in item_names.items():
            names[lang][str(iid)] = value
        name = item_names.get('en-US') or r['Name']
        race = RACE.get(enum(r.get('ItemRace')), 0)
        classes = classes_by_weapon.get(category) if kind == 'Equip' else None
        main = [[stat(s['Key']), s['Value']] for s in r.get('MainStats', []) or []]
        subs = [[stat(s['Key']), s['Value'].get('MinValue'), s['Value'].get('MaxValue'), s['Value'].get('RandomWeight')] for s in r.get('SubStats', []) or []]
        stats_keys = sorted({s[0] for s in main} | {s[0] for s in subs})
        icon = r.get('IconRes') if nonempty(r.get('IconRes')) else None
        if icon and icon in icon_table:
            icons_used[icon] = icon_table[icon]
        flags = 0
        detail = {'id': iid, 'key': r['Name'], 'names': item_names, 'desc': text.all(r.get('DescLong')), 'icon': icon,
                  'type': kind, 'category': category, 'grade': enum(r.get('ItemGrade')), 'tier': enum(r.get('ItemTier')),
                  'itemLevel': r.get('ItemLevel'), 'levelMin': r.get('PermitLevelMin'), 'levelMax': r.get('PermitLevelMax'),
                  'race': race, 'classes': classes, 'stack': r.get('MaxStackCount'), 'trade': enum(r.get('ExchangeType')),
                  'tradeCategory': enum(r.get('TradeCategory')),
                  'sell': [enum(r.get('SellCurrencyType')), r.get('SellPrice')] if r.get('CanSell') else None,
                  'storage': {'account': bool(r.get('CanStorage')), 'legion': bool(r.get('CanGuildStorage')), 'character': bool(r.get('CanCharacterStorage'))},
                  'canDelete': bool(r.get('CanDelete')), 'canLock': bool(r.get('CanLock')),
                  'decompose': {'kina': r.get('DecomposeCostGold'), 'groups': r.get('DecomposeResultItemGroup')} if r.get('CanDecompose') else None}
        if r.get('CanSell'):
            flags |= F_SELL
        if r.get('CanDelete'):
            flags |= F_DELETE
        if r.get('CanStorage'):
            flags |= F_STORAGE
        if nonempty(r.get('FixedLimitTime', {}).get('Ticks')) or nonempty(r.get('MaintainTime')):
            detail['period'] = {'fixedTicks': r.get('FixedLimitTime', {}).get('Ticks'), 'minutes': r.get('MaintainTime'), 'delete': enum(r.get('DeleteType'))}
        if kind == 'Equip':
            equip = {'basicLevel': r.get('ItemBasicLevel'), 'mainStats': main, 'subStats': subs,
                     'subStatCount': [r.get('SubStatCountMin'), r.get('SubStatCountMax')],
                     'manastoneSlots': r.get('MagicStoneSlotCount'), 'godstoneSlots': r.get('GodStoneSlotCount'),
                     'armor': enum(r.get('ArmorType')), 'attackSpeed': r.get('AttackSpeed'),
                     'range': r.get('WeaponAttackRange'), 'skins': r.get('ExtractionSkins') if r.get('bCanExtractionSkin') else None,
                     'succession': r.get('SuccessionGroup') if nonempty(r.get('SuccessionGroup')) else None}
            if r.get('SoulbindRandomStat') or r.get('SoulbindFixedSubStats'):
                flags |= F_SOULBIND
                sb = {'random': bool(r.get('SoulbindRandomStat')), 'count': r.get('SoulbindRandomStatCount'),
                      'fixed': [[stat(s.get('Key') or s.get('StatType')), s.get('Value') if not isinstance(s.get('Value'), dict) else s['Value'].get('MinValue')]
                                for s in r.get('SoulbindFixedSubStats') or []],
                      'skillCountMax': r.get('SoulbindRandomSkillCountMax'), 'skillChance': r.get('SubStatSkillSelectProb') if r.get('bUseSubStatSkill') else None,
                      'skillGroup': r.get('SoulbindRandomSkillGroup') if nonempty(r.get('SoulbindRandomSkillGroup')) else None}
                cost = soulbind_cost.get(r.get('SoulbindCostName'))
                if cost:
                    sb['cost'] = [[c.get('CostA_Id'), c.get('CostA_Amount'), ref(c.get('CostB_Id')), c.get('CostB_Amount')] for c in cost.get('CostDatas', [])]
                    sb['reroll'] = [cost.get('RerollCostCount'), ref(cost.get('RerollCostItem')), cost.get('RerollCostItemCount')]
                skills = substat_skills.get(sb['skillGroup'] or '', [])
                if skills:
                    sb['skills'] = [[enum(s['Class']), ident(s['SkillId']), skill_name.get(ident(s['SkillId'])), s.get('SkillRandomProb')] for s in skills][:200]
                equip['soulbind'] = sb
            if r.get('CanEnchant') and r.get('EnchantGroup') in enchant:
                flags |= F_ENCHANT
                effects = enchant_effect.get(r.get('EnchantEffectGroup'), [])
                equip['enchant'] = {'group': r['EnchantGroup'], 'levels': upgrade_rows(enchant[r['EnchantGroup']]),
                                    'effects': [{'level': e['Level'], 'itemLevel': e.get('ItemLevelValue'),
                                                 'stats': [[stat(s['StatType']), s['StatValue']] for s in e.get('StatList', [])]} for e in effects]}
            if r.get('ExceedEnchantGroup') in exceed:
                equip['exceed'] = {'group': r['ExceedEnchantGroup'], 'levels': upgrade_rows(exceed[r['ExceedEnchantGroup']], with_stats=True)}
            if r.get('SurpassGroup') in surpass:
                equip['surpass'] = {'group': r['SurpassGroup'], 'levels': upgrade_rows(surpass[r['SurpassGroup']], with_stats=True)}
            if r.get('SoulAddGroup') in soul_add:
                s = soul_add[r['SoulAddGroup']]
                equip['soulAdd'] = {'group': r['SoulAddGroup'], 'success': s.get('SuccessProb'), 'kina': s.get('CostGold'),
                                    'items': [[ref(s.get('CostItem')), s.get('CostItemCount')]]}
            item_sets = [sets[n] for n in r.get('SetNames') or [] if n in sets]
            if item_sets:
                flags |= F_SET
                equip['sets'] = item_sets
            detail['equip'] = equip
        if kind == 'Usable':
            effect = enum(r.get('UseEffect'))
            values = r.get('UseEffectValues') or []
            use = {'category': category, 'effect': effect, 'values': values, 'cooldown': r.get('Cooltime'),
                   'cooldownGroup': enum(r.get('CoolTimeGroup')), 'maxUse': r.get('MaxUseCount'),
                   'casting': r.get('CastingTime') if r.get('NeedCasting') else None, 'quickSlot': bool(r.get('bCanQuickSlot')),
                   'autoUseOnPickup': bool(r.get('AutoUseWhenAcquire'))}
            first = values[0] if values else None
            label = None
            try:
                number = int(first) if first is not None else None
            except (TypeError, ValueError):
                number = None
            if effect == 'GetTitle' and number:
                label = title_name.get(number)
            elif effect == 'Skill' and number:
                label = skill_name.get(number)
            elif effect in ('GetVehicle', 'GetVehicleSoul') and number:
                label = vehicle_name.get(number)
            elif effect == 'GetWing' and number:
                label = wing_name.get(number)
            elif effect == 'GetGodstone' and number:
                label = godstone_name.get(number)
            if label:
                use['target'] = label
            detail['use'] = use
            group = box_of_item.get(iid)
            if group:
                flags |= F_BOX
                detail['box'] = {'group': group, 'variants': boxes[group]}
        if kind == 'Misc':
            detail['misc'] = {'category': category, 'values': r.get('CategoryValues'), 'sealStone': r.get('SealStone') if nonempty(r.get('SealStone')) else None}
        if iid in crafted_by:
            flags |= F_CRAFTED
            detail['crafted'] = [recipes[x] for x in crafted_by[iid]][:20]
        if iid in used_in:
            detail['usedIn'] = [{'recipe': recipes[x]['name'], 'product': recipes[x]['product']} for x in used_in[iid]][:60]
        if iid in sold_at:
            detail['shops'] = sold_at[iid][:30]
        if iid in random_shop:
            detail.setdefault('randomShop', sorted(set(random_shop[iid])))
        if iid in contained_in:
            detail['containedIn'] = sorted(contained_in[iid])[:100]
        if iid in routes:
            flags |= F_SOURCES
            detail['sources'] = routes[iid]
        detail['raw'] = r
        (out / 'items' / f'{iid}.json').write_text(json.dumps(detail, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
        index_rows.append([iid, name, GRADES.index(detail['grade']) if detail['grade'] in GRADES else -1, TYPES.index(kind) if kind in TYPES else -1,
                           category, r.get('ItemLevel') or 0, r.get('PermitLevelMin') or 0, r.get('PermitLevelMax') or 0, race,
                           classes, icon if icon in icons_used else None, int(enum(r.get('ItemTier'))[1:] or 0) if enum(r.get('ItemTier')).startswith('t') else 0,
                           stats_keys, detail['trade'], r.get('MaxStackCount') or 0, flags, r['Name']])

    # Unused item files from an earlier build (an item removed from the client) go away.
    keep = {f'{row[0]}.json' for row in index_rows}
    for file in (out / 'items').glob('*.json'):
        if file.name not in keep:
            file.unlink()
    for lang, mapping in names.items():
        (out / 'names' / f'{lang}.json').write_text(json.dumps(mapping, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
    labels = {
        'grades': [{'key': g, 'label': enum_label.get(('EItemGrade', g)) or g} for g in GRADES],
        'types': [{'key': t, 'label': {'Equip': 'Equipment', 'Usable': 'Consumables & usable', 'Misc': 'Materials & misc', 'Currency': 'Currency'}[t]} for t in TYPES],
        'categories': sorted(categories.values(), key=lambda c: (TYPES.index(c['type']), c['label'])),
        'classes': [{'key': c, 'label': enum_label.get(('ECharacterClass', c)) or c} for c in playable],
        'stats': {s: stat_info.get(s, {'name': s, 'pct': False, 'unit': 1, 'order': 9999}) for s in sorted(used_stats)},
        'trade': {t: label for t, label in (('Exchangeable', 'Tradeable'), ('NonExchangeable', 'Not tradeable'),
                                            ('SoulBind', 'Soul-bound'), ('SoulBind_Acquire', 'Soul-bound on pickup'))},
        'currencies': {enum(r['CurrencyCategory']): en(r.get('Desc')) or enum(r['CurrencyCategory']) for r in tables.rows('CurrencyInfo')},
        'races': ['All', 'Elyos', 'Asmodian'],
        'flags': {'enchant': F_ENCHANT, 'soulbind': F_SOULBIND, 'set': F_SET, 'box': F_BOX, 'crafted': F_CRAFTED,
                  'sources': F_SOURCES, 'sell': F_SELL, 'delete': F_DELETE, 'storage': F_STORAGE},
    }
    index = {'version': 1, 'generated': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), 'source': 'AION2 client tables (tools/build_item_database.py)',
             'languages': sorted(text.langs, key=LANGS.index), 'fields': FIELDS, 'labels': labels, 'items': index_rows}
    (out / 'index.json').write_text(json.dumps(index, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
    (out / 'icons.json').write_text(json.dumps(icons_used, indent=0), encoding='utf-8')
    summary = {'items': len(index_rows), 'icons': len(icons_used), 'languages': len(text.langs), 'withSources': len(routes),
               'boxes': len(box_of_item), 'sets': len(sets), 'seconds': round(time.time() - started, 1)}
    print(json.dumps(summary))
    return summary


def latest_full_export() -> Path | None:
    folders = sorted((ROOT / 'research' / 'local').glob('client-full-*'), key=lambda p: p.name)
    return folders[-1] if folders else None


def run_icons(args) -> None:
    project = ROOT / 'tools' / 'ItemIconExport'
    subprocess.run(['dotnet', 'build', '-c', 'Release', str(project)], check=True, cwd=ROOT)
    dll = project / 'bin' / 'Release' / 'net10.0' / 'ItemIconExport.dll'
    usmap = args.usmap or next(iter(sorted((ROOT / 'research/local/tooling/mappings').glob('*.usmap'))), None)
    if not usmap:
        sys.exit('No usmap: pass --usmap.')
    icons = Path(args.out) / 'icons.json'
    if not icons.exists():
        sys.exit(f'{icons} is missing: run the data step first.')
    subprocess.run(['dotnet', str(dll), args.paks, args.key, args.oodle, str(usmap), str(icons), str(args.icons_out), str(args.icon_size)],
                   check=True, cwd=ROOT)


def copy(src: Path, dest: str, pattern: str | None = None) -> None:
    if ':' in dest and '@' in dest.split(':')[0]:
        # user@host:path through scp (the whole folder's content).
        subprocess.run(['scp', '-q', '-r', *( [str(p) for p in src.iterdir()] ), dest], check=True)
    else:
        target = Path(dest)
        target.mkdir(parents=True, exist_ok=True)
        shutil.copytree(src, target, dirs_exist_ok=True)
    print(f'copied {src} -> {dest}')


def main() -> None:
    full = latest_full_export()
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--steps', default='data,icons')
    parser.add_argument('--tables', default=str(full / 'tables') if full else None)
    parser.add_argument('--ue-tables', default=str(full / 'ue-tables') if full else None)
    parser.add_argument('--out', default=str(ROOT / 'research/local/item-db'))
    parser.add_argument('--icons-out', default=str(ROOT / 'research/local/item-icons'))
    parser.add_argument('--icon-size', type=int, default=128)
    parser.add_argument('--paks', default=os.path.expandvars(r'%LOCALAPPDATA%\A2CorePrivate\AION2\Aion2\Content\Paks'))
    parser.add_argument('--key', default=str(ROOT / 'research/local/archive-key-validated.json'))
    parser.add_argument('--oodle', default=r'C:\Program Files\EA Games\Need for Speed Unbound\oo2core_9_win64.dll')
    parser.add_argument('--usmap')
    parser.add_argument('--dest-data')
    parser.add_argument('--dest-icons')
    args = parser.parse_args()
    steps = {s.strip() for s in args.steps.split(',') if s.strip()}
    if 'data' in steps:
        if not args.tables:
            sys.exit('data: pass --tables.')
        build_data(Tables(Path(args.tables)), Path(args.ue_tables) if args.ue_tables else None, Path(args.out))
    if 'icons' in steps:
        run_icons(args)
    if 'copy' in steps:
        if args.dest_data:
            copy(Path(args.out), args.dest_data)
        if args.dest_icons:
            copy(Path(args.icons_out), args.dest_icons)


if __name__ == '__main__':
    main()
