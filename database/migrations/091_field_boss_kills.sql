-- Field bosses (feature/field-boss-map 2026-11-11): the last kill of each world map field boss (client WorldMapFieldNamed: map id +
-- data id) and when it comes back (Unix ms), shared by the whole host, so a restart neither revives a killed period boss (the Abyss
-- bosses that appear at PeriodSpawn times) nor forgets a respawn time. The newer kill wins.
CREATE TABLE game.field_boss_kills (
    map_id integer NOT NULL,
    data_id integer NOT NULL,
    killed_at bigint NOT NULL,
    respawn_at bigint NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (map_id, data_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.field_boss_kills TO aion_app;
