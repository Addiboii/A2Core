-- Substance Morph limits: per character, account, or game server (world) in a reset period (shop-style period keys).
CREATE TABLE game.conversion_counts (
    owner_id uuid NOT NULL,
    recipe_id integer NOT NULL,
    period text NOT NULL,
    count integer NOT NULL CHECK (count > 0),
    PRIMARY KEY (owner_id, recipe_id, period)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.conversion_counts TO aion_app;
