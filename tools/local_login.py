"""Localhost login for our own client.

This protocol is separate from the development executable's handshake. A client
sends a display name. The server returns a new session and the recovered
Potatoe World preview. It does not speak to the original client, simulate the
world, or persist accounts.
"""

import argparse
import json
import socket
import uuid
from pathlib import Path


HOST = "127.0.0.1"
PORT = 3701
MAX_FRAME = 4096
MANIFESTS = (
    "sven-region-a-manifest.json",
    "sven-region-b-manifest.json",
)


class LoginError(ValueError):
    pass


def frame(payload):
    if not isinstance(payload, dict):
        raise LoginError("Payload must be an object")
    body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    if not 2 <= len(body) <= MAX_FRAME:
        raise LoginError("Frame size is outside the local limit")
    return len(body).to_bytes(4, "big") + body


def read_frame(sock):
    header = _read_exact(sock, 4)
    if header is None:
        raise LoginError("Connection closed before a frame")
    size = int.from_bytes(header, "big")
    if not 2 <= size <= MAX_FRAME:
        raise LoginError("Frame size is outside the local limit")
    body = _read_exact(sock, size)
    if body is None:
        raise LoginError("Connection closed inside a frame")
    try:
        payload = json.loads(body.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise LoginError("Frame is not a JSON object") from error
    if not isinstance(payload, dict):
        raise LoginError("Frame is not a JSON object")
    return payload


def _read_exact(sock, size):
    parts = []
    remaining = size
    while remaining:
        chunk = sock.recv(remaining)
        if not chunk:
            return None
        parts.append(chunk)
        remaining -= len(chunk)
    return b"".join(parts)


def normalize_name(name):
    if not isinstance(name, str):
        raise LoginError("Name must be text")
    cleaned = " ".join(name.split())
    if not cleaned or len(cleaned) > 32:
        raise LoginError("Name must contain 1 to 32 characters")
    if any(ord(char) < 32 for char in cleaned):
        raise LoginError("Name contains a control character")
    return cleaned


def world_entry(manifest_dir):
    root = Path(manifest_dir)
    totals = {"packages": 0, "placements": 0, "spawns": 0}
    present = []
    missing = []
    for name in MANIFESTS:
        path = root / name
        relative = Path("research/local/world") / name
        if not path.is_file():
            missing.append(relative.as_posix())
            continue
        present.append(relative.as_posix())
        with path.open(encoding="utf-8") as source:
            manifest = json.load(source)
        for key in totals:
            values = manifest.get(key, [])
            if not isinstance(values, list):
                raise LoginError(f"{name} field {key} is not a list")
            totals[key] += len(values)
    return {
        "id": "potatoe-world",
        "name": "Potatoe World",
        "kind": "recovered-preview",
        "packages": totals["packages"],
        "placements": totals["placements"],
        "spawns": totals["spawns"],
        "manifests": present,
        "missing_manifests": missing,
        "simulation": False,
    }


def welcome(name, manifest_dir):
    return {
        "op": "welcome",
        "session_id": str(uuid.uuid4()),
        "display_name": normalize_name(name),
        "worlds": [world_entry(manifest_dir)],
    }


def handle(sock, manifest_dir):
    try:
        request = read_frame(sock)
        if request.get("op") != "login":
            raise LoginError("First request must be login")
        sock.sendall(frame(welcome(request.get("name"), manifest_dir)))
    except LoginError as error:
        try:
            sock.sendall(frame({"op": "error", "reason": str(error)}))
        except OSError:
            pass
        raise


def serve(manifest_dir, port=PORT, on_ready=None):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind((HOST, port))
        server.listen(1)
        if on_ready is not None:
            on_ready(server.getsockname()[1])
        connection, address = server.accept()
        with connection:
            if address[0] != HOST:
                raise LoginError("Peer is not loopback")
            handle(connection, manifest_dir)


def login(port, name):
    with socket.create_connection((HOST, port), timeout=5) as sock:
        sock.sendall(frame({"op": "login", "name": name}))
        return read_frame(sock)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    subcommands = parser.add_subparsers(dest="command", required=True)
    server = subcommands.add_parser("serve")
    server.add_argument("--port", type=int, default=PORT)
    server.add_argument("--manifest-dir", type=Path, default=Path("research/local/world"))
    client = subcommands.add_parser("login")
    client.add_argument("--port", type=int, default=PORT)
    client.add_argument("--name", required=True)
    args = parser.parse_args(argv)
    if args.command == "serve":
        serve(args.manifest_dir, args.port)
        return
    print(json.dumps(login(args.port, args.name), indent=2))


if __name__ == "__main__":
    main()
