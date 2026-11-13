import os
from pathlib import Path
import tempfile
import unittest
import uuid
from concurrent.futures import ThreadPoolExecutor

from unittest.mock import patch

from tools import postgres_dev
from tools.postgres_dev import literal, migration_script, sql, migrate, role_statement


class MigrationTests(unittest.TestCase):
    def test_windows_port_is_below_the_dynamic_range(self):
        from tools import postgres_dev
        if postgres_dev.WINDOWS and not os.environ.get('AION_PG_PORT'):
            self.assertLess(postgres_dev.PORT, 49152)

    def test_an_older_cluster_moves_to_the_port(self):
        from tools.postgres_dev import PORT, configured_port
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / 'postgresql.conf'
            old = 55432 if PORT != 55432 else 15432
            config.write_text(f"#port = 5432\nlisten_addresses='127.0.0.1'\nport={old}\n", encoding='utf-8')
            self.assertTrue(configured_port(config))
            self.assertEqual(config.read_text(encoding='utf-8'), f"#port = 5432\nlisten_addresses='127.0.0.1'\nport={PORT}\n")
            self.assertFalse(configured_port(config))

    def test_sql_literal(self):
        self.assertEqual(literal("O'Brien"), "'O''Brien'")

    def test_migrations_are_checked_and_transactional(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / '001.sql'
            path.write_text('SELECT 1;', encoding='utf-8')
            script = migration_script([path])
        self.assertTrue(script.startswith('BEGIN;'))
        self.assertIn('pg_advisory_xact_lock', script)
        self.assertIn('Applied migration changed', script)
        self.assertIn('\\if :apply', script)
        self.assertTrue(script.endswith('COMMIT;'))

    def test_tools_run_untranslated_and_decode_tolerantly(self):
        from unittest import mock
        from tools import postgres_dev
        done = postgres_dev.subprocess.CompletedProcess([], 0, '', '')
        with mock.patch.object(postgres_dev.subprocess, 'run', return_value=done) as run:
            postgres_dev.run('psql', ['-c', 'SELECT 1'], check=False)
        kwargs = run.call_args.kwargs
        self.assertEqual(kwargs['env']['LC_MESSAGES'], 'C')
        self.assertEqual(kwargs['errors'], 'replace')

    def test_line_endings_do_not_count_as_a_change(self):
        # A Windows checkout (autocrlf) has CRLF where the migration may have been applied from LF, and the other way round.
        import hashlib
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / '001.sql'
            path.write_bytes(b'SELECT 1;\r\nSELECT 2;\r\n')
            script = migration_script([path])
        for form in (b'SELECT 1;\nSELECT 2;\n', b'SELECT 1;\r\nSELECT 2;\r\n'):
            self.assertIn(literal(hashlib.sha256(form).hexdigest()), script)
        self.assertNotIn(literal(hashlib.sha256(b'SELECT 1;\nSELECT 3;\n').hexdigest()), script)


class TimeoutTests(unittest.TestCase):
    def calls(self, action):
        seen = []
        with patch.object(postgres_dev, 'run', lambda binary, args=(), **kw: seen.append((binary, kw.get('timeout', 180)))),                 patch.object(postgres_dev, 'sql', lambda text, **kw: seen.append(('psql', kw.get('timeout', 180))) or
                             type('R', (), {'stdout': ''})()):
            try:
                action()
            except Exception:
                pass
        return seen

    def test_long_operations_use_the_configured_timeout(self):
        with patch.dict(os.environ, {'AION_PG_TIMEOUT': '7200'}):
            self.assertEqual(postgres_dev.long_timeout(), 7200)
            seen = self.calls(lambda: postgres_dev.verify_backup('x.dump'))
            self.assertIn(('pg_restore', 7200), seen)
            # The short calls around it (create, count, drop) keep 180 s.
            self.assertTrue(all(timeout == 180 for binary, timeout in seen if binary == 'psql'))
        with patch.dict(os.environ, {'AION_PG_TIMEOUT': ''}):
            self.assertEqual(postgres_dev.long_timeout(), 3600)
        for bad in ('soon', '0', '-5'):
            with patch.dict(os.environ, {'AION_PG_TIMEOUT': bad}), self.assertRaises(SystemExit):
                postgres_dev.long_timeout()

    def test_import_catalog_uses_the_long_timeout(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            (folder / 'manifest.json').write_text('{"counts": {"items": 0, "skills": 0}}', encoding='utf-8')
            (folder / 'entries.jsonl').write_text('', encoding='utf-8')
            with patch.object(postgres_dev, 'HOME', folder), patch.dict(os.environ, {'AION_PG_TIMEOUT': '4000'}):
                seen = self.calls(lambda: postgres_dev.import_catalog(folder))
        self.assertEqual(seen, [('psql', 4000)])

    def test_short_calls_default_to_180_seconds(self):
        self.assertEqual(postgres_dev.SHORT_TIMEOUT, 180)
        import inspect
        self.assertEqual(inspect.signature(postgres_dev.run).parameters['timeout'].default, 180)
        self.assertEqual(inspect.signature(postgres_dev.sql).parameters['timeout'].default, 180)


@unittest.skipUnless(os.environ.get('AION_POSTGRES_TESTS') == '1', 'Requires local PostgreSQL and imported catalogue')
class PostgreSQLTests(unittest.TestCase):
    def setUp(self):
        self.account, self.character = str(uuid.uuid4()), str(uuid.uuid4())
        self.build = sql('SELECT build_id FROM catalog.builds ORDER BY imported_at LIMIT 1;').stdout.strip()
        self.assertTrue(self.build)
        sql(f"INSERT INTO auth.accounts(account_id,provider,subject) VALUES('{self.account}','integration-test','{self.account}');"
            f"INSERT INTO game.characters(character_id,account_id,world_id,name,creation_id,race,gender) VALUES('{self.character}','{self.account}',-999,'{self.character}',1,1,1);")
        self.addCleanup(self.cleanup_fixture)

    def cleanup_fixture(self):
        sql(f"BEGIN; DELETE FROM game.item_grants WHERE instance_id IN(SELECT instance_id FROM game.item_instances WHERE character_id='{self.character}');"
            f"DELETE FROM audit.events WHERE character_id='{self.character}';"
            f"DELETE FROM game.item_instances WHERE character_id='{self.character}';"
            f"DELETE FROM game.characters WHERE character_id='{self.character}';"
            f"DELETE FROM auth.accounts WHERE account_id='{self.account}'; COMMIT;")

    def grant(self, request, quantity=1):
        return sql(f"SELECT game.grant_item('{self.character}','{self.build}',110160001,{quantity},'{request}');", role='aion_app').stdout.strip()

    def test_concurrent_grant_retry_returns_one_item(self):
        request = str(uuid.uuid4())
        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(lambda _: self.grant(request), range(2)))
        self.assertEqual(results[0], results[1])
        self.assertEqual(sql(f"SELECT count(*) FROM game.item_instances WHERE character_id='{self.character}';").stdout.strip(), '1')
        self.assertEqual(sql(f"SELECT count(*) FROM audit.events WHERE character_id='{self.character}';").stdout.strip(), '1')
        with self.assertRaisesRegex(RuntimeError, 'different arguments'):
            self.grant(request, 2)

    def test_invalid_grant_rolls_back(self):
        request = str(uuid.uuid4())
        result = sql(f"SELECT game.grant_item('{self.character}','{self.build}',-999,1,'{request}');", role='aion_app', check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(sql(f"SELECT count(*) FROM game.item_grants WHERE request_id='{request}';").stdout.strip(), '0')
        with self.assertRaisesRegex(RuntimeError, 'positive'):
            self.grant(request, 0)

    def test_runtime_cannot_assign_roles_change_catalog_or_delete_audit(self):
        statements = [f"INSERT INTO auth.account_roles VALUES('{self.account}','admin');",
                      "UPDATE catalog.items SET name='changed' WHERE false;", 'DELETE FROM audit.events WHERE false;']
        for statement in statements:
            self.assertNotEqual(sql(statement, role='aion_app', check=False).returncode, 0)

    def test_revisions_reject_stale_save_and_equipment_slot_conflict(self):
        result = sql(f"UPDATE game.characters SET level=2 WHERE character_id='{self.character}' AND revision=0 RETURNING revision;", role='aion_app')
        self.assertEqual(result.stdout.strip(), '1')
        stale = sql(f"UPDATE game.characters SET level=3 WHERE character_id='{self.character}' AND revision=0 RETURNING revision;", role='aion_app')
        self.assertEqual(stale.stdout.strip(), '')
        first, second = self.grant(str(uuid.uuid4())), self.grant(str(uuid.uuid4()))
        sql(f"UPDATE game.item_instances SET location='equipment',slot=1 WHERE instance_id='{first}';", role='aion_app')
        conflict = sql(f"UPDATE game.item_instances SET location='equipment',slot=1 WHERE instance_id='{second}';", role='aion_app', check=False)
        self.assertNotEqual(conflict.returncode, 0)

    def test_repeat_migrations_and_checksum_mismatch(self):
        migrate()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / '001_initial.sql'
            path.write_text('-- changed', encoding='utf-8')
            result = sql(migration_script([path]), check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Applied migration changed', result.stderr)


class RoleStatementTests(unittest.TestCase):
    def test_role_statement_targets_one_local_account(self):
        grant = role_statement('staffuser', 'admin')
        self.assertIn("provider='local' AND subject='staffuser'", grant)
        self.assertIn("'admin'", grant)
        # One rank per account (migration 076): a grant replaces the held rank.
        self.assertIn('ON CONFLICT (account_id) DO UPDATE SET role=EXCLUDED.role', grant)
        self.assertIn("'moderator'", role_statement('staffuser', 'moderator'))
        self.assertTrue(role_statement('staffuser', 'gamemaster', revoke=True).startswith('DELETE FROM auth.account_roles'))

    def test_role_statement_rejects_unsafe_input(self):
        for username, role in [("x' OR '1'='1", 'admin'), ('staffuser', 'owner'), ('', 'gamemaster'), ('a' * 33, 'gamemaster'), ('staffuser', 'gm')]:
            with self.assertRaises(ValueError):
                role_statement(username, role)
