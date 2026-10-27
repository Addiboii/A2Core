-- Friends, friend requests and blocks between characters of the same world. A friendship is two
-- rows (one per side, each with its owner's memo); a request is one row from the asker; a block is
-- one row and ends any friendship or request between the two.
CREATE TABLE game.character_friends (
    owner_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    friend_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    memo text NOT NULL DEFAULT '' CHECK (length(memo) <= 64),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id, friend_id),
    CHECK (owner_id <> friend_id)
);
CREATE INDEX character_friends_friend_idx ON game.character_friends(friend_id);

CREATE TABLE game.character_friend_requests (
    from_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    to_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (from_id, to_id),
    CHECK (from_id <> to_id)
);
CREATE INDEX character_friend_requests_to_idx ON game.character_friend_requests(to_id);

CREATE TABLE game.character_blocks (
    owner_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    blocked_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (owner_id, blocked_id),
    CHECK (owner_id <> blocked_id)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_friends, game.character_friend_requests, game.character_blocks TO aion_app;
