"""Read-only health audit of the A2Core database: migrations, size and bloat, integrity, leftovers, backups.

Run as the service account (it holds the database credentials), feeding the script on stdin so the account does not
need to read this checkout:

    sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres python3 - < tools/db_audit.py

Every query runs in a read-only transaction; the output is counts and sizes only (no names, mail, chat or hashes).
"""
import hashlib
import os
import sys
from pathlib import Path

SRC = Path(os.environ.get('AION_SRC', '/opt/aion/src'))
sys.path.insert(0, str(SRC / 'tools'))
os.environ['PGOPTIONS'] = '-c default_transaction_read_only=on -c statement_timeout=60000'
import postgres_dev as pg  # noqa: E402


def query(text):
    out = pg.run('psql', ['-X', '-q', '-A', '-t', '-F', '|', '-v', 'ON_ERROR_STOP=1', '-f', '-'], text=text).stdout
    return [line.split('|') for line in out.splitlines() if line.strip()]


def section(title):
    print(f'\n== {title}')


section('server')
for name, value in query("""
SELECT 'version', split_part(version(), ' ', 2);
SELECT 'database size', pg_size_pretty(pg_database_size(current_database()));
SELECT 'connections', count(*)::text FROM pg_stat_activity WHERE datname = current_database();
SELECT 'idle in transaction > 5 min', count(*)::text FROM pg_stat_activity
  WHERE state = 'idle in transaction' AND now() - state_change > interval '5 minutes';
SELECT 'queries running > 5 min', count(*)::text FROM pg_stat_activity
  WHERE state = 'active' AND now() - query_start > interval '5 minutes' AND pid <> pg_backend_pid();
SELECT 'autovacuum', current_setting('autovacuum');
SELECT 'invalid indexes', count(*)::text FROM pg_index WHERE NOT indisvalid OR NOT indisready;
SELECT 'xid age (wraparound at 2e9)', max(age(datfrozenxid))::text FROM pg_database;
"""):
    print(f'  {name}: {value}')

section('migrations')
files = sorted((SRC / 'database/migrations').glob('*.sql'))
applied = {v: h for v, h in query('SELECT version, sha256 FROM public.schema_migrations ORDER BY version;')}
pending = [f.name for f in files if f.name not in applied]
changed = [f.name for f in files if f.name in applied and applied[f.name] != hashlib.sha256(f.read_bytes()).hexdigest()]
unknown = sorted(set(applied) - {f.name for f in files})
print(f'  files {len(files)}, applied {len(applied)}, latest applied {max(applied) if applied else "-"}')
print(f'  pending: {pending or "none"}')
print(f'  applied but changed since: {changed or "none"}')
print(f'  applied but no file: {unknown or "none"}')

section('largest tables (rows, total size, dead rows, last vacuum)')
for row in query("""
SELECT n.nspname || '.' || c.relname, coalesce(s.n_live_tup, 0), pg_size_pretty(pg_total_relation_size(c.oid)),
       coalesce(s.n_dead_tup, 0), coalesce(greatest(s.last_vacuum, s.last_autovacuum)::date::text, 'never')
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
WHERE c.relkind = 'r' AND n.nspname IN ('auth', 'game', 'catalog', 'audit', 'public')
ORDER BY pg_total_relation_size(c.oid) DESC LIMIT 15;
"""):
    print(f'  {row[0]:<38} rows {row[1]:>9}  {row[2]:>9}  dead {row[3]:>7}  vacuum {row[4]}')

section('catalogue')
for name, value in query("""
SELECT 'builds', string_agg(build_id || ' (' || imported_at::date || ')', ', ' ORDER BY imported_at) FROM catalog.builds;
SELECT 'catalogue items', count(*)::text FROM catalog.items;
SELECT 'catalogue skills', count(*)::text FROM catalog.skills;
SELECT 'items by build', string_agg(build_id || '=' || n, ', ') FROM (SELECT build_id, count(*) n FROM game.item_instances GROUP BY 1) x;
SELECT 'equipment rules', count(*)::text FROM game.equipment_rules;
SELECT 'starter profiles', count(*)::text FROM game.starter_profiles;
SELECT 'stack limits', count(*)::text FROM game.item_stacks;
"""):
    print(f'  {name}: {value}')

