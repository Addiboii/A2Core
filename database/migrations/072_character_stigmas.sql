-- Stigma skills (tools/data/stigmas.json: SkillAcquireData Stigma rows of build 26262663). A row is a stigma the character
-- learned with shards and the level it reached; which stigma sits in a stigma quick slot (QuickSlotData 21-24) stays in
-- game.quick_slots. Only the server's learn/level requests write a row (compare-and-set on the previous level); nothing at
-- login lowers or deletes one, even when the character's shards no longer cover it.
CREATE TABLE game.character_stigmas (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    skill_id integer NOT NULL CHECK (skill_id > 0),
    level integer NOT NULL CHECK (level BETWEEN 1 AND 255),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, skill_id)
);

-- Shards from GetStigmaShard items (Item.dat UseEffectValues), on top of the Exp.dat total of the character's level. Written
-- together with the item's consumption.
CREATE TABLE game.character_stigma_shards (
    character_id uuid PRIMARY KEY REFERENCES game.characters(character_id) ON DELETE CASCADE,
    item_shards integer NOT NULL DEFAULT 0 CHECK (item_shards >= 0),
    updated_at timestamptz NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_stigmas TO aion_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_stigma_shards TO aion_app;
