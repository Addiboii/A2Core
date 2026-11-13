-- QuickSlotInfo._context_skill_ids: the skills a slot offers besides its own (the Q/E slots 6 and 7: official Sorcerer
-- bar 15220000 + 15150000, 15010000 + 15050000 + 15310000). Dropping them left those slots without their skills.
ALTER TABLE game.quick_slots ADD COLUMN context_skills integer[] NOT NULL DEFAULT '{}';
