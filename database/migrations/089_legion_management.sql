-- Legion management (feature/systems-2, 2026-11-10): join applications for the Manual join rule (EGuildJoinRule 2,
-- GlobalSetting guild_join_req_count 3 per applicant, guild_join_recv_count 20 per legion), the grade authority the master
-- sets (GuildSetAuthority_RQ; empty = GuildMemberGradeAuthority.dat defaults), the legion wallet (EGuildWalletType 1
-- EssenceGuild), daily attendance (and its reward claim) and donations per member. Attendance and donation days are the
-- client's attendance day (GlobalSetting attendance_reset_time 16:00 UTC), computed by the server.
-- Merge 2026-11-13 (was 048 on the Dell server): migration 043 (GitHub) already made game.guild_join_requests (the same
-- columns, comments up to 100 characters) and game.guild_members.donations (integer); only the rest is added here.
ALTER TABLE game.guilds
    ADD COLUMN authority jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(authority) = 'object'),
    ADD COLUMN wallet bigint NOT NULL DEFAULT 0 CHECK (wallet >= 0);
ALTER TABLE game.guild_members
    ADD COLUMN attended_on date,
    ADD COLUMN attendance_reward_on date,
    ADD COLUMN donated_on date;
