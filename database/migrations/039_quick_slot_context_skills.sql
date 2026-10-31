-- Hotbar context skills (QuickSlotInfo._context_skill_ids slots 1-3; slot 0 stays the main skill_id).
ALTER TABLE game.quick_slots
    ADD COLUMN IF NOT EXISTS context_skill_1 integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS context_skill_2 integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS context_skill_3 integer NOT NULL DEFAULT 0;
