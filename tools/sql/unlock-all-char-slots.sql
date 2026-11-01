-- Set extended_character_slots to the Global client maximum (GlobalSetting lobby_pc_slot_max_extended_count).
-- Requires migration 087. Reconnect to the game server after running.
--
-- Usage: psql "$DATABASE_URL" -v login=<login> -f tools/sql/unlock-all-char-slots.sql
UPDATE auth.accounts AS a
SET extended_character_slots = 4
FROM auth.local_credentials AS c
WHERE c.account_id = a.account_id
  AND a.disabled_at IS NULL
  AND lower(c.username) = lower(:'login');
