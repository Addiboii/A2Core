-- Titles (Title.dat ids): owned, one equipped per ETitleEquipCategory (1 Attack, 2 Defense, 3 Etc) and the one shown
-- (category 0). Skins (Skin.dat ids): the collection with counts and the equipped skin per ESkinEquipSlot. Craft and
-- gather masteries: level and exp per kind ('craft' with ECraftCategory 1..5, 'gather' with category 0).
CREATE TABLE game.character_titles (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    title_id integer NOT NULL CHECK (title_id > 0),
    acquired_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, title_id)
);
CREATE TABLE game.character_title_equips (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    category smallint NOT NULL CHECK (category BETWEEN 0 AND 3),
    title_id integer NOT NULL CHECK (title_id > 0),
    PRIMARY KEY (character_id, category)
);
CREATE TABLE game.character_skins (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    skin_id integer NOT NULL CHECK (skin_id > 0),
    count integer NOT NULL DEFAULT 1 CHECK (count > 0),
    acquired_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, skin_id)
);
CREATE TABLE game.character_skin_equips (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    slot smallint NOT NULL CHECK (slot BETWEEN 1 AND 27),
    skin_id integer NOT NULL CHECK (skin_id > 0),
    PRIMARY KEY (character_id, slot)
);
CREATE TABLE game.character_masteries (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    kind text NOT NULL CHECK (kind IN ('craft', 'gather')),
    category smallint NOT NULL CHECK (category BETWEEN 0 AND 5),
    level integer NOT NULL DEFAULT 1 CHECK (level BETWEEN 1 AND 100),
    exp bigint NOT NULL DEFAULT 0 CHECK (exp >= 0),
    PRIMARY KEY (character_id, kind, category)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_titles, game.character_title_equips, game.character_skins,
    game.character_skin_equips, game.character_masteries TO aion_app;
