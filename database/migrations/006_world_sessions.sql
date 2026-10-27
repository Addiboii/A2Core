-- A server-side connection capability, never sent to the original game client.
CREATE TABLE auth.world_sessions (
    token_hash bytea PRIMARY KEY CHECK(octet_length(token_hash)=32),
    session_hash bytea NOT NULL REFERENCES auth.sessions ON DELETE CASCADE,
    world_id integer NOT NULL,
    expires_at timestamptz NOT NULL
);
CREATE INDEX world_session_parent_idx ON auth.world_sessions(session_hash);
CREATE INDEX world_session_expiry_idx ON auth.world_sessions(expires_at);
GRANT SELECT,INSERT,DELETE ON auth.world_sessions TO aion_auth;
