-- The equipped wing (Wing.dat id), chosen with WingChange_RQ 0x5674 and kept across logins and map loads.
-- One row per character; no row means the default: the race's default wing for staff and ascended characters,
-- else none (the default wing is implied and never stored, as in 012). A stored wing must be a registered one
-- (game.character_wings); removing that registration removes the choice with it. Additive: no existing row changes.
CREATE TABLE game.character_wing_equips (
    character_id uuid PRIMARY KEY,
    wing_id integer NOT NULL CHECK (wing_id > 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (character_id, wing_id) REFERENCES game.character_wings(character_id, wing_id) ON DELETE CASCADE
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_wing_equips TO aion_app;
