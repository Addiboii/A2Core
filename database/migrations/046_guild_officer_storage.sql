-- Legion officer flows, attendance, wallet, storage, owned agit decoration.

CREATE TABLE game.guild_join_requests (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    comment text NOT NULL DEFAULT '',
    requested_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, guild_id)
);
CREATE INDEX guild_join_requests_guild_idx ON game.guild_join_requests (guild_id);

ALTER TABLE game.guilds
    ADD COLUMN today_attendance_count integer NOT NULL DEFAULT 0,
    ADD COLUMN yesterday_attendance_count integer NOT NULL DEFAULT 0,
    ADD COLUMN attendance_day date,
    ADD COLUMN authority jsonb NOT NULL DEFAULT '[]'::jsonb,
    ADD COLUMN storage_revision bigint NOT NULL DEFAULT 1,
    ADD COLUMN storage_extension_count integer NOT NULL DEFAULT 0;

ALTER TABLE game.guild_members
    ADD COLUMN last_attendance_day date,
    ADD COLUMN attendance_reward_day date;

CREATE TABLE game.guild_wallet (
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    wallet_type integer NOT NULL,
    amount bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (guild_id, wallet_type)
);

CREATE TABLE game.guild_agit_deco_owned (
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    deco_id integer NOT NULL,
    PRIMARY KEY (guild_id, deco_id)
);

CREATE TABLE game.guild_storage_items (
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    item_key bigint NOT NULL,
    template_id integer NOT NULL,
    quantity bigint NOT NULL CHECK (quantity > 0),
    slot_pos integer NOT NULL DEFAULT 0,
    revision bigint NOT NULL DEFAULT 1,
    PRIMARY KEY (guild_id, item_key)
);
CREATE INDEX guild_storage_guild_slot_idx ON game.guild_storage_items (guild_id, slot_pos);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.guild_join_requests, game.guild_wallet, game.guild_agit_deco_owned, game.guild_storage_items TO aion_app;
