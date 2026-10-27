"""Load test: many simulated players on a development-login game port.

Each bot does the development login (greeting, 0x3613, name check, creation, selection, world entry), then walks
around the start point at running speed sending position reports (0x3700 start, 0x3701 updates), and measures
round trips: a ping (0x3602 -> 0x3603, map work on the server) and a legion info request (0x8a1c -> 0x8a1d, world
work). Bots are spread over worker processes (asyncio inside each); the server's world-lock totals are read from
the loopback /internal/world endpoint before and after when --stats-url is given.

Start a development host first, for example (PowerShell):
  $env:Aion__Native__Logins='development'; $env:Aion__Native__GamePort='23328'; $env:Aion__Native__MaxConnections='500'
  $env:Aion__Native__DevelopmentStartMap='1110'; dotnet server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll
then:  python tools/load_test.py --port 23328 --bots 100 --seconds 60 --stats-url http://127.0.0.1:8091/internal/world
"""
import argparse
import asyncio
import json
import math
import multiprocessing
import random
import struct
import time
import urllib.request
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools.aion_protocol import encode_varint, split_frames  # noqa: E402
from tools.probe_login_reply import RC4  # noqa: E402

SESSION = '1000001:0b4f6a3c-1d2e-4f50-8a9b-0c1d2e3f4a5b'


def _probable_prime(bits, rng):
    while True:
        candidate = rng.getrandbits(bits) | (3 << (bits - 2)) | 1
        if all(candidate % p for p in (3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37)) and _miller_rabin(candidate, rng):
            return candidate


def _miller_rabin(n, rng, rounds=20):
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


def keypair(seed):
    rng, e = random.Random(seed), 65537
    while True:
        p, q = _probable_prime(512, rng), _probable_prime(512, rng)
        phi = (p - 1) * (q - 1)
        if p != q and phi % e:
            return p * q, e, pow(e, -1, phi)


