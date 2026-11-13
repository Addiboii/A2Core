-- Market (the client's Trade window, feature/systems-3 2026-11-10; migration 047, 042-046 left to the other groups).
-- One row per sale: the item left the seller's bag at TradeSell_RQ (a snapshot of the instance: template, count, rolled
-- stats and upgrades, so an enchanted item comes back as it was), the asking price in non-binding Kina (GlobalSetting
-- trade_buy_cost_currency_type GoldNonbinding) and the sale fee paid at listing (trade_sale_fee_tax_currency_type
-- GoldCombined). state: 'selling' -> 'sold' (TradePurchase_RQ: the buyer got the item, the seller's earnings wait in
-- earnings), 'cancelled' (TradeCancelSale_RQ, the item back to the seller), 'expired' (TradeExpireSale_RQ after
-- expires_at, the item back). Earnings are paid once (TradeDeliveryComplete_RQ, or the mail sent at the sale): paid_at.
CREATE TABLE game.market_sales (
    sale_id uuid PRIMARY KEY,
    seller_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    trade_server_id integer NOT NULL DEFAULT 0,
    build_id text NOT NULL,
    template_id integer NOT NULL,
    quantity bigint NOT NULL CHECK (quantity > 0),
    rolled_stats jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(rolled_stats) = 'object'),
    upgrades jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(upgrades) = 'object'),
    currency smallint NOT NULL DEFAULT 94,
    price bigint NOT NULL CHECK (price > 0),
    fee bigint NOT NULL DEFAULT 0 CHECK (fee >= 0),
    state text NOT NULL DEFAULT 'selling' CHECK (state IN ('selling','sold','cancelled','expired')),
    listed_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    buyer_id uuid REFERENCES game.characters(character_id) ON DELETE SET NULL,
    sold_at timestamptz,
    earnings bigint NOT NULL DEFAULT 0 CHECK (earnings >= 0),
    paid_at timestamptz,
    FOREIGN KEY (build_id, template_id) REFERENCES catalog.items
);
CREATE INDEX market_sales_selling_idx ON game.market_sales (template_id, price) WHERE state = 'selling';
CREATE INDEX market_sales_seller_idx ON game.market_sales (seller_id, state);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.market_sales TO aion_app;
