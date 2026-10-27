"""Monitor a development-copy host launch using cached Steam launch metadata.

Writes backups and reports under research/local. Does not edit saved settings,
restore files, terminate the game, or save opaque launch arguments in reports.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
import uuid

try:
    from .audit_saved_data import snapshot, compare
    from .inspect_bootstrap import read_appinfo, argument_summary, DEFAULT_STEAM, APP_ID
    from .prepare_sandbox_test import EXPECTED_SHA256
except ImportError:
    from audit_saved_data import snapshot, compare
    from inspect_bootstrap import read_appinfo, argument_summary, DEFAULT_STEAM, APP_ID
    from prepare_sandbox_test import EXPECTED_SHA256

ROOT = Path(__file__).resolve().parents[1]


def launch_arguments(cached):
    """Accept only the observed cached launch shape; replace its saved suffix."""
    tokens = cached.split()
    allowed = re.compile(r'-(?:steam|NoDebugExecBindings|saveddirsuffix=Steam|lp:[A-Za-z0-9+/=]+)', re.I)
    if len(tokens) != 4 or any(not allowed.fullmatch(token) for token in tokens):
        raise ValueError('Cached launch shape changed; review before launching')
    kinds = [token.split(':', 1)[0].split('=', 1)[0].lower() for token in tokens]
    if set(kinds) != {'-steam', '-nodebugexecbindings', '-saveddirsuffix', '-lp'}:
        raise ValueError('Cached launch switches are incomplete or duplicated')
    return ['-saveddirsuffix=AionDev' if token.lower().startswith('-saveddirsuffix=') else token
            for token in tokens]


def save(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


def backup(root, target):
    before = snapshot(root)
    if before['errors']:
        raise ValueError('Saved-data audit incomplete; no launch performed')
    for name, metadata in before['files'].items():
        dest = target / name
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(root / name, dest)
        with dest.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != metadata['sha256']:
                raise ValueError('Backup verification failed; no launch performed')
    return before


def collect_logs(run, started):
    sources = {
        'ncguard': Path(os.environ['TEMP']) / 'ncglog',
        'nccr': Path(os.environ['LOCALAPPDATA']) / 'NCSOFT/NccrData/com.ncsoft.aion2global',
        'game': Path(os.environ['LOCALAPPDATA']) / 'AION2/Saved_AionDev/Logs',
    }
    errors = []
    for label, root in sources.items():
        if not root.exists():
            continue
        for path in root.iterdir():
            try:
                if not path.is_file() or path.stat().st_mtime < started:
                    continue
                dest = run / 'logs' / label / path.name
                dest.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(path, dest)
            except OSError as error:
                errors.append({'source': label, 'file': path.name, 'error': str(error)})
    return errors


def prepare_launch():
    """Validate process state, copied image, and cached metadata without launching."""
    if os.name != 'nt':
        raise ValueError('Windows required')
    active = subprocess.run(['powershell.exe', '-NoProfile', '-Command',
        "if (Get-Process AION2 -ErrorAction SilentlyContinue) { exit 2 }"], capture_output=True)
    if active.returncode:
        raise ValueError('AION2 may already be running or process check failed')
    executable = ROOT / 'research/local/dev-client/AION2/Aion2/Binaries/Win64/AION2.exe'
    with executable.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    if digest != EXPECTED_SHA256:
        raise ValueError('Unexpected development executable version')
    entry = read_appinfo(DEFAULT_STEAM / 'appcache/appinfo.vdf', APP_ID)['appinfo']['config']['launch']['0']
    if entry['executable'].replace('\\', '/').strip('/').lower() != 'aion2/binaries/win64/aion2.exe':
        raise ValueError('Cached executable changed')
    arguments = launch_arguments(entry['arguments'])
    return executable, arguments, digest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--launch', action='store_true', help='Actually launch after verified backups')
    parser.add_argument('--duration', type=int, default=120)
    args = parser.parse_args()
    if not 10 <= args.duration <= 300:
        parser.error('Duration must be 10..300 seconds')
    executable, arguments, digest = prepare_launch()
    print(json.dumps({'arguments': argument_summary(' '.join(arguments)), 'launch_requested': args.launch}), flush=True)
    if not args.launch:
        return
    run = ROOT / 'research/local/dev-client' / ('host-cached-' + datetime.now().strftime('%Y%m%d-%H%M%S-') + uuid.uuid4().hex[:8])
    run.mkdir()
    roots = {'aion': Path(os.environ['LOCALAPPDATA']) / 'AION2',
             'nccr': Path(os.environ['LOCALAPPDATA']) / 'NCSOFT/NccrData/com.ncsoft.aion2global'}
    before = {}
    for label, root in roots.items():
        before[label] = backup(root, run / 'backups' / label)
        save(run / (label + '-before.json'), before[label])
    started = time.time()
    state = {'status': 'launching', 'started_utc': datetime.now(timezone.utc).isoformat(),
             'arguments': argument_summary(' '.join(arguments)), 'executable_sha256': digest,
             'limitations': ['Normal network-enabled host launch; suffix is not full isolation.',
                            'Initial-process exit does not establish child-process exit.',
                            'Saved snapshots omit transient writes, other paths, and registry changes.']}
    save(run / 'state.json', state)
    print('Reports: ' + str(run.relative_to(ROOT)), flush=True)
    try:
        process = subprocess.Popen([str(executable), *arguments], cwd=executable.parent,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        state.update(status='observing', initial_pid=process.pid)
        save(run / 'state.json', state)
        deadline = time.monotonic() + args.duration
        while time.monotonic() < deadline:
            time.sleep(min(5, max(0, deadline - time.monotonic())))
            save(run / 'collection-errors.json', collect_logs(run, started))
        state.update(status='observation_finished', initial_exit_code=process.poll())
    finally:
        save(run / 'collection-errors.json', collect_logs(run, started))
        for label, root in roots.items():
            after = snapshot(root)
            after['comparison'] = compare(before[label], after)
            save(run / (label + '-after.json'), after)
        state['finished_utc'] = datetime.now(timezone.utc).isoformat()
        save(run / 'state.json', state)
        print('Observation ended. Game processes are not automatically stopped.', flush=True)


if __name__ == '__main__':
    main()
