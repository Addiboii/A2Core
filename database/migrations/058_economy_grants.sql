-- ensure app role can use account_storage column (inherits table grants).
COMMENT ON COLUMN game.item_instances.account_storage_owner IS 'Set when location=account_storage; shared per auth.accounts row';
