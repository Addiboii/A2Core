-- Small per-character settings stored as JSON by name: earned emotes (social_actions), the seal stone toggle
-- (seal_stone), auto-potion slots (potion_auto_use), auto-use items (item_auto_use) and presets (presets).
CREATE TABLE game.character_settings (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    name text NOT NULL CHECK (name ~ '^[a-z_]{1,32}$'),
    value jsonb NOT NULL,
    PRIMARY KEY (character_id, name)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_settings TO aion_app;
