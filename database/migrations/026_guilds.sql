-- Legions (guilds): created for 100000 Kina (GlobalSetting guild_create_gold), joined by application (EGuildJoinRule
-- 0 AutoJoin, 1 AutoDeny, 2 Manual, 3 Password). One legion per character; grade EGuildMemberGrade 0 Master,
-- 1 Officer, 2 Member.
CREATE TABLE game.guilds (
    guild_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 12),
    level smallint NOT NULL DEFAULT 1,
    exp bigint NOT NULL DEFAULT 0,
    emblem_id integer NOT NULL DEFAULT 0,
    join_rule smallint NOT NULL DEFAULT 0 CHECK (join_rule BETWEEN 0 AND 3),
    password text NOT NULL DEFAULT '',
    intro text NOT NULL DEFAULT '',
    notice text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX guild_name_idx ON game.guilds (lower(name));
CREATE TABLE game.guild_members (
    character_id uuid PRIMARY KEY REFERENCES game.characters(character_id) ON DELETE CASCADE,
    guild_id bigint NOT NULL REFERENCES game.guilds(guild_id) ON DELETE CASCADE,
    grade smallint NOT NULL DEFAULT 2 CHECK (grade BETWEEN 0 AND 3),
    comment text NOT NULL DEFAULT '',
    joined_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX guild_member_guild_idx ON game.guild_members (guild_id);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.guilds, game.guild_members TO aion_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA game TO aion_app;
