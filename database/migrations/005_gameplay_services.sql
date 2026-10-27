-- Explicit server policy, separate from recovered catalogue facts.
CREATE TABLE game.starter_profiles (
    profile_id text PRIMARY KEY,
    build_id text NOT NULL REFERENCES catalog.builds,
    creation_id integer NOT NULL,
    race integer NOT NULL,
    gender integer NOT NULL,
    enabled boolean NOT NULL DEFAULT false,
    definition jsonb NOT NULL CHECK(jsonb_typeof(definition)='object')
);
CREATE TABLE game.equipment_rules (
    build_id text NOT NULL,
    template_id integer NOT NULL,
    slot integer NOT NULL CHECK(slot>=0),
    minimum_level integer NOT NULL CHECK(minimum_level>=1),
    creation_ids integer[] NOT NULL,
    evidence text NOT NULL,
    PRIMARY KEY(build_id,template_id,slot),
    FOREIGN KEY(build_id,template_id) REFERENCES catalog.items
);
CREATE TABLE game.character_initializations (
    request_id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES auth.accounts,
    character_id uuid NOT NULL UNIQUE REFERENCES game.characters,
    arguments jsonb NOT NULL
);
CREATE TABLE game.skill_slots (
    character_id uuid NOT NULL,
    slot integer NOT NULL CHECK(slot>=0),
    skill_id integer NOT NULL,
    PRIMARY KEY(character_id,slot),
    FOREIGN KEY(character_id,skill_id) REFERENCES game.character_skills(character_id,skill_id)
);
CREATE TABLE game.admin_requests (
    request_id uuid PRIMARY KEY,
    actor_id uuid NOT NULL REFERENCES auth.accounts,
    arguments jsonb NOT NULL,
    result jsonb NOT NULL
);
ALTER TABLE audit.events ADD COLUMN actor_id uuid REFERENCES auth.accounts;
GRANT SELECT ON game.starter_profiles,game.equipment_rules TO aion_app;
GRANT SELECT,INSERT ON game.character_initializations,game.admin_requests TO aion_app;
GRANT SELECT,INSERT,UPDATE,DELETE ON game.skill_slots TO aion_app;

-- Auth role can disable through this checked operation, never directly update
-- accounts/roles. Lock credentials in the same order as sign-in/password change.
CREATE FUNCTION auth.disable_account(p_session bytea,p_target uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;
BEGIN
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id)
      JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.expires_at>now() AND a.disabled_at IS NULL AND r.role='admin';
    IF actor IS NULL OR actor=p_target THEN RETURN false; END IF;
    PERFORM 1 FROM auth.local_credentials WHERE account_id=p_target FOR UPDATE;
    UPDATE auth.accounts SET disabled_at=COALESCE(disabled_at,now()) WHERE account_id=p_target;
    IF NOT FOUND THEN RETURN false; END IF;
    DELETE FROM auth.sessions WHERE account_id=p_target;
    INSERT INTO audit.events(action,actor_id,details)
      VALUES('account_disabled',actor,jsonb_build_object('target',p_target));
    RETURN true;
END $$;
REVOKE ALL ON FUNCTION auth.disable_account(bytea,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth.disable_account(bytea,uuid) TO aion_auth;
