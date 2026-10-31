-- ContentsReset reward limit counters (RewardLimitUpdate_NT / Reset_NT).
CREATE TABLE game.character_reward_limits (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    limit_type integer NOT NULL,
    period text NOT NULL,
    count integer NOT NULL CHECK (count >= 0),
    PRIMARY KEY (character_id, limit_type, period)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_reward_limits TO aion_app;
