-- Expired posts with untaken attachments are returned via ReturnExpiredPosts (application); index for sweep.
CREATE INDEX character_posts_expire_idx ON game.character_posts (expire_at) WHERE NOT deleted AND NOT taken;
