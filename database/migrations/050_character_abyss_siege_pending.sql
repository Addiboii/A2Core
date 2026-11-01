-- Offline fortress siege AP and rift PvP opt-in (Abyss character row).
ALTER TABLE game.character_abyss
    ADD COLUMN IF NOT EXISTS pending_siege_ap bigint NOT NULL DEFAULT 0 CHECK (pending_siege_ap >= 0),
    ADD COLUMN IF NOT EXISTS rift_pvp_on smallint NOT NULL DEFAULT 0 CHECK (rift_pvp_on IN (0, 1));
