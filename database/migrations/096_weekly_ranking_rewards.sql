-- Weekly (mid-season) ranking rewards (fix/rank-rewards): one row per ESeasonContentsType and weekly reset that was paid out.
-- GlobalSetting midseason_class_ranking_reward_use / midseason_class_ranking_reward_group_abyss (Weekly_Reward_Class_Abyss): at each
-- weekly reset (weekly_reset_day 3 = Wednesday, 16:00 KST = 07:00 UTC) the Abyss class grades 1-4 get their reward by mail
-- (Post.dat 26 "Abyss Class Rank Reward has been sent.", valid 7 days). The row and the mails are written in one transaction, so a
-- week is paid at most once, also after a restart or when several sessions reach the reset together (the primary key).
CREATE TABLE game.season_weekly_settlements (
    contents_type integer NOT NULL,
    week_end timestamptz NOT NULL,
    season_no integer NOT NULL,
    mails integer NOT NULL DEFAULT 0,
    settled_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (contents_type, week_end)
);
GRANT SELECT, INSERT, UPDATE ON game.season_weekly_settlements TO aion_app;