section('accounts and characters')
for name, value in query("""
SELECT 'accounts', count(*)::text FROM auth.accounts;
SELECT 'characters', count(*)::text FROM game.characters;
SELECT 'characters pending delete (overdue)', count(*) FILTER (WHERE pending_delete_at IS NOT NULL)::text || ' (' ||
       count(*) FILTER (WHERE pending_delete_at < now())::text || ')' FROM game.characters;
SELECT 'characters without a map', count(*)::text FROM game.characters WHERE map_id IS NULL OR map_id <= 0;
SELECT 'characters with empty position', count(*)::text FROM game.characters WHERE position = '{}'::jsonb;
SELECT 'staff characters', count(*)::text FROM game.characters WHERE staff;
"""):
    print(f'  {name}: {value}')

section('items')
for name, value in query("""
SELECT 'item instances', count(*)::text FROM game.item_instances;
SELECT 'by location', string_agg(location || '=' || n, ', ') FROM (SELECT location, count(*) n FROM game.item_instances GROUP BY 1) x;
SELECT 'bag items without a slot', count(*)::text FROM game.item_instances WHERE location = 'bag' AND slot IS NULL;
SELECT 'storage items without a slot', count(*)::text FROM game.item_instances WHERE location = 'storage' AND slot IS NULL;
SELECT 'stacks over their limit', count(*)::text FROM game.item_instances i JOIN game.item_stacks s USING (template_id) WHERE i.quantity > s.max_stack;
SELECT 'non-stackable items with quantity > 1', count(*)::text FROM game.item_instances i
  WHERE i.quantity > 1 AND NOT EXISTS (SELECT FROM game.item_stacks s WHERE s.template_id = i.template_id) AND i.template_id <> 930100001;
SELECT 'mergeable split stacks (char, template)', count(*)::text FROM (
  SELECT character_id, template_id FROM game.item_instances i JOIN game.item_stacks s USING (template_id)
  WHERE location = 'bag' GROUP BY 1, 2, s.max_stack HAVING count(*) > 1 AND sum(quantity) <= s.max_stack) x;
SELECT 'grants pointing at missing items', count(*)::text FROM game.item_grants g
  WHERE NOT EXISTS (SELECT FROM game.item_instances i WHERE i.instance_id = g.instance_id);
SELECT 'slot_extends settings', count(*)::text FROM game.character_settings WHERE name = 'slot_extends';
"""):
    print(f'  {name}: {value}')

section('expired or growing data')
for name, value in query("""
SELECT 'web sessions (expired)', count(*)::text || ' (' || count(*) FILTER (WHERE expires_at < now())::text || ')' FROM auth.sessions;
SELECT 'game tickets (expired)', count(*)::text || ' (' || count(*) FILTER (WHERE expires_at < now())::text || ')' FROM auth.game_tickets;
SELECT 'client launches (expired)', count(*)::text || ' (' || count(*) FILTER (WHERE expires_at < now())::text || ')' FROM auth.client_launches;
SELECT 'client handoffs (expired)', count(*)::text || ' (' || count(*) FILTER (WHERE expires_at < now())::text || ')' FROM auth.client_handoffs;
SELECT 'world sessions (expired)', count(*)::text || ' (' || count(*) FILTER (WHERE expires_at < now())::text || ')' FROM auth.world_sessions;
SELECT 'chat log (older than 90 days)', count(*)::text || ' (' || count(*) FILTER (WHERE sent_at < now() - interval '90 days')::text || ')' FROM game.chat_log;
SELECT 'audit events (older than 180 days)', count(*)::text || ' (' || count(*) FILTER (WHERE occurred_at < now() - interval '180 days')::text || ')' FROM audit.events;
SELECT 'population samples (oldest)', count(*)::text || ' (' || coalesce(min(sampled_at)::date::text, '-') || ')' FROM game.population_samples;
"""):
    print(f'  {name}: {value}')

section('backups')
folder = Path('/var/lib/aion/backups')   # update.sh keeps the newest 10 here (deploy/linux/README.md)
backups = sorted(folder.glob('*'), key=lambda p: p.stat().st_mtime) if folder.exists() else []
print(f'  {len(backups)} in {folder}' + (f', newest {backups[-1].name}' if backups else ' (no backup has ever been made)'))
