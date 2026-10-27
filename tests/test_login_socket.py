import socket
import threading
import unittest

from tools.probe_login_socket import receive, summarize


class LoginSocketTests(unittest.TestCase):
    def test_fragmented_stream_is_combined_and_eof_retained(self):
        left, right = socket.socketpair()
        with left, right:
            def send():
                for chunk in (b'abc', b'd', b'ef'):
                    right.sendall(chunk)
                right.shutdown(socket.SHUT_WR)
            worker = threading.Thread(target=send)
            worker.start()
            self.assertEqual(receive(left), (b'abcdef', 'peer_closed'))
            worker.join()

    def test_byte_limit_and_deadline(self):
        left, right = socket.socketpair()
        with left, right:
            right.sendall(b'abcdefgh')
            self.assertEqual(receive(left, limit=4), (b'abcd', 'byte_limit'))
            self.assertEqual(receive(left, seconds=0.02), (b'efgh', 'deadline'))

    def test_incomplete_frame_and_unknown_body_are_not_exposed(self):
        self.assertEqual(summarize(b'\x80')['trailing_bytes'], 1)
        body = b'\x34\x12private-secret'
        result = summarize(bytes([len(body)+4])+body)
        self.assertEqual(result['frames'][0]['tag_candidate'], '0x1234')
        self.assertEqual(result['trailing_bytes'], 0)
        self.assertNotIn('private-secret', str(result))
        self.assertIn('framing_error', summarize(b'\x01'))
