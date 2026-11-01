"""Loopback login server for the original AION2 client.

Speaks the observed port-13700 login protocol on loopback only:

- Read the plaintext 0x3900 client greeting (advertised RSA public key).
- Reply 0x3901 carrying a self-generated 32-byte RC4 session key,
  OAEP-SHA1 encrypted with the connection's public key (parameter 60,
  10 zero trailer bytes, per the solved handshake).
- Decrypt every follow-up client frame body with one continuing RC4
  keystream over body bytes only; the varint length prefixes are plaintext
  and never advance the keystream.
- Reply with plaintext server frames, one per client request opcode, using
  the observed consecutive request/response pairing:
  0x3905 (Login_RQ) -> 0x3906 account, 0x390a (ListMyCharacters_RQ) ->
  0x390b character list, 0x3908 (ListServer_RQ) -> 0x3909 world directory,
  and 0x3903 pings echoing the 0x3902 heartbeat counters.

Constraints: loopback only, never logs key material, never replays official
capture bytes. All account, GUID and world data sent here is locally generated.
"""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import random
import socket
import string
import time
import uuid

try:
    from .aion_protocol import Reader, encode_varint, split_frames, world_list
    from .probe_login_reply import RC4, build_reply, read_greeting, rsa_public_from_hello
except ImportError:
    from aion_protocol import Reader, encode_varint, split_frames, world_list
    from probe_login_reply import RC4, build_reply, read_greeting, rsa_public_from_hello

ROOT = Path(__file__).resolve().parents[1]
HOST = "127.0.0.1"
DEFAULT_PORT = 13700


class LoginServerError(ValueError):
    pass


def local_port():
    """Port the client will connect to, from policy LoginServerIP or 3700.

    The dev client is configured with LoginServerIP=127.0.0.1:3700 via the
    bootstrap fixture's policy custom_data. When a chained run overrides
    --redirect-port, the same port must be selected here.
    """
    override = os.environ.get("AION_LOGIN_PORT")
    if override:
        return int(override)
    return 3700


def frame(body):
    """Plaintext varint length prefix + body, the observed login-port framing."""
    if not isinstance(body, (bytes, bytearray)) or not body:
        raise LoginServerError("Body must be non-empty bytes")
    return encode_varint(len(body) + 4) + bytes(body)


def build_account(account_id, guid, server_id):
    """0x3906 account body; account_id decimal, guid is the 36-char text form."""
    if not account_id.isascii() or not account_id.isdecimal() or not 1 <= len(account_id) <= 16:
        raise LoginServerError("Account ID must be a short decimal string")
    try:
        guid_shape_ok = isinstance(guid, str) and str(uuid.UUID(guid)) == guid.lower()
    except (ValueError, AttributeError):
        guid_shape_ok = False
    if not guid_shape_ok:
        raise LoginServerError("GUID must be the canonical 36-char text form")
    if not 0 <= server_id <= 0xFFFF:
        raise LoginServerError("Server ID outside uint16")
    first = guid.encode("ascii")
    combined = (account_id + ":" + guid).encode("ascii")
    return (b"\x06\x39\x00\x00\x00"
            + bytes([len(first)]) + first
            + b"\x01\x00\x00"
            + bytes([len(combined)]) + combined
            + server_id.to_bytes(2, "little")
            # One packed boolean byte: bit 0 _tutorial_completed_light, bit 1 _tutorial_completed_dark (the official
            # sample had only bit 0, and picking the Dark world then sent the client back for the prologue), then the
            # empty pre-generated character (nickname, race, server).
            + b"\x03\x00\x00\x00\x00"
            + server_id.to_bytes(2, "little")
            + b"\x03\x09\x03")


