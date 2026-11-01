-- Skill levels bought with skill points (SkillLevelUp_RQ) are recorded apart from the level the client shows.
-- character_skills.level is also written by the level-managed kit (sync_character_skills), so counting every level above
-- 1 as spent charged characters for points they never spent and refused every purchase. Rows start at 0 (nothing bought):
-- earlier purchases cannot be told from kit levels and are refunded.
ALTER TABLE game.character_skills ADD COLUMN purchased_level integer NOT NULL DEFAULT 0 CHECK (purchased_level >= 0);
