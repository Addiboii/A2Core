-- The listed worlds are one race each, both served by this game world: an account's Light and Dark characters each
-- have their own lobby slots (the Dark world's lobby starts empty even when the Light one is full).
DROP INDEX game.character_lobby_slot_idx;
CREATE UNIQUE INDEX character_lobby_slot_idx ON game.characters(account_id,world_id,race,lobby_slot) WHERE lobby_slot IS NOT NULL;