def build_characters(characters):
    """0x390b character list body; each entry is a local-only dict.

    The canonical a2kit layout, confirmed byte-for-byte against the official
    plaintext capture: a character row is
      u16 server_id | name(u8 len + bytes) | u32 level | u32 exp |
      u32 0 | u32 code | u8 flags | u64 last_played_unix_ms | 8 zero bytes
    with one group-mask byte before every fourth row. The empty list sends
    only the header and the 01 01 trailer. The official rows differ only in
    the character identity fields; everything sent here is locally generated.

    Placeholder/intro characters are marked the same way the official rows
    do: a '$'-prefixed name. a2kit notes that the lobby UI hides those names.
    There is no separate pre_generated wire flag on the login port; the usmap
    MyCharacter struct does not apply to this login wire path. The game-login
    record has to carry the same name before the client can load it.
    """
    if len(characters) > 255:
        raise LoginServerError("Character list must hold 0..255 rows")
    body = bytearray(b"\x0b\x39\x00\x00" + bytes([len(characters)]))
    for index, entry in enumerate(characters):
        server_id = entry.get("server_id", 0)
        name = entry.get("name", "")
        if not 0 <= server_id <= 0xFFFF:
            raise LoginServerError(f"Character {index} server_id outside uint16")
        encoded = name.encode("utf-8")
        if not 1 <= len(encoded) <= 255:
            raise LoginServerError(f"Character {index} name length outside uint8")
        level = entry.get("level", 1)
        exp = entry.get("exp", 0)
        code = entry.get("code", 5)
        flags = entry.get("flags", 1)
        played = entry.get("last_played_unix_ms", 0)
        if not 0 <= level <= 0xFFFFFFFF or not 0 <= exp <= 0xFFFFFFFF \
                or not 0 <= code <= 0xFFFFFFFF or not 0 <= flags <= 0xFF \
                or not 0 <= played <= 0xFFFFFFFFFFFFFFFF:
            raise LoginServerError(f"Character {index} field outside bounds")
        body += server_id.to_bytes(2, "little")
        body += bytes([len(encoded)]) + encoded
        body += level.to_bytes(4, "little")
        body += exp.to_bytes(4, "little")
        body += b"\x00\x00\x00\x00"          # zero u32 between exp and code
        body += code.to_bytes(4, "little")
        body += bytes([flags])
        body += played.to_bytes(8, "little")
        body += b"\x00" * 8
        if index % 4 == 0:
            body += b"\x00"  # group mask byte, all-zero for synthetic rows
    body += b"\x01\x01"
    return bytes(body)


def build_pre_generated_intro(server_id, now_ms=None):
    """One generic intro character: the official '$'-named placeholder.

    Returns a single-row character list suitable for 0x390b. The name is 12
    bytes, `$` plus 11 generated characters, the same width as the `$` name
    in the 0x3615 sample that loaded with no create step. The client hides
    `$` names in the lobby UI. The real character is created later in the
    intro map. The name is locally generated, never replayed from captures.
    """
    if now_ms is None:
        now_ms = int(time.time() * 1000)
    # 12 bytes matches the `$` name in the 0x3615 sample that loaded
    # automatically, with no create step.
    name = "$" + "".join(random.choice(string.ascii_letters + string.digits)
                         for _ in range(11))
    return [{"server_id": server_id, "name": name, "level": 1, "exp": 0,
             "code": 42, "flags": 2, "last_played_unix_ms": now_ms}]


def online_group_mask(row_count, index):
    """Mask byte for the group of up to four rows that starts at index.

    Each row owns two bits. Official directory rows set those bits to 1, and
    a short final group leaves the unused high bits clear: one row is 0x01,
    two rows are 0x05, and a full group is 0x55.
    """
    if index % 4 != 0:
        raise LoginServerError("Group mask is only carried on every fourth row")
    remaining = row_count - index
    if not 1 <= remaining:
        raise LoginServerError("Group mask has no rows")
    mask = 0
    for slot in range(min(4, remaining)):
        mask |= 1 << (2 * slot)
    return mask


