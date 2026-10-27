-- Staff is per character. A new character is a player even on an account with a staff role; the
-- flag is set by hand (admin command "staff", the staff dashboard's character page) and gates the
-- in-world role: the name tag, builder commands, the default wing, entry overrides. Account roles
-- stay the authorization root (the dashboard, staff-style names at creation, who may be flagged).
-- Existing characters of staff accounts keep their standing.
ALTER TABLE game.characters ADD COLUMN staff boolean NOT NULL DEFAULT false;
UPDATE game.characters c SET staff=true
WHERE EXISTS(SELECT FROM auth.account_roles r WHERE r.account_id=c.account_id AND r.role IN ('gamemaster','admin'));
