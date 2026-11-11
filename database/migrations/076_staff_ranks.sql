-- Staff ranks (OURS): gamemaster < moderator < admin (shown as Administrator). An account holds at most one rank.
-- One permission table names the lowest rank for each staff dashboard request, dashboard command kind, in-game builder
-- command and checked account operation; it is the same table as Aion.Core's StaffPermissions (a check compares
-- them), and the host allows an action only when both agree. Every rank choice is OURS.
ALTER TABLE auth.account_roles DROP CONSTRAINT account_roles_role_check;
ALTER TABLE auth.account_roles ADD CONSTRAINT account_roles_role_check CHECK (role IN ('gamemaster','moderator','admin'));

-- Existing staff become Administrator, audited per account whose rank changes.
INSERT INTO audit.events(action,details)
SELECT 'staff_rank_changed', jsonb_build_object('target',account_id,'from','gamemaster','to','admin','reason','migration 076')
FROM auth.account_roles GROUP BY account_id HAVING NOT bool_or(role='admin');
DELETE FROM auth.account_roles r WHERE r.role<>'admin'
  AND EXISTS(SELECT FROM auth.account_roles o WHERE o.account_id=r.account_id AND o.role='admin');
UPDATE auth.account_roles SET role='admin' WHERE role<>'admin';
CREATE UNIQUE INDEX account_roles_one_rank ON auth.account_roles(account_id);

CREATE FUNCTION auth.staff_rank(p_role text) RETURNS integer
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
    SELECT CASE p_role WHEN 'gamemaster' THEN 1 WHEN 'moderator' THEN 2 WHEN 'admin' THEN 3 ELSE 0 END;
$$;

CREATE TABLE auth.staff_permissions (
    action text PRIMARY KEY CHECK (length(action) BETWEEN 1 AND 128),
    min_role text NOT NULL CHECK (min_role IN ('gamemaster','moderator','admin'))
);
-- Rows written from server/Aion.Core/Accounts/StaffPermissions.cs (OURS).
INSERT INTO auth.staff_permissions(action,min_role) VALUES
    ('api GET /api/admin/me','gamemaster'),
    ('api GET /api/admin/characters','gamemaster'),
    ('api GET /api/admin/characters/{id:guid}','gamemaster'),
    ('api GET /api/admin/items','gamemaster'),
    ('api POST /api/admin/commands','gamemaster'),
    ('api GET /api/admin/live','gamemaster'),
    ('api GET /api/admin/maps','gamemaster'),
    ('api GET /api/admin/maps/{id:int}/outline','gamemaster'),
    ('api GET /api/admin/maps/{id:int}/image','gamemaster'),
    ('api GET /api/admin/server','gamemaster'),
    ('api POST /api/admin/players/{name}/message','gamemaster'),
    ('api POST /api/admin/players/{name}/kick','gamemaster'),
    ('api POST /api/admin/players/{name}/unstuck','gamemaster'),
    ('api POST /api/admin/players/{name}/teleport','gamemaster'),
    ('api POST /api/admin/players/{name}/restore','moderator'),
    ('api POST /api/admin/announce','moderator'),
    ('api GET /api/admin/audit','moderator'),
    ('api POST /api/admin/restart','admin'),
    ('api DELETE /api/admin/restart','admin'),
    ('api GET /api/admin/sanctions','gamemaster'),
    ('api POST /api/admin/sanctions','moderator'),
    ('api DELETE /api/admin/sanctions/{id:long}','moderator'),
    ('api GET /api/admin/chat','gamemaster'),
    ('api GET /api/admin/tickets','gamemaster'),
    ('api POST /api/admin/tickets/{id:long}','gamemaster'),
    ('api GET /api/admin/maintenance','gamemaster'),
    ('api POST /api/admin/maintenance','admin'),
    ('api GET /api/admin/events','gamemaster'),
    ('api POST /api/admin/events','moderator'),
    ('api DELETE /api/admin/events/{id:long}','moderator'),
    ('api GET /api/admin/population','gamemaster'),
    ('api GET /api/admin/economy','moderator'),
    ('api POST /api/admin/rewards','moderator'),
    ('api GET /api/admin/npcs','gamemaster'),
    ('api GET /api/admin/maps/{id:int}/monsters','gamemaster'),
    ('api POST /api/admin/maps/{id:int}/monsters/respawn','moderator'),
    ('api POST /api/admin/maps/{id:int}/monsters/clear','moderator'),
    ('api POST /api/admin/maps/{id:int}/monsters/spawn','moderator'),
    ('api DELETE /api/admin/maps/{id:int}/monsters/staff','moderator'),
    ('api GET /api/admin/backups','admin'),
    ('api POST /api/admin/backups','admin'),
    ('api GET /api/admin/backups/{name}','admin'),
    ('api POST /api/admin/wardrobe-access','gamemaster'),
    ('api POST /api/admin/accounts','admin'),
    ('api POST /api/admin/accounts/{id:guid}/disable','admin'),
    ('api POST /api/admin/accounts/{id:guid}/roles','admin'),
    ('api POST /api/admin/accounts/{id:guid}/totp/reset','admin'),
    ('account.disable','admin'),
    ('account.totp_reset','admin'),
    ('staff.rank','admin'),
    ('command.teleport','gamemaster'),
    ('command.level','moderator'),
    ('command.class','moderator'),
    ('command.ascension','moderator'),
    ('command.wings','moderator'),
    ('command.grant','moderator'),
    ('command.staff','admin'),
    ('grant.own_account','admin'),
    ('gm.SetInvisible','gamemaster'),
    ('gm.SetInvincible','gamemaster'),
    ('gm.SetInvincibleHP','gamemaster'),
    ('gm.NoCool','gamemaster'),
    ('gm.DashVariant','gamemaster'),
    ('gm.Hp','gamemaster'),
    ('gm.Mp','gamemaster'),
    ('gm.Sp','gamemaster'),
    ('gm.FixHpPercent','gamemaster'),
    ('gm.Teleport','gamemaster'),
    ('gm.TeleportTo','gamemaster'),
    ('gm.MoveMap','gamemaster'),
    ('gm.LayerTest','gamemaster'),
    ('gm.Dev','gamemaster'),
    ('gm.RevealMap','gamemaster'),
    ('gm.EnterDungeon','gamemaster'),
    ('gm.Mail','gamemaster'),
    ('gm.SetLevel','moderator'),
    ('gm.ClassChange','moderator'),
    ('gm.SetAscension','moderator'),
    ('gm.ContentsUnlock','moderator'),
    ('gm.ContentsUnlockAll','moderator'),
    ('gm.AcquireWingAll','moderator'),
    ('gm.QuestAdd','moderator'),
    ('gm.QuestComplete','moderator'),
    ('gm.QuestCompleteStep','moderator'),
    ('gm.QuestDelete','moderator'),
    ('gm.AcquireTitle','moderator'),
    ('gm.AcquireSkin','moderator'),
    ('gm.SetMastery','moderator'),
    ('gm.AcquireItem','moderator'),
    ('gm.QuestReward','moderator'),
    ('gm.MailItem','moderator');

