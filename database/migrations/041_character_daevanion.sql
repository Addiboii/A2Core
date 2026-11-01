-- Daevanion activated nodes and point pools (board progress, crystal spend).
CREATE TABLE game.character_daevanion_nodes (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    node_id integer NOT NULL,
    activated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, node_id)
);

CREATE TABLE game.character_daevanion_points (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    point_type smallint NOT NULL CHECK (point_type BETWEEN 1 AND 5),
    used_points integer NOT NULL DEFAULT 0 CHECK (used_points >= 0),
    bonus_points integer NOT NULL DEFAULT 0 CHECK (bonus_points >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, point_type)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_daevanion_nodes TO aion_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_daevanion_points TO aion_app;
