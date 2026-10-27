import unittest
from unittest.mock import Mock, patch
from urllib.error import HTTPError
from tools.world_store import WorldStore, WorldStoreError


class WorldStoreTests(unittest.TestCase):
    def test_credentials_not_in_diagnostics(self):
        store = WorldStore(8780, 'machine-secret', 'A' * 64)
        self.assertNotIn('machine-secret', repr(store))
        self.assertNotIn('A' * 64, repr(store))

    def test_http_errors_preserve_only_status(self):
        store = WorldStore(8780, 'machine-secret', 'A' * 64)
        store._opener = Mock()
        store._opener.open.side_effect = HTTPError('secret-url', 409, 'secret detail', {}, None)
        with self.assertRaises(WorldStoreError) as error:
            store.characters()
        self.assertEqual(error.exception.status, 409)
        self.assertNotIn('secret', str(error.exception))

    def test_close_discards_token_even_when_service_unavailable(self):
        store = WorldStore(8780, 'secret', 'A' * 64)
        with patch.object(store, '_request', side_effect=WorldStoreError()):
            with self.assertRaises(WorldStoreError):
                store.close()
        store.close()
        with self.assertRaises(WorldStoreError) as error:
            store.characters()
        self.assertEqual(error.exception.status, 401)

    def test_character_path_requires_uuid(self):
        store = WorldStore(8780, 'secret', 'A' * 64)
        with self.assertRaises(ValueError):
            store.load('../accounts')
