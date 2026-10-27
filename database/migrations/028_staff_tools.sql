-- Staff tools: server settings (maintenance mode), sanctions (timed bans and chat mutes), the chat log, player
-- tickets (reports), scheduled events (timed announcements, experience and drop boosts) and population samples.
CREATE TABLE game.server_settings (
    key text PRIMARY KEY,
    value jsonb NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid REFERENCES auth.accounts
);

-- kind ban: no game login; mute: no chat. expires_at NULL is permanent; revoked ones are kept for the record.
CREATE TABLE game.sanctions (
    sanction_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES auth.accounts,
    kind text NOT NULL CHECK (kind IN ('ban', 'mute')),
    reason text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz,
    created_by uuid REFERENCES auth.accounts,
    revoked_at timestamptz,
    revoked_by uuid REFERENCES auth.accounts
);
CREATE INDEX sanctions_active_idx ON game.sanctions(account_id, kind) WHERE revoked_at IS NULL;

-- Room messages and whispers as delivered (kept 30 days by the host's cleanup).
CREATE TABLE game.chat_log (
    message_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sent_at timestamptz NOT NULL DEFAULT now(),
    channel text NOT NULL,
    account_id uuid,
    sender text NOT NULL,
    receiver text,
    content text NOT NULL
);
CREATE INDEX chat_log_sender_idx ON game.chat_log(lower(sender), message_id DESC);
CREATE INDEX chat_log_time_idx ON game.chat_log(sent_at);

CREATE TABLE game.tickets (
    ticket_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now(),
    account_id uuid REFERENCES auth.accounts,
    character_name text,
    category text NOT NULL DEFAULT 'other' CHECK (category IN ('bug', 'player', 'stuck', 'other')),
    content text NOT NULL,
    status text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'closed')),
    staff_note text NOT NULL DEFAULT '',
    handled_by uuid REFERENCES auth.accounts,
    handled_at timestamptz
);
CREATE INDEX tickets_open_idx ON game.tickets(status, ticket_id DESC);

-- announce: text at starts_at (and every repeat_minutes until ends_at); exp / drop: multiplier from starts_at to ends_at.
CREATE TABLE game.scheduled_events (
    event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind text NOT NULL CHECK (kind IN ('announce', 'exp', 'drop')),
    starts_at timestamptz NOT NULL,
    ends_at timestamptz,
    repeat_minutes int CHECK (repeat_minutes IS NULL OR repeat_minutes BETWEEN 5 AND 10080),
    text text NOT NULL DEFAULT '',
    multiplier numeric(5,2) CHECK (multiplier IS NULL OR multiplier BETWEEN 0.1 AND 10),
    created_at timestamptz NOT NULL DEFAULT now(),
    created_by uuid REFERENCES auth.accounts,
    cancelled_at timestamptz,
    last_run_at timestamptz
);

CREATE TABLE game.population_samples (
    sampled_at timestamptz PRIMARY KEY DEFAULT now(),
    players int NOT NULL,
    connections int NOT NULL
);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.server_settings, game.sanctions, game.chat_log, game.tickets, game.scheduled_events,
    game.population_samples TO aion_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA game TO aion_app;
