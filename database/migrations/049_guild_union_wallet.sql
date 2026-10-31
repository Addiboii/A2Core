-- Legion union/ally diplomacy, storage history, wallet.

CREATE TABLE game.guild_unions (
    union_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name text NOT NULL,
    join_rule smallint NOT NULL DEFAULT 0,
    password text NOT NULL DEFAULT '',
    emblem_id integer NOT NULL DEFAULT 0,
    intro text NOT NULL DEFAULT '',
    notice text NOT NULL DEFAULT '',
    owner_guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT guild_unions_name_unique UNIQUE (name)
);

ALTER TABLE game.guilds
    ADD COLUMN union_id bigint REFERENCES game.guild_unions(union_id) ON DELETE SET NULL,
    ADD COLUMN storage_last_sort_ms bigint NOT NULL DEFAULT 0,
    ADD COLUMN storage_reserved_slots integer NOT NULL DEFAULT 0;

CREATE TABLE game.guild_relations (
    from_guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    to_guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    relation smallint NOT NULL,
    state smallint NOT NULL DEFAULT 0,
    requested_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (from_guild_id, to_guild_id, relation)
);
CREATE INDEX guild_relations_to_idx ON game.guild_relations (to_guild_id);

CREATE TABLE game.guild_union_join_requests (
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    union_id bigint NOT NULL REFERENCES game.guild_unions(union_id) ON DELETE CASCADE,
    requested_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (guild_id, union_id)
);
CREATE INDEX guild_union_join_requests_union_idx ON game.guild_union_join_requests (union_id);

CREATE TABLE game.guild_storage_history (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    character_id uuid REFERENCES game.characters(character_id) ON DELETE SET NULL,
    char_name text NOT NULL DEFAULT '',
    record_type smallint NOT NULL,
    template_id integer NOT NULL DEFAULT 0,
    quantity bigint NOT NULL DEFAULT 0,
    recorded_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX guild_storage_history_guild_time_idx ON game.guild_storage_history (guild_id, recorded_at DESC);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.guild_unions, game.guild_relations, game.guild_union_join_requests, game.guild_storage_history TO aion_app;
