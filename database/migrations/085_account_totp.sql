CREATE TABLE auth.account_totp (
    account_id uuid PRIMARY KEY REFERENCES auth.accounts ON DELETE CASCADE,
    secret_ciphertext bytea NOT NULL CHECK (octet_length(secret_ciphertext) >= 16),
    secret_nonce bytea NOT NULL CHECK (octet_length(secret_nonce)=12),
    enabled_at timestamptz NOT NULL DEFAULT now(),
    recovery_code_hashes text[] NOT NULL DEFAULT '{}'::text[] CHECK (cardinality(recovery_code_hashes) <= 16)
);
CREATE TABLE auth.totp_enrollment (
    account_id uuid PRIMARY KEY REFERENCES auth.accounts ON DELETE CASCADE,
    secret_ciphertext bytea NOT NULL CHECK (octet_length(secret_ciphertext) >= 16),
    secret_nonce bytea NOT NULL CHECK (octet_length(secret_nonce)=12),
    expires_at timestamptz NOT NULL
);
CREATE TABLE auth.totp_login_challenges (
    token_hash bytea PRIMARY KEY CHECK (octet_length(token_hash)=32),
    account_id uuid NOT NULL REFERENCES auth.accounts ON DELETE CASCADE,
    expires_at timestamptz NOT NULL,
    failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until timestamptz
);
CREATE INDEX totp_login_challenge_account_idx ON auth.totp_login_challenges(account_id);
CREATE INDEX totp_login_challenge_expiry_idx ON auth.totp_login_challenges(expires_at);
CREATE TABLE auth.platform_secondary_auth (
    session_id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES auth.accounts ON DELETE CASCADE,
    access_token text NOT NULL,
    verified_at timestamptz,
    expires_at timestamptz NOT NULL,
    failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until timestamptz
);
CREATE INDEX platform_secondary_auth_account_idx ON auth.platform_secondary_auth(account_id);
CREATE INDEX platform_secondary_auth_expiry_idx ON auth.platform_secondary_auth(expires_at);
CREATE TABLE auth.platform_client_context (
    address inet PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES auth.accounts ON DELETE CASCADE,
    expires_at timestamptz NOT NULL
);
GRANT SELECT, INSERT, UPDATE, DELETE ON auth.account_totp, auth.totp_enrollment, auth.totp_login_challenges,
    auth.platform_secondary_auth, auth.platform_client_context TO aion_auth;
