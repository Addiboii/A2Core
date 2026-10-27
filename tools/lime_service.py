"""Our own Lime-compatible chat backend for the dev client (HTTP + STOMP over WebSocket).

The client's Lime SDK reads its host from policy custom_data ChatServerURL and calls
POST {host}/gameLogin/loginWithToken, then /gameClient/... (research/chat-and-gm.md).
Shapes follow the reflected SDK types (research/local/lime-api-schema-20261014.txt):
responses are BaseResponse {error, module, line, text, defined} plus fields, camelCase.
SubscriptionInfo points the SDK at our STOMP endpoint (ws://host/stomp); sent messages
are fanned out as NotificationReceiveMessage bodies to every subscribed session.
First iteration: field values are local placeholders; every request is logged so the
client's next step can be followed. Standard library only, loopback only.
"""
import base64
import hashlib
import json
import socket
import struct
import threading
import time
import uuid
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

WS_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'
OK = {'error': 0, 'module': 0, 'line': 0, 'text': '', 'defined': ''}


def now_iso():
    return datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.%f')[:-3] + 'Z'


def room_info(key, member_count=1):
    """GameRoomInfo for a GameRoomKeyInfo dict {type, serverKey, roomKey}."""
    return {'gameRoomKeyInfo': key, 'gameCode': 'AION2', 'name': key.get('roomKey', ''),
            'memberCount': member_count, 'voiceMemberCount': 0, 'maxMemberCount': 10000,
            'lastMessageContent': '', 'dateLastMessagePublished': '', 'lastMessageGuid': '',
            'dateCreated': now_iso(), 'deleted': False}


def stomp_frame(command, headers=None, body=''):
    lines = [command] + [f'{k}:{v}' for k, v in (headers or {}).items()]
    return ('\n'.join(lines) + '\n\n' + body + '\0').encode('utf-8')


def parse_stomp(data):
    """Parse one STOMP frame (bytes without the trailing NUL)."""
    text = data.decode('utf-8', 'replace').lstrip('\r\n')
    head, _, body = text.partition('\n\n')
    lines = head.split('\n')
    headers = {}
    for line in lines[1:]:
        if ':' in line:
            key, _, value = line.partition(':')
            headers.setdefault(key, value)
    return lines[0].strip(), headers, body


def ws_frame(payload, opcode=1):
    header = bytes([0x80 | opcode])
    size = len(payload)
    if size < 126:
        header += bytes([size])
    elif size < 65536:
        header += bytes([126]) + struct.pack('>H', size)
    else:
        header += bytes([127]) + struct.pack('>Q', size)
    return header + payload


