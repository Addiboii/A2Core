"""Differential 0x3901 reply probe for the original client login socket.

Binds loopback:3700 only. Reads the client greeting (0x3900), extracts the
advertised RSA public parameters, builds a candidate plaintext, OAEP-SHA1
encrypts it with the connection's key, and sends a structurally complete 0x3901
reply. It then records follow-up frames and, because the injected key is known,
tries to RC4-decrypt them to confirm the key-transport hypothesis.

Follow-up analysis is deliberately layered:

- Raw frame parse assumes the varint length prefixes are plaintext while bodies
  are encrypted (consistent with the official captures, where follow-up lengths
  decode but bodies do not).
- Whole-stream RC4 treats the entire byte stream as one RC4 ciphertext.
- Body-level RC4 decrypts only each frame body, both as one continuing stream
  and with a fresh RC4 state per frame.

Never logs the modulus, key bytes, or plaintext. No memory is read.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import socket
import time

try:
    from .aion_protocol import Reader, client_hello, der_tlv, encode_varint, split_frames
except ImportError:
    from aion_protocol import Reader, client_hello, der_tlv, encode_varint, split_frames

ROOT = Path(__file__).resolve().parents[1]
HOST = "127.0.0.1"
PORT = 3700


def derive_key(nonce, length):
    """Deterministic per-run key material from a random nonce; never from captures."""
    return hashlib.sha256(b"local-login-probe-key" + nonce).digest()[:length]


def oaep_encode(message, size):
    """OAEP-SHA1 / MGF1-SHA1 / empty label, per OpenSSL RSA_PKCS1_OAEP_PADDING."""
    hlen = 20
    if not 2 <= len(message) <= size - 2 * hlen - 2:
        raise ValueError("Message length outside OAEP-SHA1 capacity")
    seed = os.urandom(hlen)
    lhash = hashlib.sha1(b"").digest()
    db = lhash + bytes(size - 2 * hlen - 2 - len(message)) + b"\x01" + message

    def mgf1(seed, length):
        out = b""
        counter = 0
        while len(out) < length:
            out += hashlib.sha1(seed + counter.to_bytes(4, "big")).digest()
            counter += 1
        return out[:length]

    masked_db = bytes(a ^ b for a, b in zip(db, mgf1(seed, size - hlen - 1)))
    masked_seed = bytes(a ^ b for a, b in zip(seed, mgf1(masked_db, hlen)))
    return b"\x00" + masked_seed + masked_db


def rsa_pub_encrypt(message, modulus, exponent):
    size = (modulus.bit_length() + 7) // 8
    encoded = oaep_encode(message, size)
    if int.from_bytes(encoded, "big") >= modulus:
        encoded = oaep_encode(message, size)  # one retry with a fresh OAEP seed
    cipher = pow(int.from_bytes(encoded, "big"), exponent, modulus)
    return cipher.to_bytes(size, "big")


def build_reply(modulus, exponent, plaintext, parameter=60):
    cipher = rsa_pub_encrypt(plaintext, modulus, exponent)
    body = b"\x01\x39" + b"\x00\x00" + parameter.to_bytes(2, "little") \
        + encode_varint(len(cipher)) + cipher + b"\x00" + b"\x00" \
        + b"\x00\x00\x00\x00" + b"\x00\x00\x00\x00"
    logical = len(body) + 4
    return encode_varint(logical) + body


def read_greeting(sock, seconds=5, limit=65536):
    data = bytearray()
    deadline = time.monotonic() + seconds
    while len(data) < limit:
        try:
            frames, consumed = split_frames(bytes(data))
        except ValueError:
            frames, consumed = [], 0
        if frames and consumed == len(data):
            break  # a complete greeting is buffered; reply without waiting
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        sock.settimeout(remaining)
        try:
            chunk = sock.recv(min(8192, limit - len(data)))
        except socket.timeout:
            break
        except OSError:
            break
        if not chunk:
            break
        data.extend(chunk)
    return bytes(data)


def rsa_public_from_hello(body):
    """Return (modulus, exponent) ints from a login or game greeting body.

    The login port greeting is 0x3900; the game port greeting is 0x3610.
    Both begin with the same u16 tag, varint DER length, DER RSAPublicKey
    layout, so the tag is read but not required to be 0x3900.
    """
    reader = Reader(body)
    tag = reader.uint(2)
    if tag not in (0x3900, 0x3610):
        raise ValueError("not a 0x3900 or 0x3610 greeting")
    size = reader.varuint()
    der = reader.take(size)
    tag, seq, _end = der_tlv(der, 0)
    if tag != 0x30:
        raise ValueError("not a DER sequence")
    values = []
    offset = 0
    while offset < len(seq):
        tag, value, offset = der_tlv(seq, offset)
        if tag != 2:
            raise ValueError("expected DER integer")
        values.append(int.from_bytes(value, "big"))
    if len(values) != 2:
        raise ValueError("expected modulus and exponent")
    return values[0], values[1]


class RC4:
    """RC4 with resumable state, so one instance can span multiple bodies."""

    def __init__(self, key):
        self.state = list(range(256))
        j = 0
        for i in range(256):
            j = (j + self.state[i] + key[i % len(key)]) & 0xFF
            self.state[i], self.state[j] = self.state[j], self.state[i]
        self.i = 0
        self.j = 0

    def crypt(self, data):
        out = bytearray()
        for byte in data:
            self.i = (self.i + 1) & 0xFF
            self.j = (self.j + self.state[self.i]) & 0xFF
            self.state[self.i], self.state[self.j] = self.state[self.j], self.state[self.i]
            out.append(byte ^ self.state[(self.state[self.i] + self.state[self.j]) & 0xFF])
        return bytes(out)


def rc4(data, key):
    return RC4(key).crypt(data)


def _header(body):
    return "0x%04x" % int.from_bytes(body[:2], "little") if len(body) >= 2 else "short"


def summarize_followup(data, key):
    result = {"received_bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    if not data:
        result["followup"] = "none"
        return result
    frames = []
    try:
        frames, consumed = split_frames(data)
        result["raw_frame_parse"] = {"frame_count": len(frames),
                                     "consumed_bytes": consumed,
                                     "unconsumed_bytes": len(data) - consumed,
                                     "wire_lengths": [f.wire_length for f in frames]}
    except ValueError as exc:
        result["raw_frame_parse"] = {"error": str(exc)}
    whole = rc4(data, key)
    result["rc4_whole_decrypt_preview"] = whole[:64].hex()
    try:
        wframes, wconsumed = split_frames(whole)
        result["rc4_whole_parse"] = {
            "frame_count": len(wframes), "consumed_bytes": wconsumed,
            "headers": [_header(f.body) for f in wframes]}
    except ValueError as exc:
        result["rc4_whole_parse"] = {"error": str(exc)}
    if frames:
        stream_headers, per_frame_headers = [], []
        cipher = RC4(key)
        for frame in frames:
            stream_headers.append(_header(cipher.crypt(frame.body)))
            per_frame_headers.append(_header(rc4(frame.body, key)))
        result["body_rc4_stream_headers"] = stream_headers
        result["body_rc4_per_frame_headers"] = per_frame_headers
    return result


def run(output, seconds, key_len, prefix_len, followup_seconds):
    report = {"status": "starting", "bind": f"{HOST}:{PORT}", "connections": [],
              "limitations": ["Loopback only; never sends or logs key material.",
                              "One synthetic 0x3901 per connection; the plaintext is our own.",
                              "RC4 decrypt uses the injected key only.",
                              "Raw follow-up bytes stay under research/local and are gitignored."]}

    def save():
        (output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    deadline = time.monotonic() + seconds
    with socket.socket() as listener:
        listener.bind((HOST, PORT))
        listener.listen(1)
        listener.settimeout(1)
        report["status"] = "listening"
        save()
        print(f"Login reply probe ready: {output.relative_to(ROOT)}", flush=True)
        while time.monotonic() < deadline and not (output / "stop").exists():
            try:
                connection, peer = listener.accept()
            except socket.timeout:
                continue
            with connection:
                entry = {"utc": datetime.now(timezone.utc).isoformat(), "peer_port": peer[1]}
                report["connections"].append(entry)
                save()
                print(f"Accepted connection from port {peer[1]}", flush=True)
                greeting = read_greeting(connection)
                entry["greeting_bytes"] = len(greeting)
                print(f"Greeting read: {len(greeting)} bytes", flush=True)
                try:
                    frames, consumed = split_frames(greeting)
                    if not frames:
                        raise ValueError("no frames in greeting")
                    hello = client_hello(frames[0].body)
                    modulus, exponent = rsa_public_from_hello(frames[0].body)
                    entry["greeting_frames"] = len(frames)
                    entry["greeting_consumed"] = consumed
                    entry["public_key"] = {"modulus_bits": hello.get("modulus_bits"),
                                           "exponent": hello.get("public_exponent")}
                except (ValueError, IndexError) as exc:
                    entry["greeting_parse_error"] = str(exc)
                    save()
                    continue
                nonce = os.urandom(16)
                key = derive_key(nonce, key_len)
                prefix = os.urandom(prefix_len)
                plaintext = prefix + key
                reply = build_reply(modulus, exponent, plaintext)
                # Our own generated key, stored only under gitignored research/local
                # so followup-<port>.bin can be re-decrypted offline.
                (output / f"injected-key-{peer[1]}.bin").write_bytes(key)
                entry["injected"] = {"key_bytes": key_len, "prefix_bytes": prefix_len,
                                     "nonce_sha256": hashlib.sha256(nonce).hexdigest(),
                                     "key_sha256": hashlib.sha256(key).hexdigest()}
                connection.sendall(reply)
                entry["reply_bytes"] = len(reply)
                print(f"0x3901 reply sent: {len(reply)} bytes", flush=True)
                connection.settimeout(1)
                followup = bytearray()
                followup_deadline = time.monotonic() + followup_seconds
                while time.monotonic() < followup_deadline and len(followup) <= 65536 \
                        and not (output / "stop").exists():
                    try:
                        chunk = connection.recv(8192)
                    except socket.timeout:
                        continue
                    except OSError:
                        break
                    if not chunk:
                        break
                    followup.extend(chunk)
                    entry.update(summarize_followup(bytes(followup), key))
                    save()
                    print(f"Follow-up: {len(followup)} bytes total", flush=True)
                entry.update(summarize_followup(bytes(followup), key))
                (output / f"followup-{peer[1]}.bin").write_bytes(bytes(followup))
                save()
                printable = {k: v for k, v in entry.items() if not k.endswith("decrypt_preview")}
                print(json.dumps(printable), flush=True)
    report["status"] = "stopped"
    save()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seconds", type=int, default=1800)
    parser.add_argument("--followup-seconds", type=int, default=45)
    parser.add_argument("--key-len", type=int, default=32, choices=(16, 24, 32))
    parser.add_argument("--prefix-len", type=int, default=0)
    args = parser.parse_args()
    output = args.output.resolve()
    if output.parent != (ROOT / "research/local").resolve() \
            or not 10 <= args.seconds <= 3600 or not 10 <= args.followup_seconds <= 600:
        parser.error("Use a new directory directly under research/local, "
                     "10..3600 listen seconds, and 10..600 follow-up seconds")
    output.mkdir(exist_ok=False)
    run(output, args.seconds, args.key_len, args.prefix_len, args.followup_seconds)


if __name__ == "__main__":
    main()
