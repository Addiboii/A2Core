-- Broker (ported from A2Core PR #4, Artezianec, migrations 051/052/057, rewritten). A listing holds its items in escrow:
-- the listed units are their own game.item_instances row (location 'trade_escrow'; part of a stack is split off, never
-- copied). A purchase takes count units at the stored unit price (count x price), moves them to the buyer and owes the
-- seller a delivery (Kina, bigint). Cancelling needs the seller's own listing, neither sold nor cancelled; an expired
-- listing is taken back the same way.
CREATE TABLE game.trade_listings (
    sale_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    seller_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    instance_id uuid REFERENCES game.item_instances(instance_id) ON DELETE SET NULL,
    template_id integer NOT NULL,
    quantity bigint NOT NULL CHECK (quantity >= 0),
    unit_price bigint NOT NULL CHECK (unit_price > 0),
    listed_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    sold boolean NOT NULL DEFAULT false,
    cancelled boolean NOT NULL DEFAULT false,
    CHECK (NOT (sold AND cancelled)),
    CHECK (sold OR cancelled OR (quantity > 0 AND instance_id IS NOT NULL))
);
CREATE INDEX trade_listings_open_idx ON game.trade_listings (template_id, unit_price) WHERE NOT sold AND NOT cancelled;
CREATE INDEX trade_listings_seller_idx ON game.trade_listings (seller_id) WHERE NOT sold AND NOT cancelled;

CREATE TABLE game.trade_deliveries (
    delivery_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    seller_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    sale_id bigint NOT NULL REFERENCES game.trade_listings(sale_id) ON DELETE CASCADE,
    kina bigint NOT NULL CHECK (kina >= 0),
    template_id integer NOT NULL,
    quantity bigint NOT NULL CHECK (quantity > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz
);
CREATE INDEX trade_deliveries_open_idx ON game.trade_deliveries (seller_id) WHERE completed_at IS NULL;

GRANT SELECT, INSERT, UPDATE, DELETE ON game.trade_listings, game.trade_deliveries TO aion_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA game TO aion_app;