-- Whether an account's rank allows an action; unknown actions and disabled accounts are refused.
CREATE FUNCTION auth.staff_allowed(p_account uuid, p_action text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    SELECT EXISTS(SELECT FROM auth.account_roles r JOIN auth.accounts a USING(account_id)
                  JOIN auth.staff_permissions p ON p.action=p_action
                  WHERE r.account_id=p_account AND a.disabled_at IS NULL AND auth.staff_rank(r.role)>=auth.staff_rank(p.min_role));
$$;
REVOKE ALL ON FUNCTION auth.staff_allowed(uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth.staff_allowed(uuid,text) TO aion_app, aion_auth;

-- Every rank change is audited, whoever writes it (dashboard, postgres_dev.py role): staff_rank_changed
-- {target, from, to}; the actor is the staff account when auth.set_staff_rank made the change.
CREATE FUNCTION auth.audit_staff_rank() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid := nullif(current_setting('aion.staff_actor', true), '')::uuid;
BEGIN
    IF TG_OP='INSERT' THEN
        INSERT INTO audit.events(action,actor_id,details)
          VALUES('staff_rank_changed',actor,jsonb_build_object('target',NEW.account_id,'from',NULL,'to',NEW.role));
    ELSIF TG_OP='DELETE' THEN
        INSERT INTO audit.events(action,actor_id,details)
          VALUES('staff_rank_changed',actor,jsonb_build_object('target',OLD.account_id,'from',OLD.role,'to',NULL));
    ELSIF OLD.role<>NEW.role THEN
        INSERT INTO audit.events(action,actor_id,details)
          VALUES('staff_rank_changed',actor,jsonb_build_object('target',NEW.account_id,'from',OLD.role,'to',NEW.role));
    END IF;
    RETURN NULL;
END $$;
REVOKE ALL ON FUNCTION auth.audit_staff_rank() FROM PUBLIC;
CREATE TRIGGER staff_rank_audit AFTER INSERT OR UPDATE OR DELETE ON auth.account_roles
    FOR EACH ROW EXECUTE FUNCTION auth.audit_staff_rank();

-- Sets (or with NULL removes) an account's rank, from a staff session allowed staff.rank. Nobody changes their own
-- rank, and nobody sets or changes a rank above their own.
CREATE FUNCTION auth.set_staff_rank(p_session bytea, p_target uuid, p_role text) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid; actor_rank integer; held text;
BEGIN
    IF p_role IS NOT NULL AND p_role NOT IN ('gamemaster','moderator','admin') THEN RETURN false; END IF;
    SELECT s.account_id, auth.staff_rank(r.role) INTO actor, actor_rank
      FROM auth.sessions s JOIN auth.accounts a USING(account_id) JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.expires_at>now() AND s.kind='staff' AND a.disabled_at IS NULL;
    IF actor IS NULL OR actor=p_target OR NOT auth.staff_allowed(actor,'staff.rank') THEN RETURN false; END IF;
    PERFORM 1 FROM auth.accounts WHERE account_id=p_target AND disabled_at IS NULL FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    SELECT role INTO held FROM auth.account_roles WHERE account_id=p_target;
    IF auth.staff_rank(held)>actor_rank OR auth.staff_rank(p_role)>actor_rank THEN RETURN false; END IF;
    IF held IS NOT DISTINCT FROM p_role THEN RETURN true; END IF;
    PERFORM set_config('aion.staff_actor', actor::text, true);
    IF p_role IS NULL THEN
        DELETE FROM auth.account_roles WHERE account_id=p_target;
    ELSE
        INSERT INTO auth.account_roles(account_id,role) VALUES(p_target,p_role)
          ON CONFLICT(account_id) DO UPDATE SET role=EXCLUDED.role;
    END IF;
    PERFORM set_config('aion.staff_actor', '', true);
    RETURN true;
END $$;
REVOKE ALL ON FUNCTION auth.set_staff_rank(bytea,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth.set_staff_rank(bytea,uuid,text) TO aion_auth;

-- The role toggle of migration 009, kept for its callers: granting a role sets that rank (replacing the old one),
-- revoking the held role removes the rank; audited by the trigger above.
CREATE OR REPLACE FUNCTION auth.set_staff_role(p_session bytea,p_target uuid,p_role text,p_granted boolean) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE held text;
BEGIN
    IF p_role IS NULL OR p_role NOT IN ('gamemaster','moderator','admin') OR p_granted IS NULL THEN RETURN false; END IF;
    IF p_granted THEN RETURN auth.set_staff_rank(p_session,p_target,p_role); END IF;
    SELECT role INTO held FROM auth.account_roles WHERE account_id=p_target;
    RETURN auth.set_staff_rank(p_session,p_target,CASE WHEN held=p_role THEN NULL ELSE held END);
END $$;

-- The checked account operations (migration 075) ask the permission table instead of naming the admin role.
CREATE OR REPLACE FUNCTION auth.disable_account(p_session bytea,p_target uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;
BEGIN
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id)
      WHERE s.token_hash=p_session AND s.expires_at>now() AND s.kind='staff' AND a.disabled_at IS NULL;
    IF actor IS NULL OR actor=p_target OR NOT auth.staff_allowed(actor,'account.disable') THEN RETURN false; END IF;
    PERFORM 1 FROM auth.local_credentials WHERE account_id=p_target FOR UPDATE;
    UPDATE auth.accounts SET disabled_at=COALESCE(disabled_at,now()) WHERE account_id=p_target;
    IF NOT FOUND THEN RETURN false; END IF;
    DELETE FROM auth.sessions WHERE account_id=p_target;
    INSERT INTO audit.events(action,actor_id,details)
      VALUES('account_disabled',actor,jsonb_build_object('target',p_target));
    RETURN true;
END $$;

CREATE OR REPLACE FUNCTION auth.reset_totp(p_session bytea,p_target uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid; was_enabled boolean;
BEGIN
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id)
      WHERE s.token_hash=p_session AND s.expires_at>now() AND s.kind='staff' AND a.disabled_at IS NULL;
    IF actor IS NULL OR NOT auth.staff_allowed(actor,'account.totp_reset') THEN RETURN false; END IF;
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
