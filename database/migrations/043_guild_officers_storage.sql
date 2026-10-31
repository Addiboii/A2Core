-- Legion officers, applications, warehouse, wallet and attendance (ported from A2Core PR #4, Artezianec, migrations
-- 044/046/049/084, with its faults fixed). The warehouse keeps whole item instances: a stored item stays a row of
-- game.item_instances (location 'guild_storage', character_id the one who stored it) so enchant, godstones, rolled stats
-- and binding survive; game.guild_storage_items holds which legion and slot. Bound items are refused by the store.
ALTER TABLE game.item_instances DROP CONSTRAINT item_instances_location_check;
ALTER TABLE game.item_instances ADD CONSTRAINT item_instances_location_check
    CHECK (location IN ('bag','equipment','storage','buyback','guild_storage','trade_escrow'));

CREATE TABLE game.guild_join_requests (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    comment text NOT NULL DEFAULT '' CHECK (length(comment) <= 100),
    requested_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, guild_id)
);
CREATE INDEX guild_join_requests_guild_idx ON game.guild_join_requests (guild_id);

ALTER TABLE game.guilds
    ADD COLUMN storage_extensions integer NOT NULL DEFAULT 0 CHECK (storage_extensions >= 0),
    ADD COLUMN storage_revision bigint NOT NULL DEFAULT 1,
    ADD COLUMN storage_sorted_ms bigint NOT NULL DEFAULT 0,
    ADD COLUMN attendance_day date,
    ADD COLUMN attendance_today integer NOT NULL DEFAULT 0 CHECK (attendance_today >= 0),
    ADD COLUMN attendance_yesterday integer NOT NULL DEFAULT 0 CHECK (attendance_yesterday >= 0);

-- Game days (attendance, donations) start at 07:00 UTC, as the shop limits (ShopTables.Period); the server passes the day.
ALTER TABLE game.guild_members
    ADD COLUMN attendance_day date,
    ADD COLUMN donation_day date,
    ADD COLUMN donations integer NOT NULL DEFAULT 0 CHECK (donations >= 0);

CREATE TABLE game.guild_storage_items (
    instance_id uuid PRIMARY KEY REFERENCES game.item_instances(instance_id) ON DELETE CASCADE,
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    slot integer NOT NULL CHECK (slot >= 0),
    UNIQUE (guild_id, slot)
);

-- A disbanded legion's warehouse goes back to the bags of those who stored the items (OURS: the client data has no rule).
CREATE FUNCTION game.return_guild_storage() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    UPDATE game.item_instances SET location = 'bag', slot = NULL
    WHERE instance_id IN (SELECT instance_id FROM game.guild_storage_items WHERE guild_id = OLD.guild_id);
    RETURN OLD;
END $$;
CREATE TRIGGER guild_storage_return BEFORE DELETE ON game.guilds
FOR EACH ROW EXECUTE FUNCTION game.return_guild_storage();
REVOKE ALL ON FUNCTION game.return_guild_storage() FROM PUBLIC;

CREATE TABLE game.guild_storage_history (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    character_id uuid REFERENCES game.characters(character_id) ON DELETE SET NULL,
    char_name text NOT NULL DEFAULT '',
    record_type smallint NOT NULL,
    template_id integer NOT NULL,
    quantity bigint NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX guild_storage_history_idx ON game.guild_storage_history (guild_id, recorded_at DESC);

-- EGuildWalletType 1 EssenceGuild. Changed only by amount = amount + delta; never below zero.
CREATE TABLE game.guild_wallet (
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    wallet_type smallint NOT NULL CHECK (wallet_type > 0),
    amount bigint NOT NULL DEFAULT 0 CHECK (amount >= 0),
    PRIMARY KEY (guild_id, wallet_type)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.guild_join_requests, game.guild_storage_items, game.guild_storage_history, game.guild_wallet TO aion_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA game TO aion_app;
