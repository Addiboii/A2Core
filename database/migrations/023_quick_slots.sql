-- The player's hotbar (QuickSlotSet_RQ 0x5601, live: 27 bytes = QuickSlotInfo): per slot a skill or an
-- item (EQuickSlotType 0 Skill, 1 Item, ...). Sent at world entry instead of the class's default bar.
CREATE TABLE game.quick_slots (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    slot smallint NOT NULL CHECK (slot BETWEEN 0 AND 255),
    slot_type smallint NOT NULL CHECK (slot_type BETWEEN 0 AND 4),
    item_uid bigint NOT NULL DEFAULT 0,
    item_id integer NOT NULL DEFAULT 0,
    skill_id integer NOT NULL DEFAULT 0,
    PRIMARY KEY (character_id, slot)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.quick_slots TO aion_app;