def build_world_directory(entries):
    """0x3909 world directory body from locally generated rows."""
    if not 1 <= len(entries) <= 255:
        raise LoginServerError("World directory must hold 1..255 rows")
    body = bytearray(b"\x09\x39\x00\x00" + bytes([len(entries)]))
    for index, entry in enumerate(entries):
        identifier = entry["id"]
        group = entry["group"]
        name = entry["name"]
        if not 0 <= identifier <= 0xFFFF or group not in (1, 2):
            raise LoginServerError(f"World row {index} id/group outside observed bounds")
        encoded = name.encode("utf-8")
        if not 1 <= len(encoded) <= 255:
            raise LoginServerError(f"World row {index} name length outside uint8")
        body += identifier.to_bytes(2, "little") + bytes([group])
        body += identifier.to_bytes(2, "little") + b"\x00\x00"
        body += bytes([len(encoded)]) + encoded
        if index % 4 == 0:
            body += bytes([online_group_mask(len(entries), index)])
        body += bytes([entry.get("status", 0), entry.get("load", 0)]) + b"\x50\x01\x00"
    body += b"\x01\x01"
    return bytes(body)


def build_select_result(world_id):
    """0x390d selection result body: echo the picked world ID."""
    if not 0 <= world_id <= 0xFFFF:
        raise LoginServerError("World ID outside uint16")
    return b"\x0d\x39\x00\x00\x00" + world_id.to_bytes(2, "little")


def build_redirect(world_id, address, port):
    """0x390f handoff body: the destination world server to connect to."""
    if not 0 <= world_id <= 0xFFFF:
        raise LoginServerError("World ID outside uint16")
    encoded = address.encode("ascii")
    if not 7 <= len(encoded) <= 15:
        raise LoginServerError("Destination address length outside observed bounds")
    if not 1 <= port <= 0xFFFF:
        raise LoginServerError("Destination port outside uint16")
    return (b"\x0f\x39\x00\x00"
            + world_id.to_bytes(2, "little")
            + encode_varint(len(encoded)) + encoded
            + port.to_bytes(2, "little"))


def build_ping(client_counter, now_ms=None):
    """0x3903 lobby ping: echo the client counter plus the server clock."""
    if now_ms is None:
        now_ms = int(time.time() * 1000)
    if not 0 <= client_counter <= 0xFFFFFFFFFFFFFFFF:
        raise LoginServerError("Client counter outside uint64")
    return (b"\x03\x39\x00\x00"
            + client_counter.to_bytes(8, "little")
            + now_ms.to_bytes(8, "little"))


def drain_frames(buffer):
    """Consume complete frames from a mutable buffer; retain the partial tail."""
    frames, consumed = split_frames(bytes(buffer))
    del buffer[:consumed]
    return frames


def followup_replies(body, worlds, account_id, guid, server_id, characters=None,
                     redirect_host="127.0.0.1", redirect_port=13328,
                     now_ms=None):
    """Map one decrypted client body to the plaintext replies to send.

    Returns (reply_bodies, sent_directory). Pure and socket-free. The
    request/response opcode pairing follows the official captures:
    0x3905 -> 0x3906, 0x390a -> 0x390b, 0x3908 -> 0x3909, 0x3902 -> 0x3903,
    0x390c -> 0x390d (echo picked world), 0x390e -> 0x390f (redirect).
    """
    if len(body) < 2:
        return [], False
    header = int.from_bytes(body[:2], "little")
    if header == 0x3905:
        return [build_account(account_id, guid, server_id)], False
    if header == 0x390a:
        return [build_characters(characters or [])], False
    if header == 0x3908:
        return [build_world_directory(worlds)], True
    if header == 0x3902 and len(body) >= 10:
        return [build_ping(int.from_bytes(body[2:10], "little"), now_ms)], False
    if header == 0x390c and len(body) >= 4:
        picked = int.from_bytes(body[2:4], "little")
        return [build_select_result(picked)], False
    if header == 0x390e and len(body) >= 4:
        picked = int.from_bytes(body[2:4], "little")
        return [build_redirect(picked, redirect_host, redirect_port)], False
    return [], False


