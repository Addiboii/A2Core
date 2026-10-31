-- Instance / dungeon lockouts (DungeonCoolTimes_NT); until_ms is Unix epoch ms.
CREATE TABLE game.character_dungeon_cooltimes (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    dungeon_id integer NOT NULL,
    until_ms bigint NOT NULL,
    PRIMARY KEY (character_id, dungeon_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_dungeon_cooltimes TO aion_app;
