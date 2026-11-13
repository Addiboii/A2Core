-- (Merge 2026-11-13: was 044 on the Dell server; the list keeps migration 043's 'guild_storage' and 'trade_escrow'.)
-- Two warehouses, as the client has them (EItemContainerType, usmap): kStorage 2 is the account warehouse (shared by the
-- account's characters on one server, location 'storage') and kCharacterStorage 15 the character's own warehouse
-- (location 'character_storage'). Until now both went to 'storage' (USER 2026-11-10: "the warehouse stores everything
-- on the server storage even if I select the character storage"). Existing rows are left as they are: they do not
-- record which tab was chosen.
ALTER TABLE game.item_instances DROP CONSTRAINT item_instances_location_check;
ALTER TABLE game.item_instances ADD CONSTRAINT item_instances_location_check
    CHECK (location IN ('bag','equipment','storage','character_storage','buyback','guild_storage','trade_escrow'));
