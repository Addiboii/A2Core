"""Loopback TLS MITM sink for NCGuard's external HTTPS calls (research only).

Routes the dev client's *blocked* external HTTPS hosts to 127.0.0.1 via a
bounded hosts-file override, answers each TLS handshake with a fresh localhost
CA certificate for that host, and records the plaintext requests the client
actually sends. It never reaches the real NCSoft servers and never returns
"success" unless an explicit --respond fixture is supplied.

Scope rules (fail closed):
  - Only the hosts named in --hosts are rewritten; every line is reverted on
    exit. Windows takes over writing the hosts file, so requires an elevated
    shell. Nothing outside that file is modified.
  - The generated CA private key never leaves the run directory; certificates
    are issued on demand only for the rewritten hosts.
  - Captured request lines/headers/bodies are written to the run directory
    under research/local. Bodies longer than --body-cap bytes are truncated.

By default the sink observes and records; it replies 404 so the client's
on-the-wire behaviour can be compared with the blocked-control runs. An
experimental --respond FIXTURE.json can later supply a candidate 200 body for
one host+path pair after the real request is observed.

Run:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\\run_loopback_tls_mitm.ps1 \\
      -DurationMinutes 10 -Hosts bladnoch.ncsoft.com,lagavulin.ncsoft.com,atp-json.ncsoft.com,sdk.gpa.ncsoft.com,stun.gpa.ncsoft.com,nccr-api.global.nc.com
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import socket
import ssl
import threading
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import NameOID

ROOT = Path(__file__).resolve().parents[1]
HOSTS_PATH = Path(os.environ.get('SystemRoot', r'C:\Windows')) / 'System32/drivers/etc/hosts'

DEFAULT_HOSTS = [
    'bladnoch.ncsoft.com',
    'lagavulin.ncsoft.com',
    'atp-json.ncsoft.com',
    'sdk.gpa.ncsoft.com',
    'stun.gpa.ncsoft.com',
    'nccr-api.global.nc.com',
]


class Recording:
    def __init__(self, directory: Path, body_cap: int):
        self.directory = directory
        self.body_cap = body_cap
        self.lock = threading.Lock()
        self.meta = directory / 'mitm-records.jsonl'
        self.raw = directory / 'request-bodies.bin'
        self.fd = open(self.raw, 'ab')
        self.count = 0
        self.unique = {}

    def add(self, host: str, req_line: str, headers, body: bytes):
        with self.lock:
            at = self.fd.tell()
            body = body[: self.body_cap]
            self.fd.write(body)
            self.fd.flush()
            self.count += 1
            key = (host,
                   req_line.split(' ')[0] if ' ' in req_line else req_line,
                   req_line.split(' ')[1] if ' ' in req_line else '')
            self.unique.setdefault(key, 0)
            self.unique[key] += 1
            record = {
                'n': self.count,
                'host': host,
                'request_line': req_line,
                'method': key[1],
                'path': key[2],
                'headers': headers,
                'body_sha256': hashlib.sha256(body).hexdigest(),
                'body_len': len(body),
                'body_offset': at,
                'utc': datetime.now(timezone.utc).isoformat(),
            }
            with open(self.meta, 'a', encoding='utf-8') as f:
                f.write(json.dumps(record) + '\n')

    def close(self):
        with self.lock:
            self.fd.close()


class HostRewrite:
    def __init__(self, hosts):
        self.hosts = [h.lower() for h in hosts]
        self.backup = HOSTS_PATH.read_bytes() if HOSTS_PATH.exists() else b''

    def apply(self):
        text = self.backup.decode('utf-8', errors='replace')
        for h in self.hosts:
            text += f'\n127.0.0.1 {h} # loopback-tls-mitm\n'
        HOSTS_PATH.write_bytes(text.encode('utf-8'))

    def revert(self):
        HOSTS_PATH.write_bytes(self.backup)


def now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec='seconds')


def log(msg: str, fd=None):
    line = f'[{now()}] {msg}'
    print(line, flush=True)
    if fd is not None:
        fd.write(line + '\n')
        fd.flush()


def build_ca(directory: Path):
    key_path = directory / 'ca.key'
    cert_path = directory / 'ca.pem'
    if key_path.exists() and cert_path.exists():
        key = serialization.load_pem_private_key(key_path.read_bytes(), password=None)
        cert = x509.load_pem_x509_certificate(cert_path.read_bytes())
        return key, cert
    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([
        x509.NameAttribute(NameOID.COMMON_NAME, 'AionResearch loopback MITM CA'),
        x509.NameAttribute(NameOID.ORGANIZATION_NAME, 'local research'),
    ])
    cert = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(datetime.utcnow())
        .not_valid_after(datetime.utcnow() + timedelta(days=30))
        .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
        .sign(key, hashes.SHA256())
    )
    key_path.write_bytes(key.private_bytes(
        serialization.Encoding.PEM,
        serialization.PrivateFormat.TraditionalOpenSSL,
        serialization.NoEncryption(),
    ))
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    return key, cert


def build_leaf(ca_key, ca_cert, host: str, directory: Path):
    cache = directory / 'certs'
    cache.mkdir(exist_ok=True)
    key_path = cache / f'{host}.key'
    cert_path = cache / f'{host}.pem'
    if key_path.exists() and cert_path.exists():
        key = serialization.load_pem_private_key(key_path.read_bytes(), password=None)
        cert = x509.load_pem_x509_certificate(cert_path.read_bytes())
        return key, cert
    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, host)])
    cert = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(ca_cert.subject)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(datetime.utcnow())
        .not_valid_after(datetime.utcnow() + timedelta(days=30))
        .add_extension(x509.SubjectAlternativeName([x509.DNSName(host)]), critical=False)
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .sign(ca_key, hashes.SHA256())
    )
    key_path.write_bytes(key.private_bytes(
        serialization.Encoding.PEM,
        serialization.PrivateFormat.TraditionalOpenSSL,
        serialization.NoEncryption(),
    ))
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    return key, cert


def leaf_context(ca_key, ca_cert, host: str, directory: Path) -> ssl.SSLContext:
    key, cert = build_leaf(ca_key, ca_cert, host, directory)
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.set_alpn_protocols(['http/1.1'])
    cache = directory / 'certs'
    ctx.load_cert_chain(str(cache / f'{host}.pem'), str(cache / f'{host}.key'))
    return ctx


def parse_request(stream: bytes):
    end = stream.find(b'\r\n\r\n')
    if end < 0:
        return None
    head = stream[:end].decode('latin-1')
    lines = head.split('\r\n')
    req_line = lines[0]
    headers = {}
    for line in lines[1:]:
        if ':' in line:
            k, v = line.split(':', 1)
            headers[k.strip().lower()] = v.strip()
    body = stream[end + 4:]
    return req_line, headers, body


def read_http_body(fd, first: bytes, headers) -> bytes:
    body = first
    if 'content-length' in headers:
        want = int(headers['content-length'])
        while len(body) < want:
            chunk = fd.read(want - len(body))
            if not chunk:
                break
            body += chunk
    return body


def handle(conn: socket.socket, contexts: dict, run_dir: Path, rec: Recording, logfd, respond):
    cell = {'host': 'unknown'}
    tls = None
    try:
        def sni_cb(sock, servername, initial_ctx):
            host = (servername or 'unknown').lower()
            cell['host'] = host
            try:
                if host in contexts:
                    sock.context = contexts[host]
            except Exception as exc:
                log(f'SNI callback error for {host}: {exc!r}', logfd)
                raise
            return None
        base_ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        base_ctx.minimum_version = ssl.TLSVersion.TLSv1_2
        base_ctx.set_servername_callback(sni_cb)
        base_ctx.set_alpn_protocols(['http/1.1'])
        base_ctx.load_cert_chain(str(run_dir / 'certs' / 'default.pem'), str(run_dir / 'certs' / 'default.key'))
        tls = base_ctx.wrap_socket(conn, server_side=True)
        cell['alpn'] = tls.selected_alpn_protocol()
    except Exception as exc:
        log(f'{cell["host"]}: TLS handshake failed: {exc!r}', logfd)
        try:
            conn.close()
        except Exception:
            pass
        return
    tls.settimeout(10.0)
    try:
        data = b''
        while b'\r\n\r\n' not in data:
            chunk = tls.recv(65536)
            if not chunk:
                break
            data += chunk
        parsed = parse_request(data)
        if parsed is None:
            log(f'{cell["host"]}: no complete HTTP head received', logfd)
            return
        req_line, headers, body = parsed
        body = read_http_body(tls, body, headers)
        rec.add(cell['host'], req_line, headers, body)
        log(f'{cell["host"]} [alpn={cell.get("alpn")}] -> {req_line} (body {len(body)} B, headers {len(headers)})', logfd)
        status, reason, resp_headers, resp_body = respond_for(cell['host'], req_line, respond)
        out = f'HTTP/1.1 {status} {reason}\r\n'
        resp_headers.setdefault('Content-Length', str(len(resp_body)))
        resp_headers.setdefault('Connection', 'close')
        for k, v in resp_headers.items():
            out += f'{k}: {v}\r\n'
        out += '\r\n'
        tls.sendall(out.encode('latin-1') + resp_body)
    except Exception as exc:
        log(f'{cell["host"]}: connection error {exc!r}', logfd)
    finally:
        try:
            tls.shutdown(socket.SHUT_RDWR)
        except Exception:
            pass
        tls.close()


def respond_for(host: str, req_line: str, respond):
    """Match fixtures; path/method are exact, legacy paths are prefixes.

    Binary bodies use body_base64 so JSON preserves every response byte.
    """
    if respond:
        for rule in respond.get('rules', []):
            if rule.get('host') == host:
                paths = rule.get('paths')
                path = req_line.split(' ')[1] if ' ' in req_line else ''
                if 'path' in rule and path != rule['path']:
                    continue
                if 'method' in rule and req_line.split(' ', 1)[0] != rule['method']:
                    continue
                if paths is None or any(path.startswith(p) for p in paths):
                    if 'body_base64' in rule:
                        body = base64.b64decode(rule['body_base64'], validate=True)
                    else:
                        body = rule.get('body', '')
                    if isinstance(body, str):
                        body = body.encode('utf-8')
                    return (rule.get('status', 200), 'OK',
                            {'Content-Type': rule.get('content_type', 'application/json')}, body)
    return 404, 'Not Found', {'Content-Type': 'text/plain'}, b'loopback-mitm: not answered\n'


def trust_ca(cert_path: Path, directory: Path, logfd) -> str:
    """Trust the CA in the current user's Root store (reversible; no elevation)."""
    import subprocess
    action = 'before'
    try:
        out = subprocess.run(
            ['certutil', '-user', '-addstore', 'Root', str(cert_path)],
            capture_output=True, text=True, timeout=30)
        action = 'after'
        if 'added' in (out.stdout + out.stderr).lower() or out.returncode == 0:
            return 'trusted'
        return f'unexpected (rc={out.returncode})'
    except Exception as exc:
        return f'error: {exc!r}'


