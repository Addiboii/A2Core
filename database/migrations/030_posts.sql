-- Mail (the client's Post system): one row per post a character received. post_data_id is the Post.dat template
-- (106: free title/body through msg_params "{title}|{body}"), save_tab EPostSaveTab (1 System, 2 Reward), post_type
-- EPostType (1 Game). attachments: [[item template id, count], ...], granted once on take.
CREATE TABLE game.character_posts (
    post_id bigserial PRIMARY KEY,
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    post_type smallint NOT NULL DEFAULT 1,
    save_tab smallint NOT NULL DEFAULT 1,
    post_data_id integer NOT NULL,
    msg_params text NOT NULL DEFAULT '',
    attachments jsonb NOT NULL DEFAULT '[]'::jsonb,
    reason integer NOT NULL DEFAULT 0,
    read boolean NOT NULL DEFAULT false,
    taken boolean NOT NULL DEFAULT false,
    deleted boolean NOT NULL DEFAULT false,
    sent_at timestamptz NOT NULL DEFAULT now(),
    expire_at timestamptz NOT NULL DEFAULT now() + interval '30 days'
);
CREATE INDEX character_posts_character_idx ON game.character_posts (character_id) WHERE NOT deleted;
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_posts TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.character_posts_post_id_seq TO aion_app;