class LimeService:
    """Sessions, subscriptions and message fan-out shared by HTTP and STOMP."""

    def __init__(self, host='127.0.0.1', port=8091, log_path=None):
        self.host, self.port = host, port
        self.log_path = Path(log_path) if log_path else None
        self.lock = threading.Lock()
        self.users = {}          # mtalk token -> login request fields
        self.subscribers = []    # (send callable, destination, subscription id, session)
        self.seq = 0

    def log(self, entry):
        if self.log_path:
            entry = {'utc': now_iso(), **entry}
            with self.lock, self.log_path.open('a', encoding='utf-8') as handle:
                handle.write(json.dumps(entry) + '\n')

    # --- HTTP API -----------------------------------------------------------------
    def subscription(self, login):
        character = login.get('characterId', '')
        server = login.get('serverKey', '')
        return {'topicName': f'/topic/user.{server}.{character}',
                'subscribeUrl': f'ws://{self.host}:{self.port}/stomp',
                'login': character or 'local', 'passcode': 'local',
                'worldTopic': f'/topic/world.{server}', 'serverAppDest': '/app/chat'}

    def handle(self, path, body, headers):
        route = path.rstrip('/').rsplit('/', 1)[-1]
        if route in ('loginWithToken', 'loginWithGameAccessToken', 'getSubscriptionInfo'):
            login = dict(body)
            if route == 'getSubscriptionInfo':
                login = self.users.get(headers.get('Authorization', ''), login)
            token = 'mtalk-' + uuid.uuid4().hex
            with self.lock:
                self.users[token] = login
            return {**OK, 'mtalkToken': token, 'characterId': login.get('characterId', ''),
                    'subscriptionInfo': self.subscription(login), 'gameRoomInfoList': []}
        if route in ('joinRoom', 'createRoom', 'getRoom', 'updateRoom', 'leaveRoom', 'deleteRoom'):
            return {**OK, 'gameRoomInfo': room_info(body.get('gameRoomKeyInfo') or {})}
        if route == 'sendMessage':
            guid = uuid.uuid4().hex
            self.publish(body, headers, guid)
            return {**OK, 'guid': guid, 'restrictInfo': {'restrictStep': 0, 'startTime': '',
                                                          'expirationTime': '', 'blockedType': '',
                                                          'reason': ''}}
        if route in ('getJoinedRoomList',):
            return {**OK, 'gameRoomInfoList': []}
        if route in ('getJoinedRoomListWithRoomUsers',):
            return {**OK, 'roomWithRoomUserInfoList': []}
        if route in ('getRoomUsers',):
            return {**OK, 'gameRoomKeyInfo': body.get('gameRoomKeyInfo') or {},
                    'gameRoomUserInfoList': [], 'count': 0, 'cursor': ''}
        if route in ('getMessagesForward', 'getMessagesBackward'):
            return {**OK, 'messages': []}
        return dict(OK)

    # --- fan-out -------------------------------------------------------------------
    def publish(self, request, headers, guid):
        sender = self.users_by_auth(headers)
        message = request.get('gameMessageInfo') or {}
        key = request.get('gameRoomKeyInfo') or {}
        with self.lock:
            self.seq += 1
            seq = self.seq
            targets = list(self.subscribers)
        notification = {
            'guid': guid, 'dateCreated': now_iso(), 'seq': seq,
            'roomId': key.get('roomKey', ''), 'gameRoomKeyInfo': key,
            'type': message.get('type') or 'PUBLISH', 'subType': message.get('subType') or 'NORMAL',
            'attribute': message.get('attribute', ''), 'content': message.get('content', ''),
            'initialContent': message.get('content', ''), 'optional': message.get('optional', ''),
            'hasReplacedContent': False, 'replacedMessageInfoList': [], 'isFromGame': True,
            'gameCode': 'AION2', 'gameUserId': sender.get('characterId', ''),
            'senderGameCode': 'AION2', 'serverId': sender.get('serverKey', ''),
            'playNcCharId': sender.get('characterId', ''),
            'userName': message.get('userName', ''), 'role': 'MEMBER', 'locale': sender.get('locale', ''),
            'receiverCharacterId': message.get('receiverCharacterId', ''),
            'receiverUserName': message.get('receiverUserName', ''),
            'receiverServerId': message.get('receiverServerId', ''), 'userMetaData': {}}
        # OnReceiveStompMessage (0x7cde300) reads {type, tid, method, jsonData}: type
        # RESPONSE/ERROR -> RPC reply, MESSAGE/EVENT -> event, NOTI -> notification whose
        # jsonData.type (PUBLISH, NOTICE, ENTER, ...) picks the class; PUBLISH = message.
        # The NOTI/MESSAGE handler (0x7ce0020) requires method "GAME" (an empty method is
        # skipped earlier, 0x7cde948), then routes on jsonData.type / subType.
        body = json.dumps({'type': 'NOTI', 'tid': guid, 'method': 'GAME',
                           'jsonData': notification})
        for send, destination, sub_id, _ in targets:
            send(stomp_frame('MESSAGE', {'destination': destination, 'subscription': sub_id,
                                         'message-id': guid, 'content-type': 'application/json',
                                         'content-length': len(body.encode('utf-8'))}, body))
        self.log({'kind': 'publish', 'targets': len(targets), 'notification': notification})

    def users_by_auth(self, headers):
        token = headers.get('Mtalk-Token') or headers.get('Authorization', '')
        if token in self.users:
            return self.users[token]
        return next(iter(self.users.values()), {}) if self.users else {}

    # --- STOMP over WebSocket ------------------------------------------------------
    def websocket(self, connection, request_headers):
        lock = threading.Lock()

        def send(frame):
            with lock:
                try:
                    connection.sendall(ws_frame(frame))
                except OSError:
                    pass

        session = uuid.uuid4().hex[:8]
        buffer = b''
        try:
            while True:
                head = self._read(connection, 2)
                if not head:
                    break
                opcode, size = head[0] & 0x0F, head[1] & 0x7F
                if size == 126:
                    size = struct.unpack('>H', self._read(connection, 2))[0]
                elif size == 127:
                    size = struct.unpack('>Q', self._read(connection, 8))[0]
                mask = self._read(connection, 4) if head[1] & 0x80 else b'\0\0\0\0'
                payload = bytes(b ^ mask[i % 4] for i, b in enumerate(self._read(connection, size)))
                if opcode == 8:
                    break
                if opcode == 9:
                    with lock:
                        connection.sendall(ws_frame(payload, 10))
                    continue
                buffer += payload
                while b'\0' in buffer:
                    frame, buffer = buffer.split(b'\0', 1)
                    if not frame.strip(b'\r\n'):
                        continue  # heart-beat
                    self._stomp(send, session, *parse_stomp(frame))
                if buffer.strip(b'\r\n') == b'':
                    buffer = b''
        finally:
            with self.lock:
                self.subscribers = [s for s in self.subscribers if s[3] != session]
            self.log({'kind': 'ws-close', 'session': session})

    def _stomp(self, send, session, command, headers, body):
        self.log({'kind': 'stomp', 'session': session, 'command': command, 'headers': headers,
                  'body': body[:2000]})
        if command in ('CONNECT', 'STOMP'):
            send(stomp_frame('CONNECTED', {'version': '1.2', 'heart-beat': '0,0',
                                           'server': 'aion-local-lime/0.1'}))
        elif command == 'SUBSCRIBE':
            with self.lock:
                self.subscribers.append((send, headers.get('destination', ''),
                                         headers.get('id', '0'), session))
        elif command == 'UNSUBSCRIBE':
            with self.lock:
                self.subscribers = [s for s in self.subscribers
                                    if not (s[3] == session and s[2] == headers.get('id'))]
        elif command == 'DISCONNECT' and headers.get('receipt'):
            send(stomp_frame('RECEIPT', {'receipt-id': headers['receipt']}))
        if headers.get('receipt') and command != 'DISCONNECT':
            send(stomp_frame('RECEIPT', {'receipt-id': headers['receipt']}))

    @staticmethod
    def _read(connection, size):
        data = b''
        while len(data) < size:
            chunk = connection.recv(size - len(data))
            if not chunk:
                return b''
            data += chunk
        return data


