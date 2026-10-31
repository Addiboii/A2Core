-- Moving the game to a newer catalogue build (tools/postgres_dev.py upgrade-catalog, after import-catalog).
-- Catalogue builds are immutable snapshots; items, skills and policy rows name the build they were made from, and
-- game.grant_item / the level-skill sync use the newest one. Without a move, an item held from the old build and
-- the same item granted from the new one would sit in separate stacks, and equipment/starter checks would look up
-- rules under the old build.
--
-- catalog.adopt_build(new) makes the newest build the one everything points at:
-- - equipment rules and skill unlocks are copied to the new build for templates/skills it still has;
-- - starter profiles move when every item and skill they name exists in the new build;
-- - item instances and character skills move when their template/skill exists in the new build; the rest stay on
--   their old build (its catalogue rows are kept, so nothing is deleted and the foreign keys still hold) and are
--   reported per template for staff to decide;
-- - plain bag stacks that now share build and template are merged (game.merge_item_stacks, the 034 merge).
-- Repeatable: a second call moves nothing more.

CREATE OR REPLACE FUNCTION game.merge_item_stacks() RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    grp record;
    row record;
    keeper uuid;
    keeper_qty bigint;
    max_stack integer;
    merged integer := 0;
BEGIN
    FOR grp IN SELECT character_id, build_id, template_id FROM game.item_instances
               WHERE location = 'bag' AND upgrades = '{}'::jsonb AND rolled_stats = '{}'::jsonb
               GROUP BY character_id, build_id, template_id HAVING count(*) > 1
    LOOP
        max_stack := game.item_stack_max(grp.build_id, grp.template_id);
        CONTINUE WHEN max_stack = 1;
        keeper := NULL;
        FOR row IN SELECT instance_id, quantity FROM game.item_instances
                   WHERE character_id = grp.character_id AND build_id = grp.build_id AND template_id = grp.template_id
                     AND location = 'bag' AND upgrades = '{}'::jsonb AND rolled_stats = '{}'::jsonb
                   ORDER BY created_at, instance_id FOR UPDATE
        LOOP
            IF keeper IS NULL OR (max_stack <> 0 AND keeper_qty >= max_stack) THEN
                keeper := row.instance_id;
                keeper_qty := row.quantity;
                CONTINUE;
            END IF;
            IF max_stack = 0 OR keeper_qty + row.quantity <= max_stack THEN
                UPDATE game.item_instances SET quantity = quantity + row.quantity, revision = revision + 1 WHERE instance_id = keeper;
                -- Grant records keep pointing at a live row (they only serve idempotent replays).
                UPDATE game.item_grants SET instance_id = keeper WHERE instance_id = row.instance_id;
                DELETE FROM game.item_instances WHERE instance_id = row.instance_id;
                keeper_qty := keeper_qty + row.quantity;
                merged := merged + 1;
            ELSE
                UPDATE game.item_instances SET quantity = max_stack, revision = revision + 1 WHERE instance_id = keeper;
                UPDATE game.item_instances SET quantity = quantity - (max_stack - keeper_qty), revision = revision + 1 WHERE instance_id = row.instance_id;
                keeper := row.instance_id;
                keeper_qty := row.quantity - (max_stack - keeper_qty);
            END IF;
        END LOOP;
    END LOOP;
    RETURN merged;
END $$;

CREATE OR REPLACE FUNCTION catalog.adopt_build(p_build text) RETURNS jsonb
LANGUAGE plpgsql AS $$
DECLARE
    rules bigint; unlocks bigint; profiles bigint; items bigint; skills bigint; merged integer;
    kept_items jsonb; kept_skills jsonb; kept_profiles jsonb;
