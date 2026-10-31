-- CraftLimitList periodic counters (server/account/character scopes via owner_id + target).
CREATE TABLE game.character_craft_limits (
    owner_id uuid NOT NULL,
    target smallint NOT NULL CHECK (target BETWEEN 1 AND 3),
    recipe_id integer NOT NULL,
    period text NOT NULL,
    count integer NOT NULL CHECK (count > 0),
    PRIMARY KEY (owner_id, target, recipe_id, period)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_craft_limits TO aion_app;
