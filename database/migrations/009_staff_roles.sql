-- Staff roles are admin and gamemaster (formerly gm). Clients show them as "[Admin]"
-- and "[GM]" name tags; roles are re-read at every game login.
ALTER TABLE auth.account_roles DROP CONSTRAINT account_roles_role_check;
UPDATE auth.account_roles SET role='gamemaster' WHERE role='gm';
ALTER TABLE auth.account_roles ADD CONSTRAINT account_roles_role_check CHECK (role IN ('gamemaster','admin'));

-- Admins grant and revoke staff roles through this checked operation (the auth role never
-- writes account_roles directly). An admin cannot remove their own admin role, so the
-- last admin stays; disabled accounts cannot be changed.
CREATE FUNCTION auth.set_staff_role(p_session bytea,p_target uuid,p_role text,p_granted boolean) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;
BEGIN
    IF p_role IS NULL OR p_role NOT IN ('gamemaster','admin') OR p_granted IS NULL THEN RETURN false; END IF;
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id)
      JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.expires_at>now() AND a.disabled_at IS NULL AND r.role='admin';
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
REVOKE ALL ON FUNCTION auth.set_staff_role(bytea,uuid,text,boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth.set_staff_role(bytea,uuid,text,boolean) TO aion_auth;
