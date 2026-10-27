CREATE TABLE auth.local_credentials (
    account_id uuid PRIMARY KEY REFERENCES auth.accounts,
    username text NOT NULL UNIQUE CHECK (username ~ '^[a-z0-9_]{3,32}$'),
    password_hash text NOT NULL,
    failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until timestamptz
);
CREATE TABLE auth.sessions (
    token_hash bytea PRIMARY KEY CHECK (octet_length(token_hash)=32),
    account_id uuid NOT NULL REFERENCES auth.accounts,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX session_account_idx ON auth.sessions(account_id);
CREATE INDEX session_expiry_idx ON auth.sessions(expires_at);
CREATE TABLE auth.game_tickets (
    token_hash bytea PRIMARY KEY CHECK (octet_length(token_hash)=32),
    session_hash bytea NOT NULL REFERENCES auth.sessions ON DELETE CASCADE,
    world_id integer NOT NULL,
    expires_at timestamptz NOT NULL
);
CREATE INDEX ticket_session_idx ON auth.game_tickets(session_hash);
CREATE INDEX ticket_expiry_idx ON auth.game_tickets(expires_at);

GRANT USAGE ON SCHEMA auth TO aion_auth;
GRANT SELECT, INSERT ON auth.accounts TO aion_auth;
GRANT SELECT, INSERT, UPDATE ON auth.local_credentials TO aion_auth;
GRANT SELECT, INSERT, DELETE ON auth.sessions, auth.game_tickets TO aion_auth;

-- The game role may consume a ticket, but cannot read credentials or sessions.
-- Logout cascades to outstanding tickets; disable/expiry is checked at consume.
CREATE FUNCTION auth.consume_game_ticket(p_hash bytea,p_world integer) RETURNS uuid
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
    DELETE FROM auth.game_tickets t USING auth.sessions s,auth.accounts a
    WHERE t.token_hash=p_hash AND t.world_id=p_world AND t.expires_at>now()
      AND s.token_hash=t.session_hash AND s.expires_at>now()
      AND a.account_id=s.account_id AND a.disabled_at IS NULL
    RETURNING a.account_id;
$$;
REVOKE ALL ON FUNCTION auth.consume_game_ticket(bytea,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth.consume_game_ticket(bytea,integer) TO aion_app;
