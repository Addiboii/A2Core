"""Bounded localhost login capture; sends no replies and keeps payloads local."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import socket
import subprocess
import time

try:
    from .aion_protocol import split_frames, client_hello
except ImportError:
    from aion_protocol import split_frames, client_hello

ROOT = Path(__file__).resolve().parents[1]


def receive(connection, seconds=5, limit=65536):
    data = bytearray()
    deadline = time.monotonic() + seconds
    while len(data) < limit:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return bytes(data), 'deadline'
        connection.settimeout(remaining)
        try:
            chunk = connection.recv(min(8192, limit - len(data)))
        except socket.timeout:
            return bytes(data), 'deadline'
        except OSError:
            return bytes(data), 'socket_error'
        if not chunk:
            return bytes(data), 'peer_closed'
        data.extend(chunk)
    return bytes(data), 'byte_limit'


def summarize(data):
    result = {'received_bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest(),
              'framing_basis': 'candidate from prior passive captures; not assumed compatible'}
    try:
        frames, consumed = split_frames(data)
        result.update(consumed_bytes=consumed, trailing_bytes=len(data)-consumed, frames=[])
        for frame in frames:
            item = {'offset': frame.offset, 'wire_bytes': frame.wire_length,
                    'body_bytes': len(frame.body),
                    'tag_candidate': '0x%04x' % int.from_bytes(frame.body[:2], 'little')}
            if frame.body[:2] in (b'\x00\x39', b'\x10\x36'):
                try:
                    item['client_hello'] = client_hello(frame.body)
                except ValueError as exc:
                    item['hello_layout_error'] = str(exc)
            result['frames'].append(item)
    except ValueError as exc:
        result['framing_error'] = str(exc)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=240)
    args = parser.parse_args()
    output = args.output.resolve()
    if output.parent != (ROOT / 'research/local').resolve() or not 10 <= args.seconds <= 600:
        parser.error('Use a new directory directly under research/local and 10..600 seconds')
    output.mkdir(exist_ok=False)
    report = {'status': 'starting', 'bind': '127.0.0.1:3700', 'connections': [],
              'limitations': ['No protocol replies sent.', 'Raw bytes stay in this local directory.',
                              'TCP reads are reassembled; receive boundaries are not frames.']}
    def save():
        (output / 'report.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    try:
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 3700))
            listener.listen(4)
            listener.settimeout(1)
            report['status'] = 'listening'
            save()
            print('Local login capture ready: ' + str(output.relative_to(ROOT)), flush=True)
            deadline = time.monotonic() + args.seconds
            while time.monotonic() < deadline and len(report['connections']) < 4 and not (output / 'stop').exists():
                try:
                    connection, peer = listener.accept()
                except socket.timeout:
                    continue
                with connection:
                    entry = {'utc': datetime.now(timezone.utc).isoformat(), 'peer_port': peer[1]}
                    command = ('Get-NetTCPConnection -LocalPort %d -RemotePort 3700 -ErrorAction SilentlyContinue '
                               '| Select-Object OwningProcess,State | ConvertTo-Json -Compress') % peer[1]
                    try:
                        proc = subprocess.run(['powershell.exe', '-NoProfile', '-Command', command],
                                              capture_output=True, text=True, timeout=10)
                        entry['socket_snapshot'] = json.loads(proc.stdout) if proc.stdout.strip() else None
                    except (OSError, ValueError, subprocess.TimeoutExpired) as exc:
                        entry['attribution_error'] = type(exc).__name__
                    data, reason = receive(connection)
                    name = 'client-stream-%02d.bin' % len(report['connections'])
                    (output / name).write_bytes(data)
                    entry.update(raw_file=name, receive_end=reason, summary=summarize(data))
                    report['connections'].append(entry)
                    save()
                    print(json.dumps(entry['summary']), flush=True)
    finally:
        report['status'] = 'stopped'
        save()


if __name__ == '__main__':
    main()
