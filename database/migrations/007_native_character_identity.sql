-- Durable protocol IDs are distinct from UUID ownership and temporary map actors.
ALTER TABLE game.characters ADD COLUMN wire_id integer GENERATED ALWAYS AS IDENTITY UNIQUE;
ALTER TABLE game.characters ADD COLUMN voice_type integer NOT NULL DEFAULT 0 CHECK(voice_type BETWEEN 0 AND 255);
ALTER TABLE game.characters ADD COLUMN lobby_slot integer CHECK(lobby_slot BETWEEN 0 AND 255);
CREATE UNIQUE INDEX character_lobby_slot_idx ON game.characters(account_id,world_id,lobby_slot) WHERE lobby_slot IS NOT NULL;
ALTER TABLE game.item_instances ADD COLUMN wire_id bigint GENERATED ALWAYS AS IDENTITY (MAXVALUE 9007199254740991) UNIQUE;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA game TO aion_app;

CREATE FUNCTION game.keep_wire_identity() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.wire_id IS DISTINCT FROM OLD.wire_id THEN
        RAISE EXCEPTION 'Wire identity cannot change';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER character_wire_identity BEFORE UPDATE ON game.characters
FOR EACH ROW EXECUTE FUNCTION game.keep_wire_identity();
CREATE TRIGGER item_wire_identity BEFORE UPDATE ON game.item_instances
FOR EACH ROW EXECUTE FUNCTION game.keep_wire_identity();
REVOKE ALL ON FUNCTION game.keep_wire_identity() FROM PUBLIC;
