-- Purchased lobby character slot extensions per account (Login_RS / Lobby_RS _extended_character_slot_count, 0x3A16).
-- Global client: 4 default open slots, up to 4 extensions (8 total indices 0..7).
ALTER TABLE auth.accounts
    ADD COLUMN extended_character_slots smallint NOT NULL DEFAULT 0
    CHECK (extended_character_slots >= 0 AND extended_character_slots <= 4);
