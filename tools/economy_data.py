"""Item, economy and legion data for the server, from the current Steam client's own tables (build 26262663).

Source: a WorldProbe export of the client tables (research/local/tables-26262663-items, made with the 2.0.6.0
usmap; every selected table decoded). The table files are matched by name through the export's summary.json.
Settings the client keeps in GlobalSetting.dat come from the 2.0.6 export (research/local/ascension-flight-tables),
because GlobalSetting.dat of build 26262663 no longer parses with that usmap; values we had to choose ourselves are
marked OURS in the output with the reason. Ported in idea from A2Core PR #4 (Artezianec); the PR's committed JSON
(partly from third-party sites) is not used. Rebuild with `python tools/economy_data.py`.

Outputs (compact JSON in tools/data):
- godstones.json: stones (GodstoneData: grade, abnormal, combinable), the item -> gacha group of GetGodstone items
  (Item.dat UseEffect), gacha groups (GodstoneGacha), combine pools per material grade (GodstoneCombine), godstone
  slots per weapon (Item.dat GodStoneSlotCount), settings.
- exceed-enchant.json: items -> exceed group (Item.dat ExceedEnchantGroup), groups (ExceedEnchant rows per level:
  success, fail correction, fail penalty, Kina, costs, stats of that level from AdditionalStat.dat).
- item-rules.json: broker categories (Item.dat TradeCategory), legion storage permission (CanGuildStorage) and the
  items bound on acquisition (ExchangeType SoulBind_Acquire) or never exchangeable (NonExchangeable).
- guild.json: legion levels (GuildData), grade authority flags (GuildMemberGradeAuthority), storage extensions
  (GuildStorageExtension) and the legion settings.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLES = ROOT / 'research/local/tables-26262663-items'
SETTINGS = ROOT / 'research/local/ascension-flight-tables/table-003.json'
OUT = ROOT / 'tools/data'
KINA = 930100001
GRADES = {'Common': 1, 'Rare': 2, 'Legend': 3, 'Unique': 4, 'Epic': 5}


def _short(value):
    return str(value).split('::')[-1]


def _value(value):
    return value.get('Value') if isinstance(value, dict) else value


class Tables:
    """The export's tables by name (summary.json maps each .dat path to its table-NNN.json)."""

    def __init__(self, root=TABLES):
        self.root = Path(root)
        summary = json.loads((self.root / 'summary.json').read_text(encoding='utf-8'))
        self.files = {p['path'].rsplit('/', 1)[-1][:-4]: p['file'] for p in summary['packages'] if p.get('status') == 'table_decoded'}
        self.cache = {}

    def rows(self, name):
        if name not in self.cache:
            data = json.loads((self.root / self.files[name]).read_text(encoding='utf-8'))
            self.cache[name] = data['Properties']['Data']
        return self.cache[name]


def global_settings(path=SETTINGS):
    """GlobalSetting.dat (2.0.6) as one flat {name: value}."""
    if not Path(path).exists():
        return {}
    data = json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']
    merged = {}
    for row in data if isinstance(data, list) else [data]:
        for key, value in row.items():
            if not isinstance(value, (dict, list)):
                merged[key] = value
    return merged


def _item_ids(tables):
    return {r['Name']: _value(r['ID']) for r in tables.rows('Item')}


def godstones(tables, settings):
    stones = {}
    for r in tables.rows('GodstoneData'):
        stones[str(_value(r['GodstoneId']))] = [GRADES[_short(r['Grade'])], int(r.get('OptAbnormalId') or 0), 1 if r.get('bCanCombineMaterial') else 0]
    gacha = {}
    for r in tables.rows('GodstoneGacha'):
        gacha.setdefault(r['GodstoneGroupId'], []).append([int(r['GodstoneId']), int(r['GodstoneProb'])])
    combine = {}
    for r in tables.rows('GodstoneCombine'):
        combine.setdefault(str(GRADES[r['Group']]), []).append([int(r['GodstoneId']), int(r['Prob'])])
    items, slots = {}, {}
    for r in tables.rows('Item'):
        if r.get('UseEffect') == 'EItemUseEffect::GetGodstone' and r.get('UseEffectValues'):
            group = r['UseEffectValues'][0]
            if group in gacha:
                items[str(_value(r['ID']))] = group
        if int(r.get('GodStoneSlotCount') or 0) > 0:
            slots[str(_value(r['ID']))] = int(r['GodStoneSlotCount'])
    count = int(settings.get('godstone_combine_material_count_max') or 4)
    return {
        'source': 'GodstoneData + GodstoneGacha + GodstoneCombine + Item.dat (26262663); settings GlobalSetting 2.0.6 (tools/economy_data.py)',
        'grades': GRADES,
        'stones': stones, 'gacha': gacha, 'combine': combine, 'items': items, 'slots': slots,
        'settings': {
            # CANDIDATE: GlobalSetting names it the maximum; a combine is taken to need exactly that many stones.
            'combine_count': count,
            'combine_max_grade': GRADES.get(_short(settings.get('godstone_combine_max_grade', 'Unique')), 4),
            # CANDIDATE: godstone_cost_money_count is read as the Kina cost of socketing one stone.
            'socket_kina': int(settings.get('godstone_cost_money_count') or 0),
        },
    }


