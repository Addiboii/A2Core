-- Abyss entry time pool, weekly AP accrual and ranking points (Reshanta PvP economy).
CREATE TABLE game.character_abyss (
    character_id uuid PRIMARY KEY REFERENCES game.characters(character_id) ON DELETE CASCADE,
    entry_remaining_ms bigint NOT NULL DEFAULT 0 CHECK (entry_remaining_ms >= 0),
    ap_acc bigint NOT NULL DEFAULT 0 CHECK (ap_acc >= 0),
    ranking_point integer NOT NULL DEFAULT 0 CHECK (ranking_point >= 0),
    ranking_grade smallint NOT NULL DEFAULT 1 CHECK (ranking_grade >= 1),
    weekly_reset_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_abyss TO aion_app;
