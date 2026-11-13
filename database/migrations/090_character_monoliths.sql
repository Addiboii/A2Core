-- Monoliths (client tables Monolith / MonolithLevel / RewardMonolith; feature/empyrean-traces 2026-11-11): how many Empyrean Trace
-- tokens a character has resonated with each Monolith (1001 Verteron, 1002 Eltnen, 2001 Altgard, 2002 Morheim, 3001/4001 Reshanta).
-- The level and its stats follow from the count (MonolithLevel NeedTokenCount, cumulative); the collected traces themselves are
-- their UseEnvObjSpawnerName achievements (stored with the other achievements).
CREATE TABLE game.character_monoliths (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    monolith_id integer NOT NULL CHECK (monolith_id > 0),
    resonate_count integer NOT NULL CHECK (resonate_count >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, monolith_id)
);
GRANT SELECT, INSERT, UPDATE ON game.character_monoliths TO aion_app;
