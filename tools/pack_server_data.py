"""Collect the data a hosted server needs from research/local into one archive.

Contents (server layout, see deploy/linux/README.md):
  bootstrap/keys/      platform bootstrap fixtures (key, maintenance, session, settings key)
  bootstrap/policy/    the client policy (re-addressed by the host at startup)
  ncguard/fixture.json NCGuard sink responses
  terrain/, navmesh/   extracted ground grids and navigation meshes (optional, large)
  game-catalog-v2/     item/skill catalogue for the database import
  wardrobe/            with --wardrobe: icons, meshes, textures and three.js for the staff
                       dashboard (served to signed-in staff only; about 2.5 GB)

These are captures and extractions from the game: copy the archive only to your own
server; it is not for redistribution.
"""
import argparse
import json
from pathlib import Path
import tarfile

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / 'research/local'
DEFAULTS = {
    'keys': LOCAL / 'bootstrap-fixtures/rsa-candidate-20261009',
    'policy': LOCAL / 'bootstrap-fixtures/policy-lime-20261014',
    'ncguard': LOCAL / 'ncguard-payload-fetch-20261014-072114/fixture.json',
    'terrain': LOCAL / 'terrain',
    'navmesh': LOCAL / 'navmesh',
    'catalog': LOCAL / 'game-catalog-v2',
    'wardrobe-dir': LOCAL / 'wardrobe',
}
# What the host serves from the wardrobe export (Program.cs wardrobe content types), by folder.
WARDROBE_DIRS = ('icons', 'skill-icons', 'meshes', 'textures', 'poses', 'sockets', 'vendor')
WARDROBE_TYPES = ('.glb', '.webp', '.json', '.js')


def wardrobe_members(root):
    """The served part of the wardrobe export (no reports, logs or screenshots)."""
    root = Path(root)
    if not (root / 'wardrobe.json').exists():
        raise FileNotFoundError(root / 'wardrobe.json')
    out = [(root / 'wardrobe.json', 'wardrobe/wardrobe.json')]
    for folder in WARDROBE_DIRS:
        if (root / folder).is_dir():
            out.extend((path, f'wardrobe/{path.relative_to(root).as_posix()}') for path in sorted((root / folder).rglob('*'))
                       if path.is_file() and path.suffix.lower() in WARDROBE_TYPES)
    return out


KEY_FILES = ('app-key.json', 'app-maintenance.json', 'local-test-key.json', 'session-response.candidate.json',
             'authn-response.candidate.json', 'secondary-start-response.candidate.json',
             'secondary-finish-response.candidate.json')


def members(paths, include_maps=True):
    """(source, archive name) pairs; raises when a required input is missing."""
    keys, policy, ncguard = Path(paths['keys']), Path(paths['policy']), Path(paths['ncguard'])
    out = []
    for name in KEY_FILES:
        if (keys / name).exists():
            out.append((keys / name, f'bootstrap/keys/{name}'))
    for required in ('app-key.json', 'local-test-key.json'):
        if not (keys / required).exists():
            raise FileNotFoundError(keys / required)
    if not (policy / 'app-policy.json').exists():
        raise FileNotFoundError(policy / 'app-policy.json')
    out.append((policy / 'app-policy.json', 'bootstrap/policy/app-policy.json'))
    if not ncguard.exists():
        raise FileNotFoundError(ncguard)
    json.loads(ncguard.read_text(encoding='utf-8'))['rules']  # a fixture, not some other JSON
    out.append((ncguard, 'ncguard/fixture.json'))
    catalog = Path(paths['catalog'])
    for name in ('manifest.json', 'entries.jsonl'):
        if not (catalog / name).exists():
            raise FileNotFoundError(catalog / name)
        out.append((catalog / name, f'game-catalog-v2/{name}'))
    if include_maps:
        for kind in ('terrain', 'navmesh'):
            root = Path(paths[kind])
            if root.is_dir():
                out.extend((path, f'{kind}/{path.relative_to(root).as_posix()}')
                           for path in sorted(root.rglob('*.bin')))
    return out


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('output', type=Path, help='archive to write (.tar.gz)')
    parser.add_argument('--no-maps', action='store_true', help='leave out terrain and navmesh')
    parser.add_argument('--wardrobe', action='store_true', help='include the wardrobe export (dashboard icons and 3D preview)')
    parser.add_argument('--wardrobe-only', action='store_true', help='only the wardrobe export (unpack next to existing data)')
    for name, default in DEFAULTS.items():
        parser.add_argument('--' + name, type=Path, default=default, dest=name.replace('-', '_'))
    args = parser.parse_args(argv)
    if args.output.exists():
        parser.error('Output already exists')
    paths = {name: getattr(args, name.replace('-', '_')) for name in DEFAULTS}
    pairs = [] if args.wardrobe_only else members(paths, include_maps=not args.no_maps)
    if args.wardrobe or args.wardrobe_only:
        pairs += wardrobe_members(paths['wardrobe-dir'])
    # Meshes and textures are already compressed: a wardrobe archive is written uncompressed.
    mode = 'w' if args.wardrobe_only else 'w:gz'
    with tarfile.open(args.output, mode, **({} if mode == 'w' else {'compresslevel': 6})) as archive:
        for source, name in pairs:
            archive.add(source, arcname=name, recursive=False)
    size = args.output.stat().st_size
    print(f'{args.output}: {len(pairs)} files, {size / 1e6:.0f} MB')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
