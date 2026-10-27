-- Quest progress per character (tools/data/quests.json codes, e.g. HQ1101010). status is
-- EQuestStatus (2 Incomplete, 3 Complete, 4 Rewarded); step is the 1-based QuestStep order;
-- goals are the per-objective counters of that step (Common_Quest _goal1.._goal6). uid is
-- the quest_uid the client echoes in give-up/reward/conversation requests.
CREATE TABLE game.character_quests (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    quest_code text NOT NULL CHECK (quest_code ~ '^[A-Za-z0-9_]{1,64}$'),
    quest_id integer NOT NULL CHECK (quest_id > 0),
    uid bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
    status smallint NOT NULL CHECK (status BETWEEN 1 AND 4),
    step smallint NOT NULL DEFAULT 1 CHECK (step BETWEEN 1 AND 64),
    goals integer[] NOT NULL DEFAULT '{0,0,0,0,0,0}' CHECK (cardinality(goals) = 6),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, quest_code)
);
-- Reward grants are idempotent per (quest uid, item index) through game.grant_item's request id.
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_quests TO aion_app;
