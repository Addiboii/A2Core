"""End-to-end check of the .NET host's Lime chat API (the port of tools/lime_service.py).

Starts the built Aion.Host with a chat port and development chat logins, then runs the
client's observed flow: loginWithToken, STOMP CONNECT/SUBSCRIBE, joinRoom, sendMessage,
and expects the message back as a NOTI envelope. Skipped when the host is not built
(dotnet build server/Aion.Host/Aion.Host.csproj).
"""
import base64
import http.client
import json
import os
import shutil
import socket
import subprocess
import time
import unittest
from pathlib import Path

from tools.lime_service import parse_stomp, stomp_frame

ROOT = Path(__file__).resolve().parents[1]
HOST_DLL = (ROOT / os.environ['AION_ARTIFACTS_PATH'] / 'bin/Aion.Host/debug/Aion.Host.dll'
            if os.environ.get('AION_ARTIFACTS_PATH') else
            ROOT / 'server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll')


def free_port():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]


@unittest.skipUnless(HOST_DLL.exists() and shutil.which('dotnet'), 'Aion.Host is not built')
class DotnetChatHostTests(unittest.TestCase):
    development_logins = 'true'

    def setUp(self):
        self.port = free_port()
        self.account_port = free_port()
        env = dict(os.environ, Aion__LocalPort=str(self.account_port), Aion__ChatPort=str(self.port),
                   Aion__ChatDevelopmentLogins=self.development_logins)
        env.pop('ConnectionStrings__Aion', None)
        env.pop('ConnectionStrings__Auth', None)
        self.process = subprocess.Popen(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env,
                                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            try:
                socket.create_connection(('127.0.0.1', self.port), timeout=0.5).close()
                return
            except OSError:
                time.sleep(0.2)
        self.fail('host did not open the chat port')

    def tearDown(self):
        self.process.terminate()
        self.process.wait(timeout=10)

    def post(self, path, body, token=''):
        connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=5)
        connection.request('POST', path, json.dumps(body),
                           {'Content-Type': 'application/json; charset=utf-8', 'Authorization': token})
        reply = json.loads(connection.getresponse().read())
        connection.close()
        return reply

    def test_client_flow_round_trips_a_message(self):
        self.round_trip(include_host_port=True)

    def test_native_websocket_host_without_port_round_trips_a_message(self):
        self.round_trip(include_host_port=False)

    def test_spoofed_host_cannot_enable_chat_on_account_port(self):
        connection = http.client.HTTPConnection('127.0.0.1', self.account_port, timeout=5)
        connection.request('POST', '/gameLogin/loginWithToken', '{}',
                           {'Host': f'127.0.0.1:{self.port}', 'Content-Type': 'application/json'})
        self.assertEqual(connection.getresponse().status, 404)
        connection.close()

    def round_trip(self, include_host_port):
        login = self.post('/gameLogin/loginWithToken', {
            'authnToken': 'local', 'gameCode': 'AION2', 'deviceId': 'd', 'clientType': 'GAME',
            'characterId': '1099511629056', 'serverKey': '1204', 'locale': 'en_US'})
        self.assertEqual(login['error'], 0)
        token = login['mtalkToken']
        self.assertEqual(login['subscriptionInfo']['subscribeUrl'], f'ws://127.0.0.1:{self.port}/stomp')

        ws = socket.create_connection(('127.0.0.1', self.port), timeout=5)
        key = base64.b64encode(os.urandom(16)).decode()
        host = f'127.0.0.1:{self.port}' if include_host_port else '127.0.0.1'
        ws.sendall((f'GET /stomp HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\n'
                    f'Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n'
                    f'Sec-WebSocket-Protocol: v10.stomp,v11.stomp,v12.stomp\r\n\r\n').encode())
        handshake = b''
        while b'\r\n\r\n' not in handshake:
            handshake += ws.recv(1024)
        self.assertIn(b'101', handshake.split(b'\r\n')[0])

        def send_ws(data):
            mask = os.urandom(4)
            masked = bytes(b ^ mask[i % 4] for i, b in enumerate(data))
            header = bytes([0x81, 0x80 | len(data)]) if len(data) < 126 else \
                bytes([0x81, 0xFE]) + len(data).to_bytes(2, 'big')
            ws.sendall(header + mask + masked)

        def recv_ws():
            head = ws.recv(2)
            size = head[1] & 0x7F
            if size == 126:
                size = int.from_bytes(ws.recv(2), 'big')
            data = b''
            while len(data) < size:
                data += ws.recv(size - len(data))
            return data

        send_ws(stomp_frame('CONNECT', {'authorization': token, 'accept-version': '1.0,1.1,1.2'}))
        self.assertTrue(recv_ws().startswith(b'CONNECTED'))
        send_ws(stomp_frame('SUBSCRIBE', {'id': 'sub-0', 'destination': '/topic/user.1204.1099511629056'}))
        room = {'type': 'REGION', 'serverKey': '1204', 'roomKey': 'RG'}
        self.assertEqual(self.post('/gameClient/joinRoom', {'gameRoomKeyInfo': room}, token)['error'], 0)
        time.sleep(0.2)  # SUBSCRIBE is processed asynchronously
        sent = self.post('/gameClient/sendMessage', {'gameRoomKeyInfo': room, 'gameMessageInfo': {
            'userName': 'Player1', 'type': 'PUBLISH', 'subType': 'NORMAL', 'content': 'test 123'}}, token)
        self.assertEqual(sent['error'], 0)
        command, headers, body = parse_stomp(recv_ws()[:-1])
        self.assertEqual((command, headers['subscription']), ('MESSAGE', 'sub-0'))
        envelope = json.loads(body)
        self.assertEqual((envelope['type'], envelope['method']), ('NOTI', 'GAME'))
        self.assertEqual(envelope['jsonData']['content'], 'test 123')
        self.assertEqual(envelope['tid'], sent['guid'])
        ws.close()

    def login(self, character_id):
        login = self.post('/gameLogin/loginWithToken', {
            'authnToken': 'local', 'gameCode': 'AION2', 'deviceId': 'd', 'clientType': 'GAME',
            'characterId': character_id, 'serverKey': '1204', 'locale': 'en_US'})
        self.assertEqual(login['error'], 0)
        return login['mtalkToken'], StompClient(self.port, login['mtalkToken'], login['subscriptionInfo']['topicName'])

    def test_two_players_share_rooms_and_whisper_by_name(self):
        room = {'type': 'REGION', 'serverKey': '1204', 'roomKey': 'RG'}
        tokens, clients = zip(*(self.login(cid) for cid in ('1099511629056', '1099511629057')))
        try:
            for token in tokens:
                self.assertEqual(self.post('/gameClient/joinRoom', {'gameRoomKeyInfo': room}, token)['error'], 0)
            time.sleep(0.2)  # SUBSCRIBE is processed asynchronously
            for token, name in zip(tokens, ('Alice', 'Bob')):
                sent = self.post('/gameClient/sendMessage', {'gameRoomKeyInfo': room, 'gameMessageInfo': {
                    'userName': name, 'type': 'PUBLISH', 'subType': 'NORMAL', 'content': 'hi from ' + name}}, token)
                self.assertEqual(sent['error'], 0)
                for client in clients:
                    self.assertEqual(client.message()['jsonData']['content'], 'hi from ' + name)
            whisper = self.post('/gameClient/sendWhisper', {'gameUserKey': {}, 'gameMessageInfo': {
                'userName': 'Alice', 'content': 'psst'}, 'validationInfo': {'targetCharacterName': '[GM] Bob'}}, tokens[0])
            self.assertEqual(whisper['error'], 0)
            for client in clients:
                received = client.message()['jsonData']
                self.assertEqual((received['content'], received['receiverUserName']), ('psst', 'Bob'))
            offline = self.post('/gameClient/sendWhisper', {'gameMessageInfo': {'userName': 'Alice', 'content': 'x'},
                                                            'validationInfo': {'targetCharacterName': 'Nobody'}}, tokens[0])
            self.assertNotEqual(offline['error'], 0)
            guild = {'type': 'GUILD', 'serverKey': '1204', 'roomKey': 'GU1'}
            self.assertNotEqual(self.post('/gameClient/joinRoom', {'gameRoomKeyInfo': guild}, tokens[0])['error'], 0)
        finally:
            for client in clients:
                client.close()

    def test_unknown_session_is_rejected(self):
        reply = self.post('/gameClient/sendMessage', {'gameRoomKeyInfo': {}, 'gameMessageInfo': {'content': 'x'}},
                          'mtalk-unknown')
        self.assertNotEqual(reply['error'], 0)


