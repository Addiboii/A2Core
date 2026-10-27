-- Items can now be used up (reward boxes, potions, scrolls): the last unit deletes its item_instances row.
-- The grant record pointed at it with a deferred foreign key, so the commit failed (live 2026-10-16: opening
-- a granted pet chest ended the connection). The grant record goes with its item; every grant path keeps its
-- own guard against granting twice (admin_requests, quest status, random request ids for box rewards).
ALTER TABLE game.item_grants DROP CONSTRAINT item_grants_instance_id_fkey;
ALTER TABLE game.item_grants ADD CONSTRAINT item_grants_instance_id_fkey
    FOREIGN KEY (instance_id) REFERENCES game.item_instances ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED;
