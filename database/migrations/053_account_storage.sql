-- Account warehouse (RemoteStorage): items shared across characters on one account (location account_storage).
ALTER TABLE game.item_instances DROP CONSTRAINT item_instances_location_check;
ALTER TABLE game.item_instances ADD CONSTRAINT item_instances_location_check
    CHECK (location IN ('bag','equipment','storage','buyback','trade_escrow','account_storage'));
ALTER TABLE game.item_instances ADD COLUMN account_storage_owner uuid REFERENCES auth.accounts(account_id);
CREATE UNIQUE INDEX item_account_storage_slot_idx ON game.item_instances(account_storage_owner, slot)
    WHERE location = 'account_storage' AND slot IS NOT NULL;
