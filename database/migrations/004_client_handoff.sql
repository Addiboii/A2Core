CREATE TABLE auth.wire_accounts (
    account_id uuid PRIMARY KEY REFERENCES auth.accounts,
    wire_id bigint GENERATED ALWAYS AS IDENTITY (START WITH 1000001 MAXVALUE 9999999999999999) UNIQUE NOT NULL
);
CREATE TABLE auth.client_launches (
    token_hash bytea PRIMARY KEY CHECK (octet_length(token_hash)=32),
    session_hash bytea NOT NULL REFERENCES auth.sessions ON DELETE CASCADE,
    world_id integer NOT NULL,
    expires_at timestamptz NOT NULL
);
CREATE TABLE auth.client_handoffs (
    nonce_hash bytea PRIMARY KEY CHECK (octet_length(nonce_hash)=32),
    ticket_hash bytea NOT NULL CHECK (octet_length(ticket_hash)=32),
    session_hash bytea NOT NULL REFERENCES auth.sessions ON DELETE CASCADE,
    wire_id bigint NOT NULL REFERENCES auth.wire_accounts(wire_id),
    world_id integer NOT NULL,
    expires_at timestamptz NOT NULL
);
CREATE INDEX client_launch_session_idx ON auth.client_launches(session_hash);
CREATE INDEX client_handoff_session_idx ON auth.client_handoffs(session_hash);
GRANT SELECT,INSERT ON auth.wire_accounts TO aion_auth;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA auth TO aion_auth;
GRANT SELECT,INSERT,DELETE ON auth.client_launches,auth.client_handoffs TO aion_auth;
