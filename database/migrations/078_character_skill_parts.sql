-- Skill specialisation (tools/data/specialized-skills.json: SpecializedSkillParts/Slot.dat of build 26262663). A row is a
-- part the character equipped on a mastery skill's slot (1-3, opening at skill level 8/12/20). Only the server's
-- equip/unequip/swap requests write rows, replacing a skill's whole set by compare-and-set; nothing at login removes one,
-- even when the skill's level no longer opens its slot. Stigma parts are not stored: they follow the stigma's level.
CREATE TABLE game.character_skill_parts (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    skill_id integer NOT NULL CHECK (skill_id > 0),
    slot smallint NOT NULL CHECK (slot BETWEEN 1 AND 5),
    part_id integer NOT NULL CHECK (part_id > 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, skill_id, slot),
    UNIQUE (character_id, part_id)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_skill_parts TO aion_app;
