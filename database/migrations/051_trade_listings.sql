-- Broker / trade server listings (Global pin build 25767555): item escrow until sold, cancelled or expired.
CREATE TABLE game.trade_listings (
    sale_id bigserial PRIMARY KEY,
    seller_character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    item_instance_id uuid NOT NULL REFERENCES game.item_instances(instance_id) ON DELETE CASCADE,
    item_wire_key bigint NOT NULL,
    template_id integer NOT NULL,
    quantity bigint NOT NULL CHECK (quantity > 0),
    price_kina bigint NOT NULL CHECK (price_kina > 0),
    trade_server_id integer NOT NULL DEFAULT 83,
    listed_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    cancelled boolean NOT NULL DEFAULT false,
    sold boolean NOT NULL DEFAULT false
);
-- Expiry is enforced in queries; partial indexes cannot use now() (not IMMUTABLE).
CREATE INDEX trade_listings_active_idx ON game.trade_listings (trade_server_id, template_id, expires_at) WHERE NOT cancelled AND NOT sold;
CREATE INDEX trade_listings_seller_idx ON game.trade_listings (seller_character_id) WHERE NOT cancelled AND NOT sold;
GRANT SELECT, INSERT, UPDATE, DELETE ON game.trade_listings TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.trade_listings_sale_id_seq TO aion_app;

ALTER TABLE game.item_instances DROP CONSTRAINT item_instances_location_check;
ALTER TABLE game.item_instances ADD CONSTRAINT item_instances_location_check
    CHECK (location IN ('bag','equipment','storage','buyback','trade_escrow'));
