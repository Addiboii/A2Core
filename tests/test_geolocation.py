import json
import threading
import unittest
from http.server import HTTPServer
from urllib.request import urlopen
from urllib.error import HTTPError

from tools.probe_local_bootstrap import handler
from tools.run_game_chain import geolocation_response


class GeolocationTests(unittest.TestCase):
    def serve(self, body):
        events = []
        server = HTTPServer(('127.0.0.1', 0), handler(events, geolocation_response=body))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return f'http://127.0.0.1:{server.server_address[1]}/local-api-gate/geolocations/v1.0/me'

    def test_route_returns_our_location(self):
        body = json.dumps(geolocation_response('59.9139,10.7522,no')).encode()
        with urlopen(self.serve(body), timeout=5) as response:
            data = json.loads(response.read())
        # The client's parser requires strings for these (EJson type 2), not numbers.
        self.assertEqual(data, {'geolocation': 'NO',
                                'coordinates': {'latitude': '59.9139', 'longitude': '10.7522'}})

    def test_route_stays_503_without_a_location(self):
        with self.assertRaises(HTTPError) as error:
            urlopen(self.serve(None), timeout=5)
        self.assertEqual(error.exception.code, 503)

    def test_bad_location_is_rejected(self):
        for text in ('91,0,NO', '0,0,NOR', 'oslo'):
            with self.assertRaises(ValueError):
                geolocation_response(text)


if __name__ == '__main__':
    unittest.main()
