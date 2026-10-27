CREATE SCHEMA catalog;
CREATE SCHEMA auth;
CREATE SCHEMA game;
CREATE SCHEMA audit;

CREATE TABLE catalog.builds (
    build_id text PRIMARY KEY,
    manifest jsonb NOT NULL CHECK (jsonb_typeof(manifest) = 'object'),
    imported_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE catalog.items (
    build_id text NOT NULL REFERENCES catalog.builds,
    template_id integer NOT NULL,
    name text NOT NULL,
    category text NOT NULL,
    definition jsonb NOT NULL CHECK (jsonb_typeof(definition) = 'object'),
    PRIMARY KEY(build_id, template_id)
);
CREATE TABLE catalog.skills (
    build_id text NOT NULL REFERENCES catalog.builds,
    template_id integer NOT NULL,
    name text NOT NULL,
    category text NOT NULL,
    definition jsonb NOT NULL CHECK (jsonb_typeof(definition) = 'object'),
    PRIMARY KEY(build_id, template_id)
);
CREATE INDEX item_category_idx ON catalog.items(build_id, category);
CREATE INDEX skill_category_idx ON catalog.skills(build_id, category);

CREATE TABLE auth.accounts (
    account_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    provider text NOT NULL,
    subject text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    disabled_at timestamptz,
    UNIQUE(provider, subject)
);
-- Managed by administration, never by client input or the gameplay DB role.
CREATE TABLE auth.account_roles (
    account_id uuid NOT NULL REFERENCES auth.accounts,
    role text NOT NULL CHECK (role IN ('gm','admin')),
    PRIMARY KEY(account_id, role)
);
CREATE TABLE game.characters (
    character_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL REFERENCES auth.accounts,
    world_id integer NOT NULL,
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 64),
    level integer NOT NULL DEFAULT 1 CHECK (level >= 1),
    experience bigint NOT NULL DEFAULT 0 CHECK (experience >= 0),
    creation_id integer NOT NULL,
    race integer NOT NULL,
    gender integer NOT NULL,
    appearance bytea NOT NULL DEFAULT ''::bytea,
    map_id integer,
    position jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(position) = 'object'),
    resources jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(resources) = 'object'),
    revision bigint NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX character_name_idx ON game.characters(world_id, lower(name));
CREATE INDEX characters_account_idx ON game.characters(account_id);

CREATE TABLE game.item_instances (
    instance_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    character_id uuid NOT NULL REFERENCES game.characters,
    build_id text NOT NULL,
    template_id integer NOT NULL,
    quantity bigint NOT NULL CHECK (quantity > 0),
    location text NOT NULL DEFAULT 'bag' CHECK (location IN ('bag','equipment','storage')),
    slot integer CHECK (slot >= 0),
    rolled_stats jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(rolled_stats) = 'object'),
    upgrades jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(upgrades) = 'object'),
    revision bigint NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY(build_id, template_id) REFERENCES catalog.items,
    CHECK (location <> 'equipment' OR (quantity = 1 AND slot IS NOT NULL))
);
CREATE INDEX item_owner_idx ON game.item_instances(character_id);
CREATE UNIQUE INDEX item_slot_idx ON game.item_instances(character_id, location, slot) WHERE slot IS NOT NULL;
CREATE TABLE game.character_skills (
    character_id uuid NOT NULL REFERENCES game.characters,
    build_id text NOT NULL,
    skill_id integer NOT NULL,
    level integer NOT NULL CHECK (level >= 1),
    PRIMARY KEY(character_id, skill_id),
    FOREIGN KEY(build_id, skill_id) REFERENCES catalog.skills(build_id, template_id)
);
CREATE TABLE audit.events (
    event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    action text NOT NULL,
    character_id uuid REFERENCES game.characters,
    details jsonb NOT NULL DEFAULT '{}'::jsonb
);
CREATE INDEX audit_character_time_idx ON audit.events(character_id, occurred_at);
CREATE TABLE game.item_grants (
    request_id uuid PRIMARY KEY,
    arguments jsonb NOT NULL,
    instance_id uuid NOT NULL UNIQUE REFERENCES game.item_instances DEFERRABLE INITIALLY DEFERRED
);

-- A durable grant primitive, not a complete GM command or stack/slot allocator.
-- Caller must authorize the actor and enforce template-specific gameplay rules.
CREATE FUNCTION game.grant_item(p_character uuid, p_build text, p_template integer,
                               p_quantity bigint, p_request uuid) RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE
    item_id uuid := gen_random_uuid();
    args jsonb := jsonb_build_object('character',p_character,'build',p_build,'template',p_template,'quantity',p_quantity);
    previous game.item_grants%ROWTYPE;
BEGIN
    IF p_quantity IS NULL OR p_quantity <= 0 THEN
        RAISE EXCEPTION 'quantity must be positive';
    END IF;
    INSERT INTO game.item_grants VALUES(p_request,args,item_id) ON CONFLICT DO NOTHING;
    IF NOT FOUND THEN
        SELECT * INTO previous FROM game.item_grants WHERE request_id=p_request;
        IF previous.arguments IS DISTINCT FROM args THEN
            RAISE EXCEPTION 'idempotency key reused with different arguments';
        END IF;
        RETURN previous.instance_id;
    END IF;
    INSERT INTO game.item_instances(instance_id,character_id,build_id,template_id,quantity)
        VALUES(item_id,p_character,p_build,p_template,p_quantity);
    INSERT INTO audit.events(action,character_id,details)
        VALUES('item_granted',p_character,jsonb_build_object('request',p_request,'instance',item_id,'template',p_template,'quantity',p_quantity));
    RETURN item_id;
END $$;

REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON FUNCTION game.grant_item(uuid,text,integer,bigint,uuid) FROM PUBLIC;
GRANT USAGE ON SCHEMA catalog, auth, game, audit TO aion_app;
GRANT SELECT ON ALL TABLES IN SCHEMA catalog, auth TO aion_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA game TO aion_app;
GRANT INSERT ON audit.events TO aion_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA audit TO aion_app;
GRANT EXECUTE ON FUNCTION game.grant_item(uuid,text,integer,bigint,uuid) TO aion_app;
