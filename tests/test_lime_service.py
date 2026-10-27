import base64
import http.client
import json
import os
import socket
import threading
import unittest

from tools.lime_service import LimeService, parse_stomp, serve, stomp_frame


LOGIN = {'authnToken': 't', 'gameCode': 'AION2', 'deviceId': 'd', 'clientType': 'GAME',
         'characterId': '1099511629056', 'serverKey': '1204', 'locale': 'en_US'}


def free_port():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]


class LimeServiceTests(unittest.TestCase):
    def test_login_returns_subscription_on_our_stomp_endpoint(self):
        service = LimeService(port=9999)
        reply = service.handle('/gameLogin/loginWithToken', LOGIN, {})
        self.assertEqual(reply['error'], 0)
        self.assertEqual(reply['characterId'], '1099511629056')
        self.assertTrue(reply['mtalkToken'])
        info = reply['subscriptionInfo']
        self.assertEqual(info['subscribeUrl'], 'ws://127.0.0.1:9999/stomp')
        self.assertIn('1204', info['worldTopic'])

    def test_send_message_returns_guid(self):
        service = LimeService()
        reply = service.handle('/gameClient/sendMessage',
                               {'gameMessageInfo': {'content': 'hi'}}, {})
        self.assertEqual(reply['error'], 0)
        self.assertEqual(len(reply['guid']), 32)

    def test_stomp_frame_round_trip(self):
        frame = stomp_frame('SEND', {'destination': '/x'}, 'body')
        self.assertTrue(frame.endswith(b'\0'))
        self.assertEqual(parse_stomp(frame[:-1]), ('SEND', {'destination': '/x'}, 'body'))

    def test_websocket_subscriber_receives_sent_message(self):
        port = free_port()
        stop = threading.Event()
        ready = threading.Event()
        worker = threading.Thread(target=serve, kwargs={'port': port, 'stop_event': stop,
                                                        'on_ready': lambda _: ready.set()})
        worker.start()
        try:
            ready.wait(3)
            ws = socket.create_connection(('127.0.0.1', port), timeout=3)
            key = base64.b64encode(os.urandom(16)).decode()
            ws.sendall((f'GET /stomp HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\n'
                        f'Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\n'
                        f'Sec-WebSocket-Version: 13\r\n\r\n').encode())
            self.assertIn(b'101', ws.recv(1024))

            def send_ws(data):
                mask = b'\x01\x02\x03\x04'
                masked = bytes(b ^ mask[i % 4] for i, b in enumerate(data))
                ws.sendall(bytes([0x81, 0x80 | len(data)]) + mask + masked)

            def recv_ws():
                head = ws.recv(2)
                size = head[1] & 0x7F
                if size == 126:
                    size = int.from_bytes(ws.recv(2), 'big')
                data = b''
                while len(data) < size:
                    data += ws.recv(size - len(data))
                return data

            send_ws(stomp_frame('CONNECT', {'accept-version': '1.2'}))
            self.assertTrue(recv_ws().startswith(b'CONNECTED'))
            send_ws(stomp_frame('SUBSCRIBE', {'id': 'sub-0', 'destination': '/topic/world.1204'}))
            connection = http.client.HTTPConnection('127.0.0.1', port, timeout=3)
            connection.request('POST', '/gameClient/sendMessage', json.dumps(
                {'gameRoomKeyInfo': {'type': 'WORLD', 'serverKey': '1204', 'roomKey': 'RG1'},
                 'gameMessageInfo': {'content': 'test 123', 'userName': 'Tester'}}))
            self.assertEqual(json.loads(connection.getresponse().read())['error'], 0)
            command, headers, body = parse_stomp(recv_ws()[:-1])
            self.assertEqual(command, 'MESSAGE')
            self.assertEqual(headers['subscription'], 'sub-0')
            envelope = json.loads(body)
            self.assertEqual(envelope['type'], 'NOTI')
            self.assertEqual(envelope['method'], 'GAME')  # the client's NOTI handler requires it
            self.assertEqual(envelope['jsonData']['type'], 'PUBLISH')
            self.assertEqual(envelope['jsonData']['content'], 'test 123')
            ws.close()
            connection.close()
        finally:
            stop.set()
            worker.join(timeout=3)


if __name__ == '__main__':
    unittest.main()
