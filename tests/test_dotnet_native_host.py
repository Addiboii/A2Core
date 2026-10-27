"""End-to-end check of the .NET host's native login/game ports (the port of login_server.py
and game_server.py), in development logins.

A local client with its own RSA key greets each port, decrypts the RC4 session key from the
OAEP reply, and walks the development flow: login, world list, redirect, game login, name
check, creation, selection and world entry. Skipped when the host is not built
(dotnet build server/Aion.Host/Aion.Host.csproj).
"""
import hashlib
import os
import random
import shutil
import socket
import struct
import subprocess
import time
import unittest
from pathlib import Path

from tools.aion_protocol import encode_varint, lz4_block, split_frames
from tools.probe_login_reply import RC4

ROOT = Path(__file__).resolve().parents[1]
HOST_DLL = ROOT / 'server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll'


def free_port():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]


def probable_prime(bits, rng):
    while True:
        candidate = rng.getrandbits(bits) | (3 << (bits - 2)) | 1
        if all(candidate % p for p in (3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37)) and miller_rabin(candidate, rng):
            return candidate


def miller_rabin(n, rng, rounds=20):
    d, s = n - 1, 0
    while d % 2 == 0:
        d, s = d // 2, s + 1
    for _ in range(rounds):
        x = pow(rng.randrange(2, n - 2), d, n)
        if x in (1, n - 1):
            continue
        for _ in range(s - 1):
            x = pow(x, 2, n)
            if x == n - 1:
                break
        else:
            return False
    return True


def keypair(rng):
    e = 65537
    while True:
        p, q = probable_prime(512, rng), probable_prime(512, rng)
        phi = (p - 1) * (q - 1)
        if p != q and phi % e:
            return p * q, e, pow(e, -1, phi)


def oaep_decode(block, size):
    def mgf1(seed, length):
        out, counter = b'', 0
        while len(out) < length:
            out += hashlib.sha1(seed + counter.to_bytes(4, 'big')).digest()
            counter += 1
        return out[:length]
    masked_seed, masked_db = block[1:21], block[21:]
    seed = bytes(a ^ b for a, b in zip(masked_seed, mgf1(masked_db, 20)))
    db = bytes(a ^ b for a, b in zip(masked_db, mgf1(seed, size - 21)))
    assert block[0] == 0 and db[:20] == hashlib.sha1(b'').digest()
    return db[db.index(b'\x01', 20) + 1:]


