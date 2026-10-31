-- persisted daily/weekly reset cursors per world host (idempotent scheduler).
CREATE TABLE game.world_reset_cursors (
    world_id integer PRIMARY KEY,
    daily_boundary_ms bigint NOT NULL DEFAULT 0,
    weekly_boundary_ms bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now()
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.world_reset_cursors TO aion_app;