class DotnetChatHostAccountModeTests(DotnetChatHostTests):
    """Without development logins and without account databases every chat login is rejected."""
    development_logins = 'false'

    def round_trip(self, include_host_port):
        reply = self.post('/gameLogin/loginWithToken', {
            'authnToken': 'a' * 64, 'gameCode': 'AION2', 'deviceId': 'd', 'clientType': 'GAME',
            'characterId': '1099511629056', 'serverKey': '1204', 'locale': 'en_US'})
        self.assertNotEqual(reply['error'], 0)
        self.assertNotIn('mtalkToken', reply)

    def test_two_players_share_rooms_and_whisper_by_name(self):
        self.round_trip(True)


class StompClient:
    """Minimal STOMP-over-WebSocket subscriber for the tests."""

    def __init__(self, port, token, topic):
        self.ws = socket.create_connection(('127.0.0.1', port), timeout=5)
        key = base64.b64encode(os.urandom(16)).decode()
        self.ws.sendall((f'GET /stomp HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\n'
                         f'Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n'
                         f'Sec-WebSocket-Protocol: v12.stomp\r\n\r\n').encode())
        handshake = b''
        while b'\r\n\r\n' not in handshake:
            handshake += self.ws.recv(1024)
        assert b'101' in handshake.split(b'\r\n')[0]
        self.send(stomp_frame('CONNECT', {'authorization': token, 'accept-version': '1.2'}))
        assert self.recv().startswith(b'CONNECTED')
        self.send(stomp_frame('SUBSCRIBE', {'id': 'sub-0', 'destination': topic}))

    def send(self, data):
        mask = os.urandom(4)
        masked = bytes(b ^ mask[i % 4] for i, b in enumerate(data))
        header = (bytes([0x81, 0x80 | len(data)]) if len(data) < 126
                  else bytes([0x81, 0xFE]) + len(data).to_bytes(2, 'big'))
        self.ws.sendall(header + mask + masked)

    def recv_exact(self, size):
        data = b''
        while len(data) < size:
            data += self.ws.recv(size - len(data))
        return data

    def recv(self):
        head = self.recv_exact(2)
        size = head[1] & 0x7F
        if size == 126:
            size = int.from_bytes(self.recv_exact(2), 'big')
        return self.recv_exact(size)

    def message(self):
        command, _, body = parse_stomp(self.recv()[:-1])
        assert command == 'MESSAGE', command
        return json.loads(body)

    def close(self):
        self.ws.close()


if __name__ == '__main__':
    unittest.main()
