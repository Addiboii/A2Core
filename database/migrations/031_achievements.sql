-- Achievements (Achievement.dat ids): the count, when the last step completed, the highest step rewarded; and the group
-- rewards taken (AchievementGroupReward.dat group id, reward step).
CREATE TABLE game.character_achievements (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    achievement_id integer NOT NULL,
    count integer NOT NULL DEFAULT 0 CHECK (count >= 0),
    completed_at timestamptz,
    rewarded_step smallint NOT NULL DEFAULT 0 CHECK (rewarded_step >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, achievement_id)
);
CREATE TABLE game.character_group_rewards (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    group_id integer NOT NULL,
    step smallint NOT NULL CHECK (step >= 1),
    taken_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, group_id, step)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_achievements, game.character_group_rewards TO aion_app;