def handle(connection, worlds, account_id, guid=None, server_id=None,
           key=None, save_dir=None, followup_seconds=300, characters=None,
           redirect_host="127.0.0.1", redirect_port=13328, authenticate=None):
    """Run one login session over an already-accepted loopback connection."""
    authenticated = authenticate is None
    if authenticate is not None:
        save_dir = None  # Real credentials must never enter raw capture/key files.
    if guid is None:
        guid = str(uuid.uuid4())
    if server_id is None:
        server_id = worlds[0]["id"]
    if key is None:
        key = os.urandom(32)
    report = {"utc": datetime.now(timezone.utc).isoformat(),
              "peer_port": connection.getpeername()[1]}

    greeting = read_greeting(connection)
    report["greeting_bytes"] = len(greeting)
    try:
        frames, consumed = split_frames(greeting)
        if not frames or consumed != len(greeting):
            raise LoginServerError("Greeting did not form a complete frame")
        body = frames[0].body
        if int.from_bytes(body[:2], "little") != 0x3900:
            raise LoginServerError("First frame is not a 0x3900 greeting")
        modulus, exponent = rsa_public_from_hello(body)
    except (ValueError, IndexError) as error:
        report["greeting_error"] = str(error)
        return report

    report["public_key"] = {"modulus_bits": modulus.bit_length(), "exponent": exponent}
    reply = build_reply(modulus, exponent, key)
    connection.sendall(reply)
    report["reply_bytes"] = len(reply)

    cipher = RC4(key)
    buffer = bytearray()
    raw = bytearray()
    sent_directory = False
    sent_account = False
    decrypted_headers = []
    deadline = time.monotonic() + followup_seconds
    connection.settimeout(1.0)
    while time.monotonic() < deadline:
        try:
            chunk = connection.recv(8192)
        except socket.timeout:
            continue
        except OSError:
            break
        if not chunk:
            break
        raw.extend(chunk)
        buffer.extend(chunk)
        for inbound in drain_frames(buffer):
            plain = cipher.crypt(inbound.body)
            decrypted_headers.append("0x%04x" % int.from_bytes(plain[:2], "little"))
            if authenticate is not None:
                header = int.from_bytes(plain[:2], 'little')
                if header == 0x3905:
                    if authenticated:
                        return {**report, 'authentication': 'replay_rejected'}
                    identity = authenticate(plain)
                    if not identity:
                        return {**report, 'authentication': 'rejected'}
                    account_id, guid = identity['account_id'], identity['guid']
                    # Stored characters in 0x390b: the server list shows a count per
                    # server. They live on the first listed server (the one picked).
                    characters = [{'server_id': worlds[0]['id'], 'name': c['name'],
                                   'level': c['level']} for c in identity.get('characters', [])]
                    authenticated = True
                    report['authentication'] = 'accepted'
                elif not authenticated and header != 0x3902:
                    return {**report, 'authentication': 'required'}
            replies, directory = followup_replies(
                plain, worlds, account_id, guid, server_id, characters=characters,
                redirect_host=redirect_host, redirect_port=redirect_port)
            if directory:
                sent_directory = True
            if any(b[:2] == b"\x06\x39" for b in replies):
                sent_account = True
            for out in replies:
                connection.sendall(frame(out))
    report["decrypted_body_headers"] = decrypted_headers
    report["sent_account"] = sent_account
    report["sent_directory"] = sent_directory
    if save_dir is not None:
        save_dir = Path(save_dir)
        save_dir.mkdir(parents=True, exist_ok=True)
        (save_dir / f"injected-key-{report['peer_port']}.bin").write_bytes(key)
        (save_dir / f"followup-{report['peer_port']}.bin").write_bytes(bytes(raw))
    return report


