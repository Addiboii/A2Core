-- Creature boards (one per ECreatureType 2 Intellect .. 6 Special): level and exp from acquiring and
-- levelling rides of that type (VehicleLevel GetCreatureExp), and the stat slot panels rolled with soul
-- crystals and Kina (VehicleCreatureSlot / VehicleCreatureOption). panels: {"1": [[slot, grade, stat, value,
-- locked], ...], "2": ..., "3": ...}. Sent at world entry in 0x9000 RideInitData_NT.
CREATE TABLE game.character_creature_boards (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    creature_type smallint NOT NULL CHECK (creature_type BETWEEN 2 AND 6),
    level smallint NOT NULL DEFAULT 1 CHECK (level BETWEEN 1 AND 100),
    exp bigint NOT NULL DEFAULT 0 CHECK (exp >= 0),
    panels jsonb NOT NULL DEFAULT '{}',
    PRIMARY KEY (character_id, creature_type)
);
GRANT SELECT, INSERT, UPDATE ON game.character_creature_boards TO aion_app;
