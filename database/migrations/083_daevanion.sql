-- Daevanion (client tables DaevanionBoard / DaevanionNode): the nodes a character learned with Daevanion points (the Start node
-- of each open board is auto-learned and not stored), and the points Daevanion items added per EDaevanionPointType
-- (1 DaevanionCrystal, 2 ConquestCrystal, 3 BattleCrystal, 4 Season02Crystal01, 5 Season03Crystal01). Level points are not
-- stored: they follow the level (Exp DaevanionPointMap).
CREATE TABLE game.character_daevanion_nodes (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    node_id integer NOT NULL CHECK (node_id > 0),
    learned_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, node_id)
);
GRANT SELECT, INSERT, DELETE ON game.character_daevanion_nodes TO aion_app;

CREATE TABLE game.character_daevanion_points (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    point_type smallint NOT NULL CHECK (point_type BETWEEN 1 AND 5),
    bonus integer NOT NULL DEFAULT 0 CHECK (bonus BETWEEN 0 AND 65535),
    PRIMARY KEY (character_id, point_type)
);
GRANT SELECT, INSERT, UPDATE ON game.character_daevanion_points TO aion_app;
