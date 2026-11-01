-- Player mail: optional sender and cash-on-delivery (COD) charged on TakePost.
ALTER TABLE game.character_posts ADD COLUMN sender_character_id uuid REFERENCES game.characters(character_id);
ALTER TABLE game.character_posts ADD COLUMN cod_kina bigint NOT NULL DEFAULT 0 CHECK (cod_kina >= 0);
