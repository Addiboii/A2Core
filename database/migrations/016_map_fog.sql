-- World-map fog groups a character has uncovered (ESubzoneGroupID, tools/data/subzones.json): sent at
-- world entry as 0x9501 OpenSubzoneGroupIDs_NT; a new one is added when the character enters its subzone.
CREATE TABLE game.character_map_fog (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    group_id smallint NOT NULL CHECK (group_id BETWEEN 1 AND 1000),
    opened_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, group_id)
);
GRANT SELECT, INSERT ON game.character_map_fog TO aion_app;
