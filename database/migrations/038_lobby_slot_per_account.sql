-- One race per account on this server again (an official server holds one race): lobby slots are unique per account and
-- world, as before 037.
DROP INDEX game.character_lobby_slot_idx;
CREATE UNIQUE INDEX character_lobby_slot_idx ON game.characters(account_id,world_id,lobby_slot) WHERE lobby_slot IS NOT NULL;
