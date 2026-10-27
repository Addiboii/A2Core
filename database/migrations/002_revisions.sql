-- Writers may use WHERE revision = expected_revision and check the affected row
-- count to reject stale saves. Transactions still own inventory operations.
CREATE FUNCTION game.advance_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.revision := OLD.revision + 1;
    IF TG_TABLE_NAME = 'characters' THEN
        NEW.updated_at := now();
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER character_revision BEFORE UPDATE ON game.characters
FOR EACH ROW EXECUTE FUNCTION game.advance_revision();
CREATE TRIGGER item_revision BEFORE UPDATE ON game.item_instances
FOR EACH ROW EXECUTE FUNCTION game.advance_revision();
REVOKE ALL ON FUNCTION game.advance_revision() FROM PUBLIC;
