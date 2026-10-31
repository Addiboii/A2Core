-- Align legion attendance rollover with game-day (07:00 UTC), not raw UTC midnight.
COMMENT ON COLUMN game.guilds.attendance_day IS 'Last game-day (UTC, shifted by daily reset hour) attendance was rolled';
COMMENT ON COLUMN game.guild_members.donation_day IS 'Last game-day daily donation count was reset';
