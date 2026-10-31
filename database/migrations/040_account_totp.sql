-- Two-step sign-in (TOTP) for the account site, launcher and staff dashboard. Ported from A2Core PR #4
-- (Artezianec, its 085_account_totp.sql) without the platform secondary_auth/client-address tables.
-- Secrets are AES-GCM ciphertext (key Aion:Totp:EncryptionKey, never in the database). last_step is the
-- last accepted 30 s time step: a code is accepted once. failed_attempts/locked_until count wrong codes
-- per account across sign-in challenges (5 wrong codes lock code entry for 15 minutes).
CREATE TABLE auth.account_totp (
    account_id uuid PRIMARY KEY REFERENCES auth.accounts ON DELETE CASCADE,
    secret_ciphertext bytea NOT NULL CHECK (octet_length(secret_ciphertext) > 16),
    secret_nonce bytea NOT NULL CHECK (octet_length(secret_nonce)=12),
    enabled_at timestamptz NOT NULL DEFAULT now(),
    recovery_code_hashes text[] NOT NULL DEFAULT '{}'::text[] CHECK (cardinality(recovery_code_hashes) <= 16),
    last_step bigint NOT NULL,
    failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until timestamptz
);
CREATE TABLE auth.totp_enrollment (
    account_id uuid PRIMARY KEY REFERENCES auth.accounts ON DELETE CASCADE,
    secret_ciphertext bytea NOT NULL CHECK (octet_length(secret_ciphertext) > 16),
    secret_nonce bytea NOT NULL CHECK (octet_length(secret_nonce)=12),
    expires_at timestamptz NOT NULL
);
-- A correct password on a two-step account gives a challenge (not a session); the session it becomes keeps
-- the lifetime the sign-in asked for ("keep me signed in").
CREATE TABLE auth.totp_login_challenges (
    token_hash bytea PRIMARY KEY CHECK (octet_length(token_hash)=32),
    account_id uuid NOT NULL REFERENCES auth.accounts ON DELETE CASCADE,
    expires_at timestamptz NOT NULL,
    session_seconds integer NOT NULL CHECK (session_seconds BETWEEN 1 AND 2592000)
);
CREATE INDEX totp_login_challenge_account_idx ON auth.totp_login_challenges(account_id);
CREATE INDEX totp_login_challenge_expiry_idx ON auth.totp_login_challenges(expires_at);
GRANT SELECT, INSERT, UPDATE, DELETE ON auth.account_totp, auth.totp_enrollment, auth.totp_login_challenges TO aion_auth;

-- Admins remove two-step sign-in from an account (lost phone and recovery codes) through this checked,
-- audited operation, like auth.set_staff_role.
CREATE FUNCTION auth.reset_totp(p_session bytea,p_target uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid; was_enabled boolean;
BEGIN
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id)
      JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.expires_at>now() AND a.disabled_at IS NULL AND r.role='admin';
    IF actor IS NULL THEN RETURN false; END IF;
    PERFORM 1 FROM auth.accounts WHERE account_id=p_target FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    DELETE FROM auth.account_totp WHERE account_id=p_target;
    was_enabled := FOUND;
    DELETE FROM auth.totp_enrollment WHERE account_id=p_target;
    DELETE FROM auth.totp_login_challenges WHERE account_id=p_target;
    INSERT INTO audit.events(action,actor_id,details)
      VALUES('totp_reset',actor,jsonb_build_object('target',p_target,'was_enabled',was_enabled));
    RETURN true;
END $$;
REVOKE ALL ON FUNCTION auth.reset_totp(bytea,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth.reset_totp(bytea,uuid) TO aion_auth;
