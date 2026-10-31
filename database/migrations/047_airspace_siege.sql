-- fortress siege (AirspaceBattle) ownership and legion applications per world host.
CREATE TABLE game.airspace_owner (
    world_id integer NOT NULL,
    airspace_id integer NOT NULL,
    guild_id bigint NOT NULL REFERENCES game.guilds (guild_id) ON DELETE CASCADE,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (world_id, airspace_id)
);

CREATE TABLE game.airspace_battle_apply (
    world_id integer NOT NULL,
    battle_id integer NOT NULL,
    airspace_id integer NOT NULL,
    guild_id bigint NOT NULL REFERENCES game.guilds (guild_id) ON DELETE CASCADE,
    applied_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (world_id, battle_id, airspace_id, guild_id)
);

CREATE INDEX airspace_battle_apply_world_idx ON game.airspace_battle_apply (world_id, battle_id, airspace_id);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.airspace_owner, game.airspace_battle_apply TO aion_app;
