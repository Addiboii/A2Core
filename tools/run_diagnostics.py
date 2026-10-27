"""Small durable run records. Never serialize session identities or keys."""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import traceback
try:
    from .protocol_knowledge import summarize_body
except ImportError:
    from protocol_knowledge import summarize_body


def record_exchange(path, request, replies, state, elapsed_ms, error=None):
    metadata = summarize_body(request)
    if metadata.get('kind') == 'telemetry' and error is None:
        return
    event = {'utc': datetime.now(timezone.utc).isoformat(), 'request': metadata,
             'reply_opcodes': [f'0x{int.from_bytes(b[:2], "little"):04x}' for b in replies],
             'elapsed_ms': round(elapsed_ms, 3)}
    if metadata.get('opcode') == '0x3800' and metadata.get('supported_shape'):
        event['cast_accepted'] = state.get('last_cast', {}).get('accepted')
        event['resources'] = {k: state.get('resources', {}).get(k) for k in ('hp', 'mp', 'sp')}
    if error is not None:
        event['error_type'] = type(error).__name__
    with Path(path).open('a', encoding='utf-8') as stream:
        stream.write(json.dumps(event) + '\n')


def record_error(directory, error):
    frames = traceback.extract_tb(error.__traceback__)
    data = {'utc': datetime.now(timezone.utc).isoformat(),
            'type': type(error).__name__, 'message': str(error),
            'stack': [{'file': Path(f.filename).name, 'line': f.lineno, 'function': f.name}
                      for f in frames]}
    Path(directory).mkdir(parents=True, exist_ok=True)
    (Path(directory) / 'error.json').write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')


def write_manifest(directory, root, settings):
    files = ['tools/game_server.py', 'tools/login_server.py', 'tools/starter_casts.py',
             'tools/starter_equipment.py', 'tools/starter_skills.py',
             'tools/data/starter-skills.json', 'tools/protocol_knowledge.py',
             'tools/run_game_chain.py', 'tools/run_diagnostics.py']
    hashes = {name: hashlib.sha256((root / name).read_bytes()).hexdigest()
              for name in files if (root / name).exists()}
    data = {'utc': datetime.now(timezone.utc).isoformat(), 'source_sha256': hashes,
            'settings': settings}
    (directory / 'manifest.json').write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