def _stat_sets(tables):
    return {r['Name']: [[_short(s['Type']), int(s['Value'])] for s in r.get('AdditionalStats') or [] if _short(s['Type']) != 'None']
            for r in tables.rows('AdditionalStat')}


def exceed_enchant(tables):
    ids = _item_ids(tables)
    stats = _stat_sets(tables)
    items = {str(_value(r['ID'])): r['ExceedEnchantGroup'] for r in tables.rows('Item') if r.get('ExceedEnchantGroup') not in (None, 'None')}
    groups = {}
    for r in sorted(tables.rows('ExceedEnchant'), key=lambda r: (r['Group'], r['CurrentLevel'])):
        if _short(r.get('CostCurrencyType')) != 'GoldCombined':
            raise ValueError(f"unexpected exceed currency {r.get('CostCurrencyType')}")
        costs = [[ids[c['Item']], int(c['Count'])] for c in r.get('CostItems') or [] if c.get('Item') in ids and int(c.get('Count') or 0) > 0]
        groups.setdefault(r['Group'], []).append([int(r['SuccessProb']), int(r.get('FailCorrectionProb') or 0), int(r.get('FailPenalty') or 0),
                                                  int(r.get('CostGold') or 0), costs, stats.get(r.get('AdditionalStatName'), [])])
    used = set(items.values())
    return {'source': 'ExceedEnchant + AdditionalStat + Item.dat (26262663, tools/economy_data.py)',
            'items': items, 'groups': {g: rows for g, rows in groups.items() if g in used}}


def item_rules(tables):
    trade, guild, bound, closed = {}, [], [], []
    for r in tables.rows('Item'):
        item = _value(r['ID'])
        exchange = _short(r.get('ExchangeType'))
        category = _short(r.get('TradeCategory'))
        if category != 'None' and exchange in ('Exchangeable', 'SoulBind'):
            trade[str(item)] = category
        if int(r.get('CanGuildStorage') or 0) == 1:
            guild.append(item)
        if exchange == 'SoulBind_Acquire':
            bound.append(item)
        elif exchange == 'NonExchangeable':
            closed.append(item)
    return {'source': 'Item.dat TradeCategory/ExchangeType/CanGuildStorage (26262663, tools/economy_data.py)',
            'trade': trade, 'guild_storage': sorted(guild), 'bind_on_acquire': sorted(bound), 'non_exchangeable': sorted(closed)}


def guild(tables, settings):
    levels = [[int(r['Level']), int(r.get('AccumulateExp') or 0), int(r.get('MaxMemberNum') or 0), int(r.get('OfficerNum') or 0)]
              for r in sorted(tables.rows('GuildData'), key=lambda r: r['Level'])]
    grades = {str({'Master': 0, 'Officer': 1, 'Member': 2}[_short(r['Type'])]): int(r['AuthorityFlags']) for r in tables.rows('GuildMemberGradeAuthority')}
    extensions = []
    for r in sorted(tables.rows('GuildStorageExtension'), key=lambda r: _value(r['ID'])):
        if _short(r.get('CurrencyType')) != 'GoldCombined':
            raise ValueError(f"unexpected storage extension currency {r.get('CurrencyType')}")
        extensions.append([int(r['Cost']), int(r.get('NeedGuildLevel') or 1)])
    return {
        'source': 'GuildData + GuildMemberGradeAuthority + GuildStorageExtension (26262663); settings GlobalSetting 2.0.6 (tools/economy_data.py)',
        'levels': levels, 'grade_authority': grades, 'storage_extensions': extensions,
        'settings': {
            'storage_slots': int(settings.get('guild_storage_slot_count_default') or 20),
            'storage_slots_per_extension': int(settings.get('guild_storage_slot_count_per_extension') or 10),
            'storage_cost_store': int(settings.get('guild_storage_cost_store') or 0),
            'storage_cost_takeout': int(settings.get('guild_storage_cost_takeout') or 0),
            'storage_sort_cooldown_ms': int(settings.get('guild_storage_sort_cooltime') or 5000),
            'join_requests_sent': int(settings.get('guild_join_req_count') or 3),
            'join_requests_received': int(settings.get('guild_join_recv_count') or 20),
            'history_days': int(settings.get('guild_info_history_max_days') or 30),
            # OURS: no client table names a daily legion attendance reward; members gain this much legion exp.
            'attendance_exp': 10,
            # OURS: the wallet has no client-side limit; a single donation is capped to keep sums inside i64.
            'wallet_donation_max': 100_000_000_000,
        },
    }


def write(name, data):
    path = OUT / name
    path.write_text(json.dumps(data, separators=(',', ':'), ensure_ascii=False) + '\n', encoding='utf-8')
    return path


def main():
    tables = Tables()
    settings = global_settings()
    for name, data in (('godstones.json', godstones(tables, settings)), ('exceed-enchant.json', exceed_enchant(tables)),
                       ('item-rules.json', item_rules(tables)), ('guild.json', guild(tables, settings))):
        path = write(name, data)
        print(f'{path.relative_to(ROOT)}: {path.stat().st_size} bytes')


if __name__ == '__main__':
    main()