def untrust_ca(cert_path: Path) -> str:
    import subprocess
    try:
        out = subprocess.run(
            ['certutil', '-user', '-delstore', 'Root', 'AionResearch loopback MITM CA'],
            capture_output=True, text=True, timeout=30)
        return 'removed' if out.returncode == 0 else f'rc={out.returncode}'
    except Exception as exc:
        return f'error: {exc!r}'



HOST_SET = set()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--duration', type=int, default=600, help='Seconds to run (max 3600)')
    parser.add_argument('--hosts', default=','.join(DEFAULT_HOSTS))
    parser.add_argument('--port', type=int, default=443)
    parser.add_argument('--body-cap', type=int, default=262144)
    parser.add_argument('--respond', type=Path, help='Optional JSON fixture; see respond_for')
    parser.add_argument('--stop-file', type=Path, help='Exit normally and restore hosts when this file appears')
    parser.add_argument('--skip-hosts-file', action='store_true', help='Do not touch the hosts file')
    parser.add_argument('--trust-ca', action='store_true', help='Trust the generated CA in the user Root store (reverted on exit)')
    args = parser.parse_args()
    if not 10 <= args.duration <= 3600:
        parser.error('--duration must be 10..3600 seconds')
    hosts = [h.strip().lower() for h in args.hosts.split(',') if h.strip()]
    run_dir = ROOT / 'research/local' / ('tls-mitm-' + datetime.now().strftime('%Y%m%d-%H%M%S-') + base64.urlsafe_b64encode(os.urandom(4)).decode().rstrip('='))
    run_dir.mkdir(parents=True)
    logfd = open(run_dir / 'mitm.log', 'a', encoding='utf-8')
    rewrite = HostRewrite(hosts)
    ca_key, ca_cert = build_ca(run_dir)
    cert_cache = run_dir / 'certs'
    cert_cache.mkdir(exist_ok=True)
    default_key = ec.generate_private_key(ec.SECP256R1())
    default_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'localhost')])
    default_cert = (
        x509.CertificateBuilder()
        .subject_name(default_name)
        .issuer_name(default_name)
        .public_key(default_key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(datetime.utcnow())
        .not_valid_after(datetime.utcnow() + timedelta(days=30))
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .sign(default_key, hashes.SHA256())
    )
    (cert_cache / 'default.key').write_bytes(default_key.private_bytes(
        serialization.Encoding.PEM,
        serialization.PrivateFormat.TraditionalOpenSSL,
        serialization.NoEncryption()))
    (cert_cache / 'default.pem').write_bytes(default_cert.public_bytes(serialization.Encoding.PEM))
    contexts = {'default': ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)}
    contexts['default'].minimum_version = ssl.TLSVersion.TLSv1_2
    for h in hosts:
        contexts[h] = leaf_context(ca_key, ca_cert, h, run_dir)
    respond = None
    if args.respond:
        respond = json.loads(args.respond.read_text(encoding='utf-8'))
    rec = Recording(run_dir, args.body_cap)
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(('127.0.0.1', args.port))
    listener.listen(64)
    listener.settimeout(0.5)
    state = {
        'status': 'running',
        'started_utc': datetime.now(timezone.utc).isoformat(),
        'port': args.port,
        'hosts': hosts,
        'hosts_file_applied': not args.skip_hosts_file,
        'respond': bool(respond),
        'body_cap': args.body_cap,
    }
    (run_dir / 'state.json').write_text(json.dumps(state, indent=2) + '\n')

    applied = False
    trusted = False
    try:
        if not args.skip_hosts_file:
            rewrite.apply()
            applied = True
            log(f'hosts file rewritten for {len(hosts)} hosts; reverted on exit', logfd)
        if args.trust_ca:
            result = trust_ca(run_dir / 'ca.pem', run_dir, logfd)
            trusted = result == 'trusted'
            state['ca_trust'] = result
            log(f'CA trust attempt: {result}', logfd)
        log(f'TLS sink listening on 127.0.0.1:{args.port}; run dir {run_dir.name}', logfd)
        deadline = time.monotonic() + args.duration
        while time.monotonic() < deadline and not (args.stop_file and args.stop_file.exists()):
            try:
                conn, addr = listener.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            try:
                conn.settimeout(15.0)
                threading.Thread(target=handle, args=(conn, contexts, run_dir, rec, logfd, respond), daemon=True).start()
            except Exception as exc:
                log(f'accept handling error: {exc!r}', logfd)
                try:
                    conn.close()
                except Exception:
                    pass
        state['status'] = 'finished'
        log('stop requested; stopping' if args.stop_file and args.stop_file.exists()
            else 'duration reached; stopping', logfd)
    except KeyboardInterrupt:
        state['status'] = 'finished'
    finally:
        if applied:
            rewrite.revert()
            log('hosts file reverted', logfd)
        if args.trust_ca:
            log(f'CA untrust attempt: {untrust_ca(run_dir / "ca.pem")}', logfd)
        rec.close()
        state['finished_utc'] = datetime.now(timezone.utc).isoformat()
        state['records'] = rec.count
        state['unique'] = {f'{k[0]} {k[1]} {k[2]}': v for k, v in rec.unique.items()}
        (run_dir / 'state.json').write_text(json.dumps(state, indent=2) + '\n')
        logfd.close()
        print(f'Done. {rec.count} request(s) recorded in {run_dir.name}', flush=True)


if __name__ == '__main__':
    main()
