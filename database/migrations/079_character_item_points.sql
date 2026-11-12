-- Points granted by items (tools/data/boxes.json uses): GetSkillPoint (category 1, ESkillPointType Point = type 1: the Wisdom
-- Stone) and GetDaevanionPoint (category 2, EDaevanionPointType 1-5: the crystals). Written together with the item's
-- consumption; the totals are what UpdateItemSkillPoint_NT 0x3811 and UpdateItemDaevanionPoint_NT 0xe22f carry.
-- Stigma shards from items stay in game.character_stigma_shards (migration 072).
CREATE TABLE game.character_item_points (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    category smallint NOT NULL CHECK (category IN (1, 2)),
    point_type smallint NOT NULL CHECK (point_type BETWEEN 1 AND 15),
    points integer NOT NULL DEFAULT 0 CHECK (points BETWEEN 0 AND 65535),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, category, point_type)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_item_points TO aion_app;
