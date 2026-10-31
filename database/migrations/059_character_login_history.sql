-- Per-character lobby / login-port history (LobbyCharDesc._logout_time, ListMyCharacters last_played_unix_ms).
ALTER TABLE game.characters
    ADD COLUMN IF NOT EXISTS last_login_at timestamptz,
    ADD COLUMN IF NOT EXISTS last_logout_at timestamptz;

UPDATE game.characters
SET last_logout_at = COALESCE(last_logout_at, updated_at),
    last_login_at = COALESCE(last_login_at, last_logout_at, created_at)
WHERE last_logout_at IS NULL OR last_login_at IS NULL;
