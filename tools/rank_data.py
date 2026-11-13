"""Client-table summary of the season rankings (feature/rank-system, 2026-11-12).

Reads the exported client tables (rows at ['Properties']['Data']; file names "<Table>.dat.json" or "Table_<Table>.json") and
writes tools/data/rank.json, embedded by Aion.World (RankTables):
- seasons: SeasonSchedule rows of the main ranking schedule groups (Season_Main_Live, Season_Main): number, start, end, reward
  period (ms since the epoch, UTC). The table ticks are KST (fix/rank-rewards 2026-11-13, OBSERVED): the official Abyss tier buff
  (abnormal 20100, 0x382b / 0x3645 of recordings 20261106-115817 and 20261107-211036, 397 samples) expires at the end of
  Season_Main_Live 1 = 2026-12-16 07:00:00.05 UTC, i.e. the table's 2026-12-16 16:00 read as KST (as the shops' and tickets'
  16:00 resets, OBSERVED 07:00 UTC);
- contents: SeasonContents rows with a ranking (DisplayRankingGrade / RankingGradeGroup / RankingClassGradeGroup / reward groups)
  per schedule group, season number and ESeasonContentsType;
- ranking: SeasonRanking per ESeasonContentsType (MinRankingPoint, UpdatePeriod minutes);
- grades: SeasonRankingGrade groups {grade, rank_max (0 = any), pct_max (RankPercentMax, top percent; 0 = none), need
  (NeedRankingPoint)}. RankMin is not used: the official lists (recording 20261106-115817, 8 lists) give grade g to rank r with
  point p for the first g where r <= RankMax and p >= NeedRankingPoint (rank 2 with 482,850 boss points is grade 6);
- grade_names: SeasonRankingGradeInfo groups (the L10N title keys);
- rewards: SeasonRankingReward groups {class, grade, light/dark items [[item id, count]], light/dark buffs [[abnormal, level]]}
  with the SeasonRankingRewardItem lists resolved to Item.dat ids;
- buffs: the reward buffs' abnormals (SkillAbnormal 20100-20106 "<content> Grade Rewards") {group, levels {level: {stat: value}}}:
  their StatChange rows (SkillAbnormalEffect) at each level (SkillAbnormalEffectLv);
- weekly: the mid-season (weekly) ranking rewards of GlobalSetting: midseason_ranking_reward_use / _group_abyss (Weekly_Reward_Abyss,
  by the whole list's grade), midseason_class_ranking_reward_use / midseason_class_ranking_reward_group_abyss (Weekly_Reward_Class_Abyss,
  by the class grade), weekly_reset_day (EDayOfWeek, 3 Wednesday) and weekly_quest_reset_time (ms of the day, KST);
- suppression: GlobalSetting suppression_rank_point_* (basic score and the difficulty factors of the Subjugation rank points).

Usage: python tools/rank_data.py [table dir] [out file]
"""
import json
import sys
from pathlib import Path

DEFAULT_TABLES = Path(r"D:\A2Core-local\client-full-20261109-0958\tables\AION2\Content\Data\Table")
OUT = Path(__file__).resolve().parent / "data" / "rank.json"
SCHEDULE_GROUPS = ("Season_Main_Live", "Season_Main")
CLASSES = {"None": 0, "Gladiator": 2, "Templar": 3, "Ranger": 4, "Assassin": 5, "Elementalist": 6, "Sorcerer": 7, "Cleric": 8,
           "Chanter": 9, "Fighter": 12}
CONTENTS = {"Abyss": 1, "SeasonMission": 2, "BossChallenge": 3, "PartyChallenge": 4, "ArenaSingle": 5, "ArenaTeam": 6,
            "ArenaStrategy": 7, "Conqueror": 9, "Suppression": 20, "Awaken": 21, "ItemSuccession": 27}


def rows(tables, name):
    path = tables / f"{name}.dat.json"
    if not path.exists():
        path = tables / f"Table_{name}.json"
    data = json.loads(path.read_text(encoding="utf-8"))["Properties"]["Data"]
    return list(data.values()) if isinstance(data, dict) else data


def enum(v):
    return v.split("::", 1)[1] if isinstance(v, str) and "::" in v else v


KST_MS = 9 * 3_600_000


def ms(ticks):
    """A table date (KST wall time) as Unix ms (UTC)."""
    return (ticks["Ticks"] - 621355968000000000) // 10_000 - KST_MS


def none(v):
    return "" if v in (None, "None") else v


