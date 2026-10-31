"""Abnormal stacking rules from the client's SkillAbnormal.dat (tools/data/abnormal-stack-rules.json).

Reads the newest WorldProbe export of SkillAbnormal.dat under research/local (any folder whose summary.json names the table;
research/local/tables-26262663-combat holds the current Steam build's) and keeps, per abnormal, GroupId,
AbnormalOverlapCount and AbnormalOverlapTimeType where they differ from the plain case (own group, one at a time, overlap
None): about 300 of 6707 rows. The server re-applies an abnormal in place (0x382b AbnormalChange, OBSERVED for the dodge's
3015, whose row is overlap None) and replaces another of its group; these rows refine that (Renew / Longer / Separate stacks).
AbnormalReplaceType is left out: its values (Replace, Priority, PriorityReplace, Time, None) are not interpreted yet.
Builder idea from A2Core PR #4 (Artezianec); the PR's data fell back to boxes.json defaults, these are the table's values.

    AION_RESEARCH_LOCAL=<main checkout>/research/local python tools/abnormal_stack_rules.py [--table PATH] [--check]
"""
import argparse
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from class_skills import find_table  # noqa: E402

OUT = ROOT / 'tools/data/abnormal-stack-rules.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))


def _short(value):
    return str(value).split('::')[-1]


def _value(v):
    return v.get('Value') if isinstance(v, dict) else v


def rules(rows):
    """{abnormal id: [group, overlap count, overlap time]} for the rows that are not the plain case."""
    out = {}
    for row in rows:
        aid = int(_value(row['ID']))
        group = int(row.get('GroupId') or aid)
        count = int(row.get('AbnormalOverlapCount') or 1)
        overlap = _short(row.get('AbnormalOverlapTimeType', 'None'))
        if group != aid or count != 1 or overlap != 'None':
            out[str(aid)] = [group, count, overlap]
    return dict(sorted(out.items(), key=lambda kv: int(kv[0])))


def build(table):
    rows = json.loads(Path(table).read_text(encoding='utf-8'))['Properties']['Data']
    try:
        source = str(Path(table).resolve().relative_to(LOCAL.resolve().parent.parent)).replace('\\', '/')
    except ValueError:
        source = Path(table).name
    return {'source': source, 'fields': ['group', 'overlap_count', 'overlap_time'], 'abnormals': rules(rows)}


def render(doc):
    return json.dumps(doc, separators=(',', ':')) + '\n'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--table', type=Path, help='SkillAbnormal.dat table json (default: newest export under research/local)')
    parser.add_argument('--check', action='store_true', help='exit 1 when the tracked file differs')
    args = parser.parse_args(argv)
    table = args.table or find_table('SkillAbnormal.dat', LOCAL)
    if table is None:
        raise SystemExit('No SkillAbnormal.dat export under research/local (set AION_RESEARCH_LOCAL)')
    text = render(build(table))
    if args.check:
        same = OUT.is_file() and OUT.read_text(encoding='utf-8') == text
        print('up to date' if same else f'{OUT.name} needs tools/abnormal_stack_rules.py')
        return 0 if same else 1
    OUT.write_text(text, encoding='utf-8')
    print(f"{len(json.loads(text)['abnormals'])} abnormal rules from {table}")
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