def _der(n, e):
    def tlv(tag, value):
        size = len(value)
        head = bytes([size]) if size < 128 else (b'\x81' + bytes([size]) if size < 256 else b'\x82' + size.to_bytes(2, 'big'))
        return bytes([tag]) + head + value

    def integer(v):
        return tlv(2, v.to_bytes((v.bit_length() + 8) // 8, 'big'))
    return tlv(0x30, integer(n) + integer(e))


def _oaep_decode(block, size):
    import hashlib

    def mgf1(seed, length):
        out, counter = b'', 0
        while len(out) < length:
            out += hashlib.sha1(seed + counter.to_bytes(4, 'big')).digest()
            counter += 1
        return out[:length]
    masked_seed, masked_db = block[1:21], block[21:]
    seed = bytes(a ^ b for a, b in zip(masked_seed, mgf1(masked_db, 20)))
    db = bytes(a ^ b for a, b in zip(masked_db, mgf1(seed, size - 21)))
    return db[db.index(b'\x01', 20) + 1:]


def _strings(opcode, *values):
    out = struct.pack('<H', opcode)
    for value in values:
        raw = value.encode()
        out += bytes([len(raw)]) + raw
    return out


class Bot:
    def __init__(self, index, args, keys, results):
        self.index, self.args, self.keys, self.results = index, args, keys, results
        self.buffer = b''
        self.waiters = {}      # opcode -> future of the next body with it
        self.pings = {}        # counter -> send time
        self.guild_sent = None
        self.received = 0

    async def run(self):
        try:
            await self._run()
        except (OSError, asyncio.IncompleteReadError, asyncio.TimeoutError, ValueError) as error:
            self.results['errors'].append(f'{self.index}: {type(error).__name__} {error}')

    def send(self, body):
        self.writer.write(encode_varint(len(body) + 4) + self.rc4.crypt(body))

    async def expect(self, opcode, seconds=15):
        future = asyncio.get_running_loop().create_future()
        self.waiters.setdefault(opcode, []).append(future)
        return await asyncio.wait_for(future, seconds)

    async def _reader(self):
        while True:
            chunk = await self.reader.read(65536)
            if not chunk:
                return
            self.received += len(chunk)
            self.buffer += chunk
            frames, consumed = split_frames(self.buffer)
            self.buffer = self.buffer[consumed:]
            now = time.perf_counter()
            for f in frames:
                body = f.body
                op = body[0] | body[1] << 8 if len(body) >= 2 else -1
                if op == 0x3603 and len(body) >= 12:
                    sent = self.pings.pop(struct.unpack_from('<Q', body, 4)[0], None)
                    if sent is not None and self.in_world:
                        self.results['ping'].append((now - sent) * 1000)
                elif op == 0x8a1d and self.guild_sent is not None:
                    if self.in_world:
                        self.results['world'].append((now - self.guild_sent) * 1000)
                    self.guild_sent = None
                if op in self.waiters and self.waiters[op]:
                    future = self.waiters[op].pop(0)
                    if not future.done():
                        future.set_result(body)

    async def _run(self):
        a = self.args
        self.in_world = False
        self.reader, self.writer = await asyncio.wait_for(asyncio.open_connection(a.host, a.port), 15)
        n, e, d = self.keys
        key_der = _der(n, e)
        greeting = struct.pack('<H', 0x3610) + encode_varint(len(key_der)) + key_der + bytes(11)
        self.writer.write(encode_varint(len(greeting) + 4) + greeting)
        while True:
            frames, consumed = split_frames(self.buffer)
            if frames:
                self.buffer = self.buffer[consumed:]
                reply = frames[0].body
                break
            chunk = await asyncio.wait_for(self.reader.read(65536), 15)
            if not chunk:
                raise OSError('closed during the greeting')
            self.buffer += chunk
        offset, size = 8, reply[8]
        if size & 0x80:
            size, offset = (size & 0x7f) | (reply[9] << 7), 9
        cipher = reply[offset + 1:offset + 1 + size]
        self.rc4 = RC4(_oaep_decode(pow(int.from_bytes(cipher, 'big'), d, n).to_bytes(128, 'big'), 128))
        reading = asyncio.create_task(self._reader())
        try:
            name = f'Bot{self.index:04d}'
            login = self.expect(0xffff)
            self.send(_strings(0x3613, SESSION, '', 'local-dev', '') + bytes(9))
            await login
            checked = self.expect(0x361a)
            self.send(_strings(0x3619, name) + b'\x01')
            await checked
            created = self.expect(0x361c)
            self.send(_strings(0x361b, name) + b'\x01' + struct.pack('<I', 1) + b'\x02\x0c\x64' + b'\x00\x00\x00')
            await created
            selected = self.expect(0x3621)
            self.send(b'\x1f\x36' + encode_varint(65536) + bytes(16))
            prepared = await selected
            # PrepareEnterMap 0x3621: i32 ticket, i32 map, i64 game time, then the position (3 x f32).
            x, y, z = struct.unpack_from("<fff", prepared, 18)
            self.send(b'\x22\x36' + struct.pack('<i', 1))
            await asyncio.sleep(2)   # the world-entry burst
            self.in_world = True
            self.results['entered'] += 1
            await self._play(x, y, z)
        finally:
            reading.cancel()
            self.writer.close()

    async def _play(self, x, y, z):
        a = self.args
        rng = random.Random(self.index)
        home = (x, y)
        heading = rng.uniform(0, 360)
        step = 1.0 / a.rate
        speed = a.speed
        deadline = time.perf_counter() + a.seconds
        next_ping = next_world = time.perf_counter() + rng.uniform(0, 1)
        counter = self.index * 1_000_000

        def report(opcode):
            yaw = int(heading / 360 * 65536) & 0xffff
            return struct.pack('<HBfffHf', opcode, 0, x, y, z, yaw, heading)
        self.send(report(0x3700))
        while time.perf_counter() < deadline:
            await asyncio.sleep(step)
            # Walk; turn back towards the start point past the spread, and wander a little.
            if math.hypot(x - home[0], y - home[1]) > a.spread:
                heading = math.degrees(math.atan2(home[1] - y, home[0] - x)) % 360
            heading = (heading + rng.uniform(-20, 20)) % 360
            x += math.cos(math.radians(heading)) * speed * step
            y += math.sin(math.radians(heading)) * speed * step
            self.send(report(0x3701))
            now = time.perf_counter()
            if now >= next_ping:
                counter += 1
                self.pings[counter] = now
                self.send(b'\x02\x36' + struct.pack('<Q', counter))
                next_ping = now + 1
            if now >= next_world and self.guild_sent is None:
                self.guild_sent = now
                self.send(b'\x1c\x8a')
                next_world = now + 2
            await self.writer.drain()
        self.results['received'] += self.received


def _worker(indexes, args, keys, queue, start_at):
    async def main():
        results = {'ping': [], 'world': [], 'errors': [], 'entered': 0, 'received': 0}
        bots = []
        for i in indexes:
            delay = start_at + i * args.ramp - time.time()
            if delay > 0:
                await asyncio.sleep(delay)
            bots.append(asyncio.create_task(Bot(i, args, keys, results).run()))
        await asyncio.gather(*bots)
        return results
    queue.put(asyncio.run(main()))


def _percentile(values, p):
    if not values:
        return float('nan')
    values = sorted(values)
    return values[min(len(values) - 1, int(len(values) * p))]


def _stats(url):
    if not url:
        return None
    try:
        with urllib.request.urlopen(url, timeout=5) as response:
            return json.load(response)
    except OSError:
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('--host', default='127.0.0.1')
    parser.add_argument('--port', type=int, default=13328)
    parser.add_argument('--bots', type=int, default=20)
    parser.add_argument('--seconds', type=float, default=60, help='play time per bot after world entry')
    parser.add_argument('--rate', type=float, default=4, help='position reports per second per bot')
    parser.add_argument('--speed', type=float, default=500, help='walking speed, cm/s (the server allows ~800)')
    parser.add_argument('--spread', type=float, default=4000, help='bots turn back this far from the start point (cm)')
    parser.add_argument('--ramp', type=float, default=0.05, help='seconds between bot logins')
    parser.add_argument('--processes', type=int, default=max(1, min(8, multiprocessing.cpu_count() // 2)))
    parser.add_argument('--stats-url', default='', help='the host\'s loopback /internal/world (chat port)')
    args = parser.parse_args()

    keys = keypair(20261016)
    before = _stats(args.stats_url)
    queue = multiprocessing.Queue()
    start_at = time.time() + 0.5
    groups = [list(range(i, args.bots, args.processes)) for i in range(args.processes)]
    workers = [multiprocessing.Process(target=_worker, args=(g, args, keys, queue, start_at)) for g in groups if g]
    for w in workers:
        w.start()
    parts = [queue.get() for _ in workers]
    for w in workers:
        w.join()
    after = _stats(args.stats_url)

    ping = [v for p in parts for v in p['ping']]
    world = [v for p in parts for v in p['world']]
    errors = [v for p in parts for v in p['errors']]
    entered = sum(p['entered'] for p in parts)
    received = sum(p['received'] for p in parts)
    print(f'{entered}/{args.bots} bots entered the world; {len(errors)} errors')
    for e in errors[:5]:
        print('  ', e)
    for label, values in (('ping (map work)', ping), ('legion info (world work)', world)):
        print(f'{label:26s} n={len(values):6d}  p50={_percentile(values, 0.5):7.1f} ms  p95={_percentile(values, 0.95):7.1f} ms  '
              f'p99={_percentile(values, 0.99):7.1f} ms  max={max(values) if values else float("nan"):7.1f} ms')
    print(f'received {received / 1e6:.1f} MB from the server ({received / 1e3 / max(1, args.seconds) / max(1, entered):.1f} kB/s per bot)')
    if before and after:
        b, a = before['locks'], after['locks']
        seconds = a['seconds'] - b['seconds']
        cpu = (after['cpuSeconds'] - before['cpuSeconds']) / seconds
        ex_turns, sh_turns = a['exclusiveTurns'] - b['exclusiveTurns'], a['sharedTurns'] - b['sharedTurns']
        print(f'server: CPU {cpu:.0%} of one core; world turn busy {(a["exclusiveHoldMs"] - b["exclusiveHoldMs"]) / 10 / seconds:.1f}% '
              f'({ex_turns} turns, avg wait {(a["exclusiveWaitMs"] - b["exclusiveWaitMs"]) / max(1, ex_turns):.2f} ms); '
              f'map turns busy {(a["sharedHoldMs"] - b["sharedHoldMs"]) / 10 / seconds:.1f}% '
              f'({sh_turns} turns, avg wait {(a["sharedWaitMs"] - b["sharedWaitMs"]) / max(1, sh_turns):.2f} ms)')


if __name__ == '__main__':
    main()
