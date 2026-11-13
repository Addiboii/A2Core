#!/usr/bin/env python3
"""Regenerate the staff dashboard's live-map data from your own AION2 client (local use only, never into git).

    python tools/export_map_images.py [--steps images,names,copy] [--dest <folder or user@host:path>]

Steps (all by default):
  images  tools/MapImageExport: the client's painted world maps -> <out>/<mapId>.webp (<= 2048 px), detail tiles
          <out>/tiles/<mapId>/<x>_<y>.webp for large maps, maps.json (world -> pixel transform from the client's
          Data/WorldMap/<Name>.dat: StartWorldLocation, SectorSize, SectorWorldSize) and skipped.json.
          Needs the client Paks, your archive key report, an Oodle DLL and the usmap (as tools/FullExtract).
  names   names.json from a FullExtract table export (--tables, the folder holding AION2/Content/...): English names
          of maps, NPCs, objects and subzones, the world map's field bosses (WorldMapFieldNamed) and region labels
          (WorldMapUIRegion). The dashboard shows them on hover; without the file it shows internal names.
  copy    copies what the host serves (*.webp, maps.json, names.json, tiles/) to --dest: a folder, or user@host:path
          through scp. The host reads Aion:MapImagesPath, else <Aion:DataDir>/data/map-images.

The extracted pictures and names are the game's own data: keep them on your own machines.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def latest(folder: Path, pattern: str) -> Path | None:
    files = sorted(folder.glob(pattern), key=lambda p: p.stat().st_mtime) if folder.exists() else []
    return files[-1] if files else None


def run_images(args: argparse.Namespace) -> None:
    project = ROOT / 'tools' / 'MapImageExport'
    subprocess.run(['dotnet', 'build', '-c', 'Release', str(project)], check=True, cwd=ROOT)
    dll = project / 'bin' / 'Release' / 'net10.0' / 'MapImageExport.dll'
    usmap = args.usmap or latest(ROOT / 'research' / 'local' / 'tooling' / 'mappings', '*.usmap')
    if not usmap:
        sys.exit('No usmap: pass --usmap.')
    command = ['dotnet', str(dll), args.paks, args.key, args.oodle, str(usmap), 'export', str(args.out),
               str(ROOT / 'tools' / 'data' / 'map-bases.json')]
    if args.geometry:
        command.append(args.geometry)
    # MapImageExport writes under research/local only and resolves its helper DLLs from the repository root.
    subprocess.run(command, check=True, cwd=ROOT)


def load_rows(tables: Path, name: str) -> list[dict]:
    path = tables / 'AION2' / 'Content' / 'Data' / 'Table' / f'{name}.dat.json'
    if not path.exists():
        print(f'  missing {path}', file=sys.stderr)
        return []
    data = json.loads(path.read_text(encoding='utf-8'))
    return data['Properties']['Data']


def run_names(args: argparse.Namespace) -> None:
    if not args.tables:
        sys.exit('names: pass --tables <FullExtract tables folder>.')
    tables = Path(args.tables)
    strings: dict[str, str] = {}
    for file in [tables / 'AION2/Content/L10N/Text' / args.language / 'L10NString.dat.json',
                 tables / 'AION2/Content/Data/Table/L10N' / args.language / 'L10NString.dat.json']:
        if file.exists():
            strings.update(json.loads(file.read_text(encoding='utf-8')).get('Entries', {}))

    def text(key: dict | None) -> str | None:
        k = (key or {}).get('Key')
        if not k or k == 'None':
            return None
        value = strings.get(k) or strings.get(f'String_{k}_body')
        return value.strip() if value else None

    def ident(row: dict) -> int:
        return row['ID']['Value'] if isinstance(row['ID'], dict) else row['ID']

    names: dict = {'language': args.language, 'maps': {}, 'npcs': {}, 'env': {}, 'subzones': {}, 'fieldNamed': {}, 'regions': {}}
    for row in load_rows(tables, 'Map'):
        if (name := text(row.get('Desc'))):
            names['maps'][str(ident(row))] = name
    for row in load_rows(tables, 'NpcData'):
        if (name := text(row.get('Desc'))):
            names['npcs'][str(ident(row))] = name
    for row in load_rows(tables, 'EnvObjData'):
        if (name := text(row.get('Desc'))):
            names['env'][str(ident(row))] = name
    subzone_ids: dict[int, str] = {}
    for row in load_rows(tables, 'Subzone'):
        subzone_ids[ident(row)] = row['Name']
        if (name := text(row.get('DisplayName'))):
            names['subzones'][row['Name']] = name
    for row in load_rows(tables, 'WorldMapFieldNamed'):
        names['fieldNamed'].setdefault(str(row['MapId']['Value']), []).append(row['FieldNamedSpawnerName'])
    for row in load_rows(tables, 'WorldMapUIRegion'):
        if not row.get('bEnableWorldMap', True):
            continue
        names['regions'].setdefault(str(row['MapId']['Value']), []).append({
            'subzone': subzone_ids.get(row['SubzoneId']['Value']), 'name': text(row.get('Desc')),
            'icon': row.get('IconType', '').split('::')[-1], 'rank': row.get('WorldMapInfoType', '').split('::')[-1]})
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / 'names.json').write_text(json.dumps(names, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
    print(f"names.json: {len(names['maps'])} maps, {len(names['npcs'])} NPCs, {len(names['env'])} objects, "
          f"{len(names['subzones'])} subzones, {sum(map(len, names['fieldNamed'].values()))} field bosses")


def served_files(out: Path) -> list[Path]:
    files = [p for p in out.glob('*.webp')] + [out / n for n in ('maps.json', 'names.json') if (out / n).exists()]
    files += [p for p in (out / 'tiles').rglob('*.webp')] if (out / 'tiles').exists() else []
    return files


def run_copy(args: argparse.Namespace) -> None:
    if not args.dest:
        sys.exit('copy: pass --dest <folder or user@host:path>.')
    out = Path(args.out)
    files = served_files(out)
    if ':' in args.dest and not os.path.splitdrive(args.dest)[0]:
        # Remote: one scp of a staging folder with only the served files.
        stage = out.parent / (out.name + '-serve')
        shutil.rmtree(stage, ignore_errors=True)
        for f in files:
            target = stage / f.relative_to(out)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(f, target)
        command = ['scp', '-r', '-q']
        if args.identity:
            command += ['-i', args.identity]
        subprocess.run(command + [str(p) for p in stage.iterdir()] + [args.dest], check=True)
        shutil.rmtree(stage, ignore_errors=True)
    else:
        dest = Path(args.dest)
        for f in files:
            target = dest / f.relative_to(out)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(f, target)
    print(f'copied {len(files)} files to {args.dest}')


def main() -> None:
    local = Path(os.environ.get('LOCALAPPDATA', ''))
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--steps', default='images,names,copy')
    parser.add_argument('--out', default=str(ROOT / 'research' / 'local' / 'map-images'))
    parser.add_argument('--paks', default=str(local / 'A2CorePrivate' / 'AION2' / 'Aion2' / 'Content' / 'Paks'))
    parser.add_argument('--key', default=str(ROOT / 'research' / 'local' / 'archive-key-validated.json'))
    parser.add_argument('--oodle', default=r'C:\Program Files\EA Games\Need for Speed Unbound\oo2core_9_win64.dll')
    parser.add_argument('--usmap')
    parser.add_argument('--geometry', help="FullExtract's maps/geometry-maps-collision.json (maps on shared levels get their painting)")
    parser.add_argument('--tables', help='FullExtract tables folder (holds AION2/Content/...)')
    parser.add_argument('--language', default='en-US')
    parser.add_argument('--dest', help=r'e.g. D:\A2Core\data\map-images or user@server:D:/A2Core/data/map-images')
    parser.add_argument('--identity', help='ssh key for a remote --dest')
    args = parser.parse_args()
    steps = {s.strip() for s in args.steps.split(',') if s.strip()}
    if 'images' in steps:
        run_images(args)
    if 'names' in steps:
        run_names(args)
    if 'copy' in steps and args.dest:
        run_copy(args)


if __name__ == '__main__':
    main()