BEGIN
    IF NOT EXISTS (SELECT FROM catalog.builds WHERE build_id = p_build) THEN
        RAISE EXCEPTION 'Catalogue build % is not imported', p_build;
    END IF;
    IF p_build <> (SELECT build_id FROM catalog.builds ORDER BY imported_at DESC, build_id LIMIT 1) THEN
        RAISE EXCEPTION 'Catalogue build % is not the newest import', p_build;
    END IF;

    INSERT INTO game.equipment_rules(build_id, template_id, slot, minimum_level, creation_ids, evidence)
    SELECT DISTINCT ON (r.template_id, r.slot) p_build, r.template_id, r.slot, r.minimum_level, r.creation_ids, r.evidence
      FROM game.equipment_rules r JOIN catalog.items c ON c.build_id = p_build AND c.template_id = r.template_id
     WHERE r.build_id <> p_build
     ORDER BY r.template_id, r.slot, (SELECT imported_at FROM catalog.builds b WHERE b.build_id = r.build_id) DESC
    ON CONFLICT (build_id, template_id, slot) DO NOTHING;
    GET DIAGNOSTICS rules = ROW_COUNT;

    INSERT INTO game.skill_unlocks(build_id, creation_id, skill_id, need_level, skill_level)
    SELECT DISTINCT ON (u.creation_id, u.skill_id) p_build, u.creation_id, u.skill_id, u.need_level, u.skill_level
      FROM game.skill_unlocks u JOIN catalog.skills c ON c.build_id = p_build AND c.template_id = u.skill_id
     WHERE u.build_id <> p_build
     ORDER BY u.creation_id, u.skill_id, (SELECT imported_at FROM catalog.builds b WHERE b.build_id = u.build_id) DESC
    ON CONFLICT (build_id, creation_id, skill_id) DO NOTHING;
    GET DIAGNOSTICS unlocks = ROW_COUNT;

    UPDATE game.starter_profiles p SET build_id = p_build
     WHERE p.build_id <> p_build
       AND NOT EXISTS (SELECT FROM jsonb_array_elements(COALESCE(p.definition->'items', '[]'::jsonb)) e
                        WHERE NOT EXISTS (SELECT FROM catalog.items c WHERE c.build_id = p_build AND c.template_id = (e->>'templateId')::integer))
       AND NOT EXISTS (SELECT FROM jsonb_array_elements_text(COALESCE(p.definition->'skills', '[]'::jsonb)) s
                        WHERE NOT EXISTS (SELECT FROM catalog.skills c WHERE c.build_id = p_build AND c.template_id = s::integer));
    GET DIAGNOSTICS profiles = ROW_COUNT;

    UPDATE game.item_instances i SET build_id = p_build
     WHERE i.build_id <> p_build
       AND EXISTS (SELECT FROM catalog.items c WHERE c.build_id = p_build AND c.template_id = i.template_id);
    GET DIAGNOSTICS items = ROW_COUNT;

    UPDATE game.character_skills k SET build_id = p_build
     WHERE k.build_id <> p_build
       AND EXISTS (SELECT FROM catalog.skills c WHERE c.build_id = p_build AND c.template_id = k.skill_id);
    GET DIAGNOSTICS skills = ROW_COUNT;

    merged := game.merge_item_stacks();

    SELECT COALESCE(jsonb_agg(jsonb_build_object('template', template_id, 'build', build_id, 'rows', n, 'quantity', q) ORDER BY template_id), '[]')
      INTO kept_items
      FROM (SELECT template_id, build_id, count(*) n, sum(quantity) q FROM game.item_instances
             WHERE build_id <> p_build GROUP BY template_id, build_id) k;
    SELECT COALESCE(jsonb_agg(jsonb_build_object('skill', skill_id, 'build', build_id, 'characters', n) ORDER BY skill_id), '[]')
      INTO kept_skills
      FROM (SELECT skill_id, build_id, count(*) n FROM game.character_skills WHERE build_id <> p_build GROUP BY skill_id, build_id) k;
    SELECT COALESCE(jsonb_agg(profile_id ORDER BY profile_id), '[]') INTO kept_profiles
      FROM game.starter_profiles WHERE build_id <> p_build;

    INSERT INTO audit.events(action, details)
    VALUES ('catalog_build_adopted', jsonb_build_object('build', p_build, 'equipment_rules', rules, 'skill_unlocks', unlocks,
            'starter_profiles', profiles, 'items', items, 'skills', skills, 'merged_stacks', merged,
            'kept_items', kept_items, 'kept_skills', kept_skills, 'kept_profiles', kept_profiles));
    RETURN jsonb_build_object('build', p_build, 'equipment_rules', rules, 'skill_unlocks', unlocks, 'starter_profiles', profiles,
                              'items', items, 'skills', skills, 'merged_stacks', merged,
                              'kept_items', kept_items, 'kept_skills', kept_skills, 'kept_profiles', kept_profiles);
END $$;
