-- Season rankings (feature/rank-system): the ranking points per character, ESeasonContentsType and season (SeasonSchedule
-- Season_Main_Live number) and the final results of an ended season (MyPrevSeasonRanking_NT, TakePrevSeasonRankingReward_RQ).
-- point: the ranking point (boss challenge / Transcendence / Ascension trial: the season's clear points summed; Abyss: the
-- season's Abyss Points; arena: the rating); best_point and extra: the best clear (the lists' order and display JSON).
CREATE TABLE game.season_ranking_points (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    contents_type integer NOT NULL,
    season_no integer NOT NULL,
    point bigint NOT NULL DEFAULT 0,
    best_point bigint NOT NULL DEFAULT 0,
    extra text NOT NULL DEFAULT '',
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, contents_type, season_no)
);
CREATE INDEX season_ranking_points_order_idx ON game.season_ranking_points (contents_type, season_no, best_point DESC, point DESC);
-- rank / total: the whole list; class_rank / class_total: the character's class list (the class rewards); grade per list.
CREATE TABLE game.season_ranking_results (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    contents_type integer NOT NULL,
    season_no integer NOT NULL,
    class integer NOT NULL,
    rank integer NOT NULL,
    total integer NOT NULL,
    class_rank integer NOT NULL,
    class_total integer NOT NULL,
    point bigint NOT NULL,
    extra text NOT NULL DEFAULT '',
    grade integer NOT NULL,
    class_grade integer NOT NULL,
    grade_display integer NOT NULL,
    reward_taken boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, contents_type, season_no)
);
CREATE INDEX season_ranking_results_season_idx ON game.season_ranking_results (season_no, contents_type);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.season_ranking_points, game.season_ranking_results TO aion_app;

-- The points kept before this migration, as season 1 (Season_Main_Live 1: 2026-09-30 16:00 to 2026-12-16 16:00 UTC): the boss
-- challenge records (migration 033: every group's cumulative points, the best group's clear) and the setting "season_points"
-- (Transcendence 4 / Ascension trial 21: {"point", "best", "extra"}).
INSERT INTO game.season_ranking_points (character_id, contents_type, season_no, point, best_point, extra)
SELECT b.character_id, 3, 1, sum(b.cumulative_point),
       max(b.best_point),
       (SELECT format('{"boss_group_id":%s,"best_clear_point_level":%s,"best_clear_point_time":%s,"best_clear_point":%s}',
                      x.group_id, x.best_level, x.best_time_ms / 1000, x.best_point)
        FROM game.character_boss_challenges x WHERE x.character_id = b.character_id ORDER BY x.best_point DESC LIMIT 1)
FROM game.character_boss_challenges b
GROUP BY b.character_id
HAVING sum(b.cumulative_point) > 0
ON CONFLICT DO NOTHING;
INSERT INTO game.season_ranking_points (character_id, contents_type, season_no, point, best_point, extra)
SELECT s.character_id, e.key::integer, 1, (e.value->>'point')::bigint, coalesce((e.value->>'best')::bigint, 0), coalesce(e.value->>'extra', '')
FROM game.character_settings s, jsonb_each(s.value) e
WHERE s.name = 'season_points' AND jsonb_typeof(s.value) = 'object' AND e.key ~ '^[0-9]+$' AND jsonb_typeof(e.value) = 'object'
  AND (e.value->>'point') ~ '^[0-9]+$' AND (e.value->>'point')::bigint > 0
ON CONFLICT DO NOTHING;
