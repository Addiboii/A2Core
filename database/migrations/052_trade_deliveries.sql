-- Offline broker payouts: kina owed to the seller when a listing sells while they are logged out.
CREATE TABLE game.trade_deliveries (
    delivery_id bigserial PRIMARY KEY,
    seller_character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    kina bigint NOT NULL CHECK (kina >= 0),
    item_template_id integer NOT NULL,
    item_count bigint NOT NULL,
    trade_server_id integer NOT NULL DEFAULT 83,
    completed boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX trade_deliveries_pending_idx ON game.trade_deliveries (seller_character_id) WHERE NOT completed;
GRANT SELECT, INSERT, UPDATE, DELETE ON game.trade_deliveries TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.trade_deliveries_delivery_id_seq TO aion_app;
