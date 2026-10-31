-- Godstone collection (GodstoneList_NT / client codex): registered template ids and counts.
CREATE TABLE game.character_godstones (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    godstone_id integer NOT NULL,
    count integer NOT NULL DEFAULT 0 CHECK (count >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, godstone_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_godstones TO aion_app;
