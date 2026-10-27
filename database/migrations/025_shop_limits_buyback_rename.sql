-- NPC shop purchase limits: goods bought per owner (a character, or the account for ServerAccount limits) in a
-- reset period ('all', a daily date, a weekly start date or a month), as GameShopGoods ItemLimitCount counts them.
CREATE TABLE game.shop_purchases (
    owner_id uuid NOT NULL,
    goods_id integer NOT NULL,
    period text NOT NULL,
    count integer NOT NULL CHECK (count > 0),
    PRIMARY KEY (owner_id, goods_id, period)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.shop_purchases TO aion_app;

-- Buyback: an item sold to a merchant waits in location 'buyback' (upgrades.sold_at ms, upgrades.sold_for Kina)
-- until it is restored, expires (72 hours) or is pushed out (50 per character).
ALTER TABLE game.item_instances DROP CONSTRAINT item_instances_location_check;
ALTER TABLE game.item_instances ADD CONSTRAINT item_instances_location_check CHECK (location IN ('bag','equipment','storage','buyback'));

-- Renaming: the last rename (a Name Change Voucher each, then 168 hours locked).
ALTER TABLE game.characters ADD COLUMN last_renamed_at timestamptz;