def main():
    tables = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_TABLES
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else OUT
    item_ids = {r["Name"]: r["ID"]["Value"] for r in rows(tables, "Item")}
    reward_items = {r["Name"]: [[item_ids[i["Item"]], i["Count"]] for i in r["Items"] if i["Item"] in item_ids]
                    for r in rows(tables, "SeasonRankingRewardItem")}
    result = {
        "seasons": [
            {"group": r["GroupName"], "no": r["SeasonNumber"], "start": ms(r["StartDate"]), "end": ms(r["EndDate"]),
             "reward_start": ms(r["RewardStartDate"]), "reward_end": ms(r["RewardEndDate"])}
            for r in rows(tables, "SeasonSchedule") if r["GroupName"] in SCHEDULE_GROUPS],
        "contents": [
            {"group": r["ScheduleGroupName"], "no": r["SeasonNumber"], "type": CONTENTS[enum(r["ContentsType"])],
             "display": enum(r["DisplayRankingGrade"]), "grade_group": none(r["RankingGradeGroup"]),
             "class_grade_group": none(r["RankingClassGradeGroup"]), "grade_info": none(r["RankingGradeInfoGroup"]),
             "reward_common": none(r["RewardCommonGroupName"]), "reward_class": none(r["RewardClassGroupName"])}
            for r in rows(tables, "SeasonContents")
            if r["ScheduleGroupName"] in SCHEDULE_GROUPS and enum(r["ContentsType"]) in CONTENTS],
        "ranking": [{"type": CONTENTS[enum(r["ContentsType"])], "min_point": r["MinRankingPoint"], "update_min": r["UpdatePeriod"]}
                    for r in rows(tables, "SeasonRanking")],
        "grades": {},
        "grade_names": {},
        "rewards": {},
    }
    for r in sorted(rows(tables, "SeasonRankingGrade"), key=lambda r: (r["GroupName"], r["Grade"])):
        result["grades"].setdefault(r["GroupName"], []).append(
            {"grade": r["Grade"], "rank_max": r["RankMax"], "pct_max": r["RankPercentMax"], "need": r["NeedRankingPoint"]})
    for r in rows(tables, "SeasonRankingGradeInfo"):
        result["grade_names"].setdefault(r["GroupName"], {})[str(r["Grade"])] = r["GradeName"]["Key"]
    for r in rows(tables, "SeasonRankingReward"):
        result["rewards"].setdefault(r["GroupName"], []).append({
            "class": CLASSES.get(enum(r["CharacterClass"]), 0), "grade": r["Grade"],
            "light": reward_items.get(r["RewardItemLight"], []), "dark": reward_items.get(r["RewardItemDark"], []),
            "light_buffs": [[b["AbnormalTableItemId"]["Value"], b["Level"]] for b in r["LightRewardBuffList"]],
            "dark_buffs": [[b["AbnormalTableItemId"]["Value"], b["Level"]] for b in r["DarkRewardBuffList"]]})
    # Only the groups a ranked SeasonContents row names, the weekly Abyss rewards and the Conqueror grades (SeasonRanking
    # Conqueror: grades by rank only, ranking_grade_conqueror / ranking_grade_info_conqueror; no season ranks it yet).
    used = {c[k] for c in result["contents"] for k in ("grade_group", "class_grade_group", "grade_info", "reward_common", "reward_class")}
    for key in ("grades", "grade_names", "rewards"):
        result[key] = {k: v for k, v in result[key].items() if k in used or k.startswith("Weekly_") or "conqueror" in k}
    setting = rows(tables, "GlobalSetting")[0]
    result["weekly"] = {
        "day": setting["weekly_reset_day"], "time_ms": setting["weekly_quest_reset_time"]["Ticks"] // 10_000,
        "common_use": bool(setting["midseason_ranking_reward_use"]), "common_group": none(setting["midseason_ranking_reward_group_abyss"]),
        "class_use": bool(setting["midseason_class_ranking_reward_use"]), "class_group": none(setting["midseason_class_ranking_reward_group_abyss"])}
    result["suppression"] = {k[len("suppression_rank_point_"):]: v for k, v in setting.items() if k.startswith("suppression_rank_point_")}
    abnormals = sorted({b[0] for g in result["rewards"].values() for r in g for b in r["light_buffs"] + r["dark_buffs"]})
    levels = {}
    for r in rows(tables, "SkillAbnormalEffectLv"):
        levels.setdefault(r["AbnormalEffectLevelGroupId"], {})[r["AbnormalEffectLevel"]] = r["Values"]
    groups = {r["ID"]["Value"]: (r["GroupId"], r["AbnormalEffectLevelMax"]) for r in rows(tables, "SkillAbnormal") if r["ID"]["Value"] in abnormals}
    result["buffs"] = {}
    for a in abnormals:
        group, level_max = groups.get(a, (a, 1))
        effects = [e for e in rows(tables, "SkillAbnormalEffect") if e["AbnormalId"]["Value"] == a and enum(e["AbnormalEffectType"]) == "StatChange"]
        by_level = {}
        for level in range(1, level_max + 1):
            stats = {}
            for e in effects:
                values = levels.get(e["AbnormalEffectLvGroupId"], {}).get(level, e["Values"])
                stats[values[0]] = stats.get(values[0], 0) + int(values[1])
            by_level[str(level)] = stats
        result["buffs"][str(a)] = {"group": group, "levels": by_level}
    out.write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(f"wrote {out}: {len(result['seasons'])} seasons, {len(result['contents'])} ranked contents, "
          f"{len(result['grades'])} grade groups, {len(result['rewards'])} reward groups")


if __name__ == "__main__":
    main()
