-- Black Cloud Merchants (the client's random shop; feature/black-cloud-merchants 2026-11-10): each character's rolled list per shop
-- (category = the wire ERandomShopCategory: 101-106 Elyos, 201-206 Asmodians). state = {until: ms the list renews, goods:
-- [[goods id, discount index, bought 0/1], ..], day: the paid refresh day, refreshes: paid refreshes that day}. revision guards
-- a slot against being bought twice by two logins of one character.
CREATE TABLE game.character_random_shops (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    category smallint NOT NULL CHECK (category BETWEEN 101 AND 299),
    revision bigint NOT NULL CHECK (revision > 0),
    state jsonb NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, category)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_random_shops TO aion_app;
