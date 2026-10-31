-- sweep marker for ReturnExpiredPostsAsync (attachments/COD return to sender).
ALTER TABLE game.character_posts ADD COLUMN expiry_processed boolean NOT NULL DEFAULT false;
CREATE INDEX character_posts_expiry_sweep_idx ON game.character_posts (expire_at)
  WHERE NOT deleted AND NOT taken AND NOT expiry_processed;
