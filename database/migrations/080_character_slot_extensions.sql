-- Bag and warehouse slot extensions (ItemContainerSlotExtend 0x8d4f; tools/data/slot-extensions.json): per character and
-- EItemContainerType (1 kInventory, 2 kStorage, 15 kCharacterStorage) the lines bought with Kina and opened with vouchers.
-- No row: no extension. The 0x5611 ItemDepotList tail reports these counts.
CREATE TABLE game.character_slot_extensions (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    container_type smallint NOT NULL CHECK (container_type IN (1, 2, 15)),
    by_gold smallint NOT NULL DEFAULT 0 CHECK (by_gold >= 0),
    by_item smallint NOT NULL DEFAULT 0 CHECK (by_item >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, container_type)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_slot_extensions TO aion_app;
