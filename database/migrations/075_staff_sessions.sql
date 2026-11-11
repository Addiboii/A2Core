-- Sessions have a kind (OURS): 'player' (account site, launcher) or 'staff' (the staff dashboard's sign-in, which
-- goes through two-step sign-in like any other when the account has it). /api/admin/* accepts staff sessions only,
-- and the checked staff operations below need one, so a player session's token cannot drive the admin API even on
-- a staff account. Sessions that exist now stay player sessions; staff sign in to the dashboard again.
ALTER TABLE auth.sessions ADD COLUMN kind text NOT NULL DEFAULT 'player' CHECK (kind IN ('player','staff'));
-- The kind a two-step sign-in's session will have once the code is checked.
ALTER TABLE auth.totp_login_challenges ADD COLUMN session_kind text NOT NULL DEFAULT 'player' CHECK (session_kind IN ('player','staff'));

-- Whether an account may hold a staff session: enabled, with a staff role. The auth role cannot read
-- auth.account_roles, so it asks here before a staff sign-in (password checked, no session yet).
CREATE FUNCTION auth.staff_account(p_account uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    SELECT EXISTS(SELECT FROM auth.account_roles r JOIN auth.accounts a USING(account_id)
                  WHERE r.account_id=p_account AND a.disabled_at IS NULL);
$$;
REVOKE ALL ON FUNCTION auth.staff_account(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth.staff_account(uuid) TO aion_auth;

-- A staff session row exists only for such an account, whoever writes it.
CREATE FUNCTION auth.check_staff_session() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    IF NEW.kind='staff' AND NOT auth.staff_account(NEW.account_id) THEN
        RAISE EXCEPTION 'staff session for an account without a staff role' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION auth.check_staff_session() FROM PUBLIC;
CREATE TRIGGER staff_session_role BEFORE INSERT OR UPDATE OF kind,account_id ON auth.sessions
    FOR EACH ROW EXECUTE FUNCTION auth.check_staff_session();

-- Losing the last staff role (dashboard, postgres_dev.py role --revoke, any path) ends the account's staff sessions.
CREATE FUNCTION auth.end_staff_sessions() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    IF NOT EXISTS(SELECT FROM auth.account_roles WHERE account_id=OLD.account_id) THEN
        DELETE FROM auth.sessions WHERE account_id=OLD.account_id AND kind='staff';
    END IF;
    RETURN NULL;
END $$;
REVOKE ALL ON FUNCTION auth.end_staff_sessions() FROM PUBLIC;
CREATE TRIGGER staff_role_removed AFTER DELETE ON auth.account_roles
    FOR EACH ROW EXECUTE FUNCTION auth.end_staff_sessions();

-- The checked staff operations (migrations 005, 009, 040) now need a staff session; otherwise unchanged.
CREATE OR REPLACE FUNCTION auth.disable_account(p_session bytea,p_target uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;
BEGIN
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id)
      JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.expires_at>now() AND s.kind='staff' AND a.disabled_at IS NULL AND r.role='admin';
    IF actor IS NULL OR actor=p_target THEN RETURN false; END IF;
    PERFORM 1 FROM auth.local_credentials WHERE account_id=p_target FOR UPDATE;
    UPDATE auth.accounts SET disabled_at=COALESCE(disabled_at,now()) WHERE account_id=p_target;
    IF NOT FOUND THEN RETURN false; END IF;
    DELETE FROM auth.sessions WHERE account_id=p_target;
    INSERT INTO audit.events(action,actor_id,details)
      VALUES('account_disabled',actor,jsonb_build_object('target',p_target));
    RETURN true;
END $$;

CREATE OR REPLACE FUNCTION auth.set_staff_role(p_session bytea,p_target uuid,p_role text,p_granted boolean) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;
BEGIN
    IF p_role IS NULL OR p_role NOT IN ('gamemaster','admin') OR p_granted IS NULL THEN RETURN false; END IF;
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id)
      JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.expires_at>now() AND s.kind='staff' AND a.disabled_at IS NULL AND r.role='admin';
    IF actor IS NULL OR (actor=p_target AND p_role='admin' AND NOT p_granted) THEN RETURN false; END IF;
    PERFORM 1 FROM auth.accounts WHERE account_id=p_target AND disabled_at IS NULL FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    IF p_granted THEN
        INSERT INTO auth.account_roles VALUES(p_target,p_role) ON CONFLICT DO NOTHING;
    ELSE
        DELETE FROM auth.account_roles WHERE account_id=p_target AND role=p_role;
    END IF;
    INSERT INTO audit.events(action,actor_id,details)
      VALUES(CASE WHEN p_granted THEN 'role_granted' ELSE 'role_revoked' END,actor,
             jsonb_build_object('target',p_target,'role',p_role));
    RETURN true;
END $$;

CREATE OR REPLACE FUNCTION auth.reset_totp(p_session bytea,p_target uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid; was_enabled boolean;
BEGIN
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id)
      JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.expires_at>now() AND s.kind='staff' AND a.disabled_at IS NULL AND r.role='admin';
    IF actor IS NULL THEN RETURN false; END IF;
    PERFORM 1 FROM auth.accounts WHERE account_id=p_target FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    DELETE FROM auth.account_totp WHERE account_id=p_target;
    was_enabled := FOUND;
    DELETE FROM auth.totp_enrollment WHERE account_id=p_target;
    DELETE FROM auth.totp_login_challenges WHERE account_id=p_target;
    INSERT INTO audit.events(action,actor_id,details)
      VALUES('totp_reset',actor,jsonb_build_object('target',p_target,'was_enabled',was_enabled));
    RETURN true;
END $$;