def der(n, e):
    def integer(v):
        raw = v.to_bytes((v.bit_length() + 8) // 8, 'big')
        return tlv(2, raw)

    def tlv(tag, value):
        size = len(value)
        head = bytes([size]) if size < 128 else (b'\x81' + bytes([size]) if size < 256 else b'\x82' + size.to_bytes(2, 'big'))
        return bytes([tag]) + head + value
    return tlv(0x30, integer(n) + integer(e))


class Client:
    def __init__(self, port, tag, keys):
        self.n, self.e, self.d = keys
        self.sock = socket.create_connection(('127.0.0.1', port), timeout=10)
        key_der = der(self.n, self.e)
        body = struct.pack('<H', tag) + encode_varint(len(key_der)) + key_der + bytes(11)
        self.sock.sendall(encode_varint(len(body) + 4) + body)
        self.buffer = b''
        reply = self.frames(1)[0]
        self.reply = reply
        offset = 6 if tag == 0x3900 else 8
        size = reply[offset]
        if size & 0x80:  # varint length of a 128-byte cipher
            size, offset = (size & 0x7f) | (reply[offset + 1] << 7), offset + 1
        cipher = reply[offset + 1:offset + 1 + size]
        plain = pow(int.from_bytes(cipher, 'big'), self.d, self.n).to_bytes(128, 'big')
        self.rc4 = RC4(oaep_decode(plain, 128))

    def frames(self, count, seconds=10):
        deadline = time.monotonic() + seconds
        out = []
        while len(out) < count and time.monotonic() < deadline:
            frames, consumed = split_frames(self.buffer)
            if frames:
                self.buffer = self.buffer[consumed:]
                out += [f.body for f in frames if f.body[:2] != b'\x00\x36']  # skip 0x3600 clocks
                continue
            self.sock.settimeout(max(0.01, deadline - time.monotonic()))
            chunk = self.sock.recv(65536)
            if not chunk:
                break
            self.buffer += chunk
        return out

    def send(self, body):
        self.sock.sendall(encode_varint(len(body) + 4) + self.rc4.crypt(body))

    def close(self):
        self.sock.close()


def strings(opcode, *values):
    out = struct.pack('<H', opcode)
    for value in values:
        raw = value.encode()
        out += bytes([len(raw)]) + raw
    return out


@unittest.skipUnless(HOST_DLL.exists() and shutil.which('dotnet'), 'Aion.Host is not built')
class DotnetNativeHostTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.keys = keypair(random.Random(20261014))

    def setUp(self):
        self.login_port, self.game_port = free_port(), free_port()
        env = dict(os.environ, Aion__LocalPort=str(free_port()), Aion__Native__LoginPort=str(self.login_port),
                   Aion__Native__GamePort=str(self.game_port), Aion__Native__Logins='development')
        env.pop('ConnectionStrings__Aion', None)
        env.pop('ConnectionStrings__Auth', None)
        self.process = subprocess.Popen(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env,
                                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            try:
                socket.create_connection(('127.0.0.1', self.game_port), timeout=0.5).close()
                return
            except OSError:
                time.sleep(0.2)
        self.fail('host did not open the native ports')

    def tearDown(self):
        self.process.terminate()
        self.process.wait(timeout=10)

    def test_login_port_lists_worlds_and_redirects(self):
        client = Client(self.login_port, 0x3900, self.keys)
        try:
            self.assertEqual(client.reply[:6], b'\x01\x39\x00\x00\x3c\x00')
            client.send(strings(0x3905, '', 'local-dev', '', '', '', ''))
            account = client.frames(1)[0]
            self.assertEqual(account[:2], b'\x06\x39')
            self.assertIn(b'1000001:', account)
            client.send(b'\x0a\x39')
            self.assertEqual(client.frames(1)[0], b'\x0b\x39\x00\x00\x00\x01\x01')
            client.send(b'\x08\x39')
            self.assertIn(b'PotatoeWorld', client.frames(1)[0])
            client.send(b'\x02\x39' + struct.pack('<Q', 77))
            self.assertEqual(client.frames(1)[0][4:12], struct.pack('<Q', 77))
            client.send(b'\x0e\x39' + struct.pack('<H', 1361))
            redirect = client.frames(1)[0]
            self.assertEqual(redirect[-2:], struct.pack('<H', self.game_port))
        finally:
            client.close()

    def test_game_port_development_world_entry(self):
        client = Client(self.game_port, 0x3610, self.keys)
        try:
            self.assertEqual(client.reply[:8], b'\x11\x36\x00\x00\x60\xea\x00\x00')
            session = '1000001:0b4f6a3c-1d2e-4f50-8a9b-0c1d2e3f4a5b'
            client.send(strings(0x3613, session, '', 'local-dev', '') + bytes(9))
            envelope = client.frames(1)[0]
            self.assertEqual(envelope[:2], b'\xff\xff')
            inner = lz4_block(envelope[6:], int.from_bytes(envelope[2:6], 'little'))
            login = split_frames(inner)[0][0].body
            self.assertEqual(login[:4], b'\x15\x36\x00\x00')
            # 768 + 44 bytes overlaps the server-key length at 811 (the official 6-digit account
            # fits exactly); the Python builder behaves the same.
            self.assertIn(session.encode()[:43] + b'\x041204\x045201', login)
            client.send(strings(0x3619, 'Tester') + b'\x01')
            self.assertEqual(client.frames(1)[0], b'\x1a\x36\x00\x00\x06Tester\x01')
            client.send(strings(0x361b, 'Tester') + b'\x01' + struct.pack('<I', 1) + b'\x02\x0c\x64' + b'\x00\x00\x00')
            self.assertEqual(client.frames(1)[0][:2], b'\x1c\x36')
            client.send(b'\x1f\x36' + encode_varint(65536) + bytes(16))
            selected = client.frames(3)
            self.assertEqual([b[:2] for b in selected], [b'\x20\x36', b'\x44\x8d', b'\x21\x36'])
            client.send(b'\x22\x36' + struct.pack('<i', 1))
            burst = client.frames(8)
            # The class passives (0x382a, weapon equip permission) follow the appearance, as at the official world entry.
            self.assertEqual([b[:2] for b in burst[:7]], [b'\x23\x36', b'\x46\x37', b'\x00\x8d', b'\x33\x36', b'\x46\x36', b'\x2a\x38', b'\x4a\x36'])
            self.assertIn(b'Tester', burst[3])
            client.send(b'\x1c\x8a')
            seen = []
            deadline = time.monotonic() + 10
            while len(seen) < 2 and time.monotonic() < deadline:  # past the NPC appear burst
                seen += [b[:2] for b in client.frames(1, 1) if b[:2] in (b'\x1d\x8a', b'\x58\xe2')]
            self.assertEqual(seen, [b'\x1d\x8a', b'\x58\xe2'])
        finally:
            client.close()

    def test_wrong_greeting_is_dropped(self):
        with socket.create_connection(('127.0.0.1', self.game_port), timeout=10) as sock:
            body = b'\x00\x39' + bytes(8)
            sock.sendall(encode_varint(len(body) + 4) + body)
            self.assertEqual(sock.recv(64), b'')


if __name__ == '__main__':
    unittest.main()
