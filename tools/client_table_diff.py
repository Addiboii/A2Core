"""Diff two client builds: file inventories (tools/ClientDiff) and decoded tables (tools/WorldProbe).

  python tools/client_table_diff.py --inventory old.tsv new.tsv
      added / removed / changed files; changed .dat tables with old and new size.
  python tools/client_table_diff.py --tables <old-export> <new-export> <selection.json> <out-dir>
      per table: rows added / removed / changed (keyed by ID.Value, else Name), the fields that
      changed, and chg-<Table>.json with every changed row's field diff. Writes report.json.

Both exports must come from the same selection so table-NNN indices line up. Outputs belong
under research/local/ (client data).
"""
import argparse
import json
from pathlib import Path


def read_inventory(path):
    out = {}
    for line in Path(path).read_text(encoding='utf-8').splitlines():
        name, size, digest = line.split('\t')
        out[name] = (int(size), digest)
    return out


def inventory(old_path, new_path):
    old, new = read_inventory(old_path), read_inventory(new_path)
    added = sorted(set(new) - set(old))
    removed = sorted(set(old) - set(new))
    changed = sorted(p for p in set(old) & set(new) if old[p] != new[p])
    print(f'added {len(added)} removed {len(removed)} changed {len(changed)}')
    for p in changed:
        if p.endswith('.dat') and '/Data/Map/' not in p:
            print(f'~ {p} {old[p][0]} -> {new[p][0]}')
    for p in added:
        if p.endswith('.dat'):
            print(f'+ {p}')
    for p in removed:
        if p.endswith('.dat'):
            print(f'- {p}')


def row_key(row, index):
    value = row.get('ID')
    if isinstance(value, dict) and 'Value' in value:
        return value['Value']
    if value is not None:
        return value
    return row.get('Name', index)


def flatten(value, prefix='', out=None):
    out = {} if out is None else out
    if isinstance(value, dict):
        for k, v in value.items():
            flatten(v, f'{prefix}.{k}' if prefix else k, out)
    elif isinstance(value, list):
        for i, v in enumerate(value):
            flatten(v, f'{prefix}[{i}]', out)
    else:
        out[prefix] = value
    return out


def rows(path):
    data = json.loads(Path(path).read_text(encoding='utf-8'))['Properties'].get('Data', [])
    data = [data] if isinstance(data, dict) else data
    return {row_key(r, i): r for i, r in enumerate(data)}


def tables(old_dir, new_dir, selection, out_dir):
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    report = {}
    for index, path in enumerate(json.loads(Path(selection).read_text(encoding='utf-8'))):
        name = path.rsplit('/', 1)[-1].removesuffix('.dat')
        old_file, new_file = Path(old_dir) / f'table-{index:03}.json', Path(new_dir) / f'table-{index:03}.json'
        if not (old_file.exists() and new_file.exists()):
            report[name] = {'error': 'not decoded in both builds'}
            print(name, 'not decoded in both builds')
            continue
        old, new = rows(old_file), rows(new_file)
        changed, fields = {}, {}
        for key in new.keys() & old.keys():
            if old[key] != new[key]:
                a, b = flatten(old[key]), flatten(new[key])
                diff = {f: [a.get(f), b.get(f)] for f in a.keys() | b.keys() if a.get(f) != b.get(f)}
                changed[str(key)] = diff
                for f in diff:
                    fields[f.split('[')[0]] = fields.get(f.split('[')[0], 0) + 1
        added = [k for k in new if k not in old]
        removed = [k for k in old if k not in new]
        report[name] = {'old': len(old), 'new': len(new), 'added': len(added), 'removed': len(removed),
                        'changed': len(changed), 'added_ids': [[k, new[k].get('Name', '')] for k in added],
                        'removed_ids': [[k, old[k].get('Name', '')] for k in removed],
                        'changed_fields': dict(sorted(fields.items(), key=lambda kv: -kv[1]))}
        (out_dir / f'chg-{name}.json').write_text(json.dumps(changed, ensure_ascii=False, indent=1, default=str), encoding='utf-8')
        print(f'{name}: {len(old)} -> {len(new)} +{len(added)} -{len(removed)} ~{len(changed)}')
    (out_dir / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=1, default=str), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--inventory', nargs=2, metavar=('OLD_TSV', 'NEW_TSV'))
    parser.add_argument('--tables', nargs=4, metavar=('OLD_DIR', 'NEW_DIR', 'SELECTION', 'OUT_DIR'))
    args = parser.parse_args()
    if args.inventory:
        inventory(*args.inventory)
    if args.tables:
        tables(*args.tables)
    if not (args.inventory or args.tables):
        parser.error('give --inventory and/or --tables')


if __name__ == '__main__':
    main()