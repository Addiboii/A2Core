"""Bring tools/data/class-skills.json to a newer client build from two WorldProbe table exports.

The full rebuild (tools/class_skills.py build) needs game-catalog-v2 and starter-skills-tables
for the target build. This tool is the incremental path: it reads PcStatLevel, Skill and
SkillEffect from one WorldProbe export of the target client and rewrites only the fields the
client data owns:

  stats                 PcStatLevel StatList per class and level (whole table, replaced)
  skills.*.cooldown_ms  Skill.dat NeedCoolTime
  skills.*.casting_ms   Skill.dat CastingTime
  skills.*.cost         Skill.dat NeedCostMp/Hp/Sp
  skills.*.damage       SkillEffect EffectValueList flat/ratio by effect_id (same reading as class_skills.damage_rows)

Usage (export made with tools/WorldProbe and a selection listing the three tables):
  python tools/client_build_update.py <export-dir> <selection.json> --baseline <old-export-dir> --build 26262663 [--write]
Without --write it prints the changes only.
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/class-skills.json'


def _short(value):
    return str(value).split('::')[-1]


def load_tables(export, selection):
    paths = json.loads(Path(selection).read_text(encoding='utf-8'))
    out = {}
    for index, path in enumerate(paths):
        name = path.rsplit('/', 1)[-1].removesuffix('.dat')
        file = Path(export) / f'table-{index:03}.json'
        if name in ('PcStatLevel', 'Skill', 'SkillEffect') and file.exists():
            out[name] = json.loads(file.read_text(encoding='utf-8'))['Properties']['Data']
    missing = {'PcStatLevel', 'Skill', 'SkillEffect'} - set(out)
    if missing:
        raise SystemExit(f'export lacks {sorted(missing)}')
    return out


def number(values, i):
    return int(values[i]) if i < len(values) and str(values[i]).lstrip('-').isdigit() else None


def update(data, tables, baseline=None):
    changes = []
    stats = {}
    for row in tables['PcStatLevel']:
        stats.setdefault(_short(row['Class']), {})[str(row['Level'])] = {_short(s['Key']): s['Value'] for s in row['StatList']}
    for cls, levels in stats.items():
        for level, values in levels.items():
            old = data['stats'].get(cls, {}).get(level)
            if old != values:
                changes.append(f'stats {cls} L{level}: {old} -> {values}')
    data['stats'] = stats
    data['max_level'] = max(r['Level'] for r in tables['PcStatLevel'])

    skills = {r['ID']['Value']: r for r in tables['Skill']}
    effects = {r['ID']['Value']: r for r in tables['SkillEffect']}
    if baseline:
        # Only carry what the client changed between the two builds; tracked values the
        # repo deliberately overrides (playability rules) stay as they are.
        old_skills = {r['ID']['Value']: r for r in baseline['Skill']}
        old_effects = {r['ID']['Value']: r for r in baseline['SkillEffect']}
        skills = {k: v for k, v in skills.items() if old_skills.get(k) != v}
        effects = {k: v for k, v in effects.items() if old_effects.get(k, {}).get('EffectValueList') != v.get('EffectValueList')}
    for key, row in data['skills'].items():
        src = skills.get(int(key))
        if not src:
            continue
        new = {'cooldown_ms': int(src.get('NeedCoolTime') or 0), 'casting_ms': int(src.get('CastingTime') or 0),
               'cost': {k: int(src.get('NeedCost' + k.title()) or 0) for k in ('mp', 'hp', 'sp')}}
        for field, value in new.items():
            if row.get(field) != value and (not baseline or int(old_skills.get(int(key), {}).get(
                    {'cooldown_ms': 'NeedCoolTime', 'casting_ms': 'CastingTime'}.get(field, 'x'), -1) or 0) != value or field == 'cost'):
                if field == 'cost' and baseline and {k: int(old_skills.get(int(key), {}).get('NeedCost' + k.title()) or 0) for k in ('mp', 'hp', 'sp')} == value:
                    continue
                changes.append(f'skill {key} {field}: {row.get(field)} -> {value}')
                row[field] = value
        for hit in row.get('damage') or []:
            effect = effects.get(hit.get('effect_id'))
            if not effect:
                continue
            values = effect.get('EffectValueList', [])
            flat, ratio = [number(values, 0), number(values, 1)], [number(values, 2), number(values, 3)]
            if hit.get('flat') != flat or hit.get('ratio') != ratio:
                changes.append(f"skill {key} effect {hit['effect_id']}: flat {hit.get('flat')}->{flat} ratio {hit.get('ratio')}->{ratio}")
                hit['flat'], hit['ratio'] = flat, ratio
            if len(values) > 24:
                pvp = {'flat': [number(values, 21), number(values, 22)], 'ratio': [number(values, 23), number(values, 24)]}
                if hit.get('pvp') != pvp:
                    changes.append(f"skill {key} effect {hit['effect_id']} pvp: {hit.get('pvp')} -> {pvp}")
                    hit['pvp'] = pvp
    return changes


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('export')
    parser.add_argument('selection')
    parser.add_argument('--baseline', help='export of the current build (same selection); apply only client deltas')
    parser.add_argument('--build', required=True, help='Steam build id of the export, recorded in evidence')
    parser.add_argument('--write', action='store_true')
    args = parser.parse_args()
    data = json.loads(DATA.read_text(encoding='utf-8'))
    changes = update(data, load_tables(args.export, args.selection),
                     load_tables(args.baseline, args.selection) if args.baseline else None)
    data.setdefault('evidence', {})['client_build'] = (
        f'Steam build {args.build}: stats, cooldowns, casting, costs and damage values from PcStatLevel/Skill/SkillEffect '
        '(tools/client_build_update.py)')
    for line in changes:
        print(line)
    print(f'{len(changes)} changes')
    if args.write:
        DATA.write_text(json.dumps(data, indent=2) + chr(10), encoding='utf-8')


if __name__ == '__main__':
    main()
