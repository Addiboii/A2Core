-- Four worlds (Stage H, feature/four-worlds 2026-11-12): two Light worlds (1361, 1362) and two Dark worlds (2361, 2362) on this
-- host, one race each as every official server (ServerName.dat: Light 1xxx, Dark 2xxx). game.characters.world_id stays the
-- host's realm (1204: tickets, chat, world sessions); a character's world is its home_server_id, the server id the client
-- shows and lists it under (Common_ServerInfo / ListMyCharacters / MyCharAppear _server_id).
-- Existing characters stay on the worlds they were listed on until now (by race: Light 1361, Dark 2361), which become world 1
-- of their faction. NULL (characters made outside the login port) means world 1 of the character's race.
ALTER TABLE game.characters ADD COLUMN home_server_id integer CHECK (home_server_id BETWEEN 1 AND 65535);
UPDATE game.characters SET home_server_id = CASE WHEN race = 2 THEN 2361 ELSE 1361 END WHERE home_server_id IS NULL;
CREATE INDEX characters_home_server_idx ON game.characters(home_server_id);
-- Lobby slots are per world (each world's lobby has its own slots, as the client numbers them per server).
DROP INDEX game.character_lobby_slot_idx;
CREATE UNIQUE INDEX character_lobby_slot_idx ON game.characters(account_id,world_id,home_server_id,lobby_slot) WHERE lobby_slot IS NOT NULL;
