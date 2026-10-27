-- Period item collections (PeriodItemCollection.dat ids): per character, collection and list the registered count and
-- whether the list's reward was paid.
CREATE TABLE game.character_collections (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    collection_id integer NOT NULL,
    list_id integer NOT NULL,
    registered integer NOT NULL DEFAULT 0 CHECK (registered >= 0),
    rewarded boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, collection_id, list_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_collections TO aion_app;
