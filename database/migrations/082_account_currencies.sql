-- Account currencies (USER 2026-11-08): Quna and its states belong to the account - every character of the account shares them.
-- category = ECurrencyCategory (usmap): 7 GameCash (Quna), 95 GameCashBinding, 96 GameCashNonbinding, 102 GameCashActivating,
-- 103 GameCashPending. The client shows them from RefreshCurrency_NT {group, category -> balance}.
CREATE TABLE game.account_currencies (
    account_id uuid NOT NULL REFERENCES auth.accounts ON DELETE CASCADE,
    category smallint NOT NULL CHECK (category > 0),
    balance bigint NOT NULL DEFAULT 0 CHECK (balance >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, category)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.account_currencies TO aion_app;