def make_handler(service):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'

        def log_message(self, *_):
            pass

        def _body(self):
            length = int(self.headers.get('Content-Length', '0') or 0)
            raw = self.rfile.read(min(length, 1048576)) if length > 0 else b''
            try:
                return raw, json.loads(raw.decode('utf-8')) if raw else {}
            except ValueError:
                return raw, {}

        def do_GET(self):
            path = urlsplit(self.path).path
            if path.rstrip('/') == '/stomp' and self.headers.get('Upgrade', '').lower() == 'websocket':
                key = self.headers.get('Sec-WebSocket-Key', '')
                accept = base64.b64encode(hashlib.sha1((key + WS_GUID).encode()).digest()).decode()
                self.send_response(101, 'Switching Protocols')
                self.send_header('Upgrade', 'websocket')
                self.send_header('Connection', 'Upgrade')
                self.send_header('Sec-WebSocket-Accept', accept)
                offered = self.headers.get('Sec-WebSocket-Protocol', '')
                if offered:
                    self.send_header('Sec-WebSocket-Protocol', offered.split(',')[0].strip())
                self.end_headers()
                service.log({'kind': 'ws-open', 'path': self.path, 'headers': dict(self.headers.items())})
                self.close_connection = True
                service.websocket(self.connection, dict(self.headers.items()))
                return
            self._reply(path, {}, b'')

        def do_POST(self):
            path = urlsplit(self.path).path
            raw, body = self._body()
            self._reply(path, body, raw)

        def _reply(self, path, body, raw):
            headers = dict(self.headers.items())
            response = service.handle(path, body if isinstance(body, dict) else {}, headers)
            data = json.dumps(response).encode('utf-8')
            service.log({'kind': 'http', 'method': self.command, 'path': self.path,
                         'headers': headers, 'body': raw.decode('utf-8', 'replace')[:8000],
                         'response': response})
            self.send_response(200)
            self.send_header('Content-Type', 'application/json; charset=utf-8')
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)

    return Handler


def serve(port=8091, log_path=None, stop_event=None, on_ready=None):
    service = LimeService(port=port, log_path=log_path)
    server = ThreadingHTTPServer(('127.0.0.1', port), make_handler(service))
    server.daemon_threads = True
    server.timeout = 0.5
    if on_ready:
        on_ready(port)
    try:
        while not (stop_event and stop_event.is_set()):
            server.handle_request()
    finally:
        server.server_close()
    return service


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8091)
    parser.add_argument('--log', type=Path)
    args = parser.parse_args()
    serve(args.port, args.log)
