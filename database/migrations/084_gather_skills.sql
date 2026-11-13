-- Gather (Essence Extraction) skills (feature/flight-gathering 2026-11-10): the learned levels of the client's GatherSkill tree
-- live with the masteries as kind 'gather_skill', category = GatherSkill id / 100 (100 .. 71200 -> 1 .. 712), level 0 = unlearned
-- (a reset keeps the row at 0). The points are not stored: GatherMastery gives them by mastery level.
ALTER TABLE game.character_masteries
    DROP CONSTRAINT IF EXISTS character_masteries_kind_check,
    DROP CONSTRAINT IF EXISTS character_masteries_category_check,
    DROP CONSTRAINT IF EXISTS character_masteries_level_check,
    ADD CONSTRAINT character_masteries_kind_check CHECK (kind IN ('craft', 'gather', 'gather_skill')),
    ADD CONSTRAINT character_masteries_category_check CHECK (
        (kind IN ('craft', 'gather') AND category BETWEEN 0 AND 5) OR (kind = 'gather_skill' AND category BETWEEN 1 AND 999)),
    ADD CONSTRAINT character_masteries_level_check CHECK (
        (kind IN ('craft', 'gather') AND level BETWEEN 1 AND 100) OR (kind = 'gather_skill' AND level BETWEEN 0 AND 100));
