-- legion level EXP, member contribution (donation/reputation), legion hall (agit) contract + decoration.
ALTER TABLE game.guilds
    ADD COLUMN agit_id integer NOT NULL DEFAULT 0,
    ADD COLUMN agit_end_ms bigint NOT NULL DEFAULT 0;

ALTER TABLE game.guild_members
    ADD COLUMN reputation bigint NOT NULL DEFAULT 0,
    ADD COLUMN weekly_reputation bigint NOT NULL DEFAULT 0,
    ADD COLUMN donation_reputation bigint NOT NULL DEFAULT 0,
    ADD COLUMN weekly_donation_reputation bigint NOT NULL DEFAULT 0,
    ADD COLUMN donations_today smallint NOT NULL DEFAULT 0,
    ADD COLUMN donation_day date;

CREATE TABLE game.guild_agit_deco (
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    deco_id integer NOT NULL,
    slot_id integer NOT NULL DEFAULT 0,
    PRIMARY KEY (guild_id, deco_id)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.guild_agit_deco TO aion_app;
