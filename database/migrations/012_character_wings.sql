-- Registered wings (Wing.dat ids; flight needs one equipped). Added by staff commands
-- (admin command "wings", builder AcquireWingAll); read at character selection. The
-- default wing of staff and ascended characters is implied and not stored.
CREATE TABLE game.character_wings (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    wing_id integer NOT NULL CHECK (wing_id > 0),
    enchant_level smallint NOT NULL DEFAULT 0 CHECK (enchant_level BETWEEN 0 AND 255),
    acquired_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, wing_id)
);
GRANT SELECT, INSERT ON game.character_wings TO aion_app;
