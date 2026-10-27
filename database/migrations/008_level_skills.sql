-- Server policy: auto-learned skills follow character level (tools/class_skills.py).
-- Rows are installed by `python tools/class_skills.py install`; one row per creation
-- (class/race) and skill with the lowest level that unlocks it.
CREATE TABLE game.skill_unlocks (
    build_id text NOT NULL,
    creation_id integer NOT NULL,
    skill_id integer NOT NULL,
    need_level integer NOT NULL CHECK(need_level>=1),
    skill_level integer NOT NULL CHECK(skill_level>=1),
    PRIMARY KEY(build_id, creation_id, skill_id),
    FOREIGN KEY(build_id, skill_id) REFERENCES catalog.skills(build_id, template_id)
);

-- Make a character's level-managed skills equal the unlocks at its level: add newly
-- unlocked ones and drop those above it (with their skill-bar slots). Skills learned
-- another way (not in skill_unlocks) are never touched. Uses the latest catalogue build.
CREATE FUNCTION game.sync_character_skills(p_character uuid, p_creation integer, p_level integer)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    build text;
BEGIN
    SELECT build_id INTO build FROM catalog.builds ORDER BY imported_at DESC, build_id LIMIT 1;
    IF build IS NULL THEN RETURN; END IF;
    DELETE FROM game.skill_slots s USING game.skill_unlocks u
     WHERE s.character_id=p_character AND u.build_id=build AND u.creation_id=p_creation
       AND u.skill_id=s.skill_id AND u.need_level>p_level;
    DELETE FROM game.character_skills k USING game.skill_unlocks u
     WHERE k.character_id=p_character AND u.build_id=build AND u.creation_id=p_creation
       AND u.skill_id=k.skill_id AND u.need_level>p_level;
    INSERT INTO game.character_skills(character_id, build_id, skill_id, level)
    SELECT p_character, build, u.skill_id, u.skill_level FROM game.skill_unlocks u
     WHERE u.build_id=build AND u.creation_id=p_creation AND u.need_level<=p_level
    ON CONFLICT (character_id, skill_id) DO UPDATE SET level=GREATEST(game.character_skills.level, EXCLUDED.level);
END $$;

CREATE FUNCTION game.sync_level_skills() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM game.sync_character_skills(NEW.character_id, NEW.creation_id, NEW.level);
    RETURN NEW;
END $$;
-- Creation, GM level changes and world saves all pass through this trigger.
CREATE TRIGGER character_level_skills AFTER INSERT OR UPDATE OF level, creation_id ON game.characters
    FOR EACH ROW EXECUTE FUNCTION game.sync_level_skills();

GRANT SELECT ON game.skill_unlocks TO aion_app;
REVOKE ALL ON FUNCTION game.sync_level_skills() FROM PUBLIC;
REVOKE ALL ON FUNCTION game.sync_character_skills(uuid,integer,integer) FROM PUBLIC;
-- Called from the trigger as the application role.
GRANT EXECUTE ON FUNCTION game.sync_character_skills(uuid,integer,integer) TO aion_app;
