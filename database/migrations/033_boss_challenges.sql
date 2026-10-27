-- Boss challenges (BossChallengeGroup.dat ids): the highest level cleared and the best clear per character.
CREATE TABLE game.character_boss_challenges (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    group_id integer NOT NULL,
    clear_level integer NOT NULL DEFAULT 0 CHECK (clear_level >= 0),
    best_point bigint NOT NULL DEFAULT 0,
    best_level integer NOT NULL DEFAULT 0,
    best_time_ms integer NOT NULL DEFAULT 0,
    cumulative_point bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, group_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_boss_challenges TO aion_app;
