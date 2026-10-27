-- Character deletion (CharPendingDelete_RQ 0x3627 / Cancel 0x3629). A deletion request sets pending_delete_at
-- (shown in the lobby's LobbyCharDesc _pending_delete_time); once due, the character moves to a disabled
-- graveyard account, loses its lobby slot and gets a suffixed name. Every owner-scoped character query
-- already ignores disabled accounts, so it vanishes everywhere while its rows (items, audit) stay intact.
ALTER TABLE game.characters ADD COLUMN pending_delete_at timestamptz;
INSERT INTO auth.accounts(account_id, provider, subject, disabled_at)
VALUES ('00000000-0000-0000-0000-00000000dead', 'system', 'deleted-characters', now())
ON CONFLICT DO NOTHING;
