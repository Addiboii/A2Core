-- Push rewards (gift box): global rewards and per-character take records.
CREATE TABLE game.push_rewards (
    push_reward_id serial PRIMARY KEY,
    reward_type smallint NOT NULL CHECK (reward_type >= 0),
    sender text NOT NULL,
    title text NOT NULL,
    body text NOT NULL,
    item_template_id integer NOT NULL,
    item_count bigint NOT NULL CHECK (item_count > 0),
    sent_at timestamptz NOT NULL DEFAULT now(),
    expire_at timestamptz NOT NULL
);
CREATE TABLE game.character_push_rewards (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    push_reward_id integer NOT NULL REFERENCES game.push_rewards(push_reward_id) ON DELETE CASCADE,
    taken_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, push_reward_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.push_rewards TO aion_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_push_rewards TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.push_rewards_push_reward_id_seq TO aion_app;

-- OBSERVED on the official server at launch: six gifts of one item each (two launch gifts for 70 days,
-- one weekly and three daily gifts for 7 days). Item ids match event_Box_Push_Reward_* rows in tools/data/boxes.json.
INSERT INTO game.push_rewards (push_reward_id, reward_type, sender, title, body, item_template_id, item_count, expire_at) VALUES
    (1, 1, 'AION', 'Launch Gift I', 'Welcome.', 533460001, 500, now() + interval '70 days'),
    (2, 1, 'AION', 'Launch Gift II', 'Welcome.', 532760001, 1, now() + interval '70 days'),
    (3, 1, 'AION', 'Weekly Gift', 'Weekly reward.', 531060026, 3, now() + interval '7 days'),
    (4, 2, 'AION', 'Daily Gift I', 'Daily reward.', 530116006, 3, now() + interval '7 days'),
    (5, 2, 'AION', 'Daily Gift II', 'Daily reward.', 503700031, 1, now() + interval '7 days'),
    (6, 2, 'AION', 'Daily Gift III', 'Daily reward.', 530116001, 10, now() + interval '7 days');
SELECT setval('game.push_rewards_push_reward_id_seq', (SELECT MAX(push_reward_id) FROM game.push_rewards));
