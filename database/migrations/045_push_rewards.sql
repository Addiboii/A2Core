-- Gift-box rewards (RetrievePushRewardInfo / TakePushReward; ported from A2Core PR #4, Artezianec, migration 088).
-- Rewards are added by staff; there is no seed (the PR's seed gave every character the official launch gifts).
CREATE TABLE game.push_rewards (
    push_reward_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    reward_type smallint NOT NULL CHECK (reward_type IN (1, 2)),   -- EPushRewardType 1 PushReward, 2 HotTimeReward
    sender text NOT NULL,
    title text NOT NULL,
    body text NOT NULL,
    item_template_id integer NOT NULL,
    item_count bigint NOT NULL CHECK (item_count > 0),
    sent_at timestamptz NOT NULL DEFAULT now(),
    expire_at timestamptz NOT NULL,
    CHECK (expire_at > sent_at)
);
CREATE TABLE game.character_push_rewards (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    push_reward_id integer NOT NULL REFERENCES game.push_rewards(push_reward_id) ON DELETE CASCADE,
    taken_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, push_reward_id)
);
GRANT SELECT ON game.push_rewards TO aion_app;
GRANT SELECT, INSERT ON game.character_push_rewards TO aion_app;
