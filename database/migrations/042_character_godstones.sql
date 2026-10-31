-- Godstones (ported from A2Core PR #4, Artezianec, migration 040): the character's godstone collection, as the client's
-- GodstoneList_NT / GodstoneUpdate_NT carry it ({godstone id, count}). GetGodstone items add a stone when used; socketing
-- (ItemEnchantGodstone_RQ) takes one into upgrades.godstones of a weapon; combining (CombineGodstone_RQ) takes the
-- materials out of the collection and adds the reward, in one transaction.
CREATE TABLE game.character_godstones (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    godstone_id integer NOT NULL,
    count integer NOT NULL CHECK (count > 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, godstone_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_godstones TO aion_app;