def serve(worlds, account_id, port=DEFAULT_PORT, key=None, save_dir=None,
          seconds=1800, followup_seconds=300, on_ready=None, characters=None,
          redirect_host="127.0.0.1", redirect_port=13328, authenticate=None):
    with socket.socket() as listener:
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind((HOST, port))
        listener.listen(1)
        listener.settimeout(1.0)
        if on_ready is not None:
            on_ready(listener.getsockname()[1])
        deadline = time.monotonic() + seconds
        reports = []
        while time.monotonic() < deadline and not (save_dir and (Path(save_dir) / "stop").exists()):
            try:
                connection, peer = listener.accept()
            except socket.timeout:
                continue
            if peer[0] != HOST:
                connection.close()
                continue
            with connection:
                report = handle(connection, worlds, account_id,
                                key=key, save_dir=save_dir,
                                followup_seconds=followup_seconds,
                                characters=characters,
                                redirect_host=redirect_host,
                                redirect_port=redirect_port, authenticate=authenticate)
                reports.append(report)
                if save_dir is not None:
                    (Path(save_dir) / "report.json").write_text(
                        json.dumps(reports, indent=2) + "\n", encoding="utf-8")
                print(json.dumps(report), flush=True)
        return reports


# Ids 1–5 and 101–105 are cooked placeholders (Elyos1_FullName, Asmodians1_FullName).
# Europe's named rows run through 1360 / 2360 (1301 is Siel). 1361 and 2361 are
# the first Europe-band ids with no ServerName.dat row, so the client is not
# forced onto those cooked labels. The wire name is what we want shown.
DEFAULT_WORLDS = [
    {"id": 1361, "group": 1, "name": "PotatoeWorld", "status": 1, "load": 50},
    {"id": 2361, "group": 2, "name": "PotatoeWorld", "status": 1, "load": 50},
]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=local_port())
    parser.add_argument("--output", type=Path, default=None)
    parser.add_argument("--seconds", type=int, default=1800)
    parser.add_argument("--followup-seconds", type=int, default=300)
    parser.add_argument("--account-id", default=None)
    parser.add_argument("--characters", type=int, default=0,
                        help="How many local-only characters to list (0 for an empty list)")
    parser.add_argument("--pre-generated-intro", action="store_true",
                        help="Seed one '$'-named generic intro character (0x390b) instead of an empty list")
    parser.add_argument("--redirect-host", default="127.0.0.1")
    parser.add_argument("--redirect-port", type=int, default=13328)
    parser.add_argument("--ready-file", type=Path, default=None,
                        help="Write this file once the listener is bound")
    args = parser.parse_args(argv)
    if args.output is not None:
        local = (ROOT / "research/local").resolve()
        output = args.output.resolve()
        if not output.is_relative_to(local) or output.exists():
            parser.error("Use a new directory directly under research/local/")
        output.mkdir(parents=True, exist_ok=False)
    characters = None
    if args.pre_generated_intro:
        characters = build_pre_generated_intro(DEFAULT_WORLDS[0]["id"])
    elif args.characters > 0:
        characters = [{"server_id": worlds0_id, "name": f"Dev_{i:02d}", "level": 1,
                       "exp": 0, "code": 5, "flags": 1, "last_played_unix_ms": 0}
                      for i, worlds0_id in enumerate(
                          [worlds0["id"] for worlds0 in DEFAULT_WORLDS], 1)]
        characters = characters[:args.characters]
    on_ready = None
    if args.ready_file is not None:
        def on_ready(port):
            args.ready_file.write_text(str(port), encoding="ascii")
    try:
        from .dev_defaults import ACCOUNT_SID
    except ImportError:
        from dev_defaults import ACCOUNT_SID
    serve(DEFAULT_WORLDS, args.account_id or ACCOUNT_SID, port=args.port, save_dir=args.output,
          seconds=args.seconds, followup_seconds=args.followup_seconds,
          characters=characters, redirect_host=args.redirect_host,
          redirect_port=args.redirect_port, on_ready=on_ready)


if __name__ == "__main__":
    main()
