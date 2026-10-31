-- Skill levels bought with skill points (SkillLevelUp_RQ) are recorded apart from the level the client shows.
-- character_skills.level can also be raised by the level-managed kit (sync_character_skills, 008), and a kit level above 1
-- would then count as bought: points the character never spent. 0 = nothing bought. (Idea from A2Core PR #4, Artezianec.)
ALTER TABLE game.character_skills ADD COLUMN purchased_level integer NOT NULL DEFAULT 0 CHECK (purchased_level >= 0);
-- Until now every kit unlock was skill level 1 (tools/data/class-skills.json has none above 1), so every stored level
-- above 1 was bought: those purchases are kept.
UPDATE game.character_skills SET purchased_level = level WHERE level > 1;
