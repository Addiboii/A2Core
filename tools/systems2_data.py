"""Client-table summary for the community and PvP menus (feature/systems-2, 2026-11-10).

Reads the exported client tables (rows at ['Properties']['Data']) and writes tools/data/systems2.json, embedded by
Aion.World (Systems2Tables): legion levels and buffs (GuildData), grade authority defaults (GuildMemberGradeAuthority),
emblems (GuildEmblem), legion storage extension costs (GuildStorageExtension), legion halls (GuildAgit), the arena and
battlefield matching (Matching, MatchingContents, MatchingSchedule, RewardArena, Dungeon map ids), field events and the
Shugo festival (FieldEvent, EventSchedule), Abyss artifacts (AbyssArtifact, AbyssArtifactEvent), rifts (RiftData), season
ranking (SeasonRanking) and the GlobalSetting values these menus use.

Usage: python tools/systems2_data.py [table dir] [out file]
"""
import json
import sys
from pathlib import Path

DEFAULT_TABLES = Path(r"D:\A2Core-local\client-full-20261109-0958\tables\AION2\Content\Data\Table")
OUT = Path(__file__).resolve().parent / "data" / "systems2.json"

GLOBAL_KEYS = [
    "guild_join_level", "guild_create_gold", "guild_join_req_count", "guild_join_recv_count", "l10n_guild_name_max",
    "l10n_guild_notice_max_length", "l10n_guild_intro_max_length", "l10n_guild_member_comment_max_length",
    "l10n_guild_password_min_length", "l10n_guild_password_max_length", "guild_storage_slot_count_default",
    "guild_storage_slot_count_per_extension", "guild_info_history_max_count", "delete_reputation_value_rate_for_leaving_guild",
    "Community_friend_max_count", "Community_friend_request_max_count", "Community_friend_block_max_count",
    "post_load_count", "abyss_ranking_base_level", "attendance_reset_time", "season_mission_reset_time",
    "season_mission_reset_day", "arena_countdown_time", "arena_result_notice_time", "dungeon_matching_complete_popup_ready_waiting_time",
    "party_invite_recommend_list_max_count", "party_auto_delay", "rift_contentsticket_default_time", "rift_pvp_on_activation_level",
]


def rows(tables, name):
    data = json.loads((tables / f"{name}.dat.json").read_text(encoding="utf-8"))["Properties"]["Data"]
    return list(data.values()) if isinstance(data, dict) else data


def value(v):
    if isinstance(v, dict):
        if "Value" in v:
            return v["Value"]
        if "Ticks" in v:
            return v["Ticks"] // 10_000  # ms
        if "Key" in v:
            return v["Key"]
    return v


def enum(v):
    return v.split("::", 1)[1] if isinstance(v, str) and "::" in v else v


def main():
    tables = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_TABLES
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else OUT
    g = rows(tables, "GlobalSetting")[0]
    dungeons = {r["ID"]["Value"]: r for r in rows(tables, "Dungeon")}
    result = {
        "global": {k: value(g[k]) for k in GLOBAL_KEYS if k in g},
        "guild_levels": [
            {"level": r["Level"], "accumulate_exp": r["AccumulateExp"], "max_members": r["MaxMemberNum"], "officers": r["OfficerNum"],
             "stats": [{"stat": enum(s["Type"]), "value": s["Value"]} for s in r["GuildStatOptionList"] if enum(s["Type"]) != "None"]}
            for r in rows(tables, "GuildData")],
        "guild_authority": {enum(r["Type"]): r["AuthorityFlags"] for r in rows(tables, "GuildMemberGradeAuthority")},
        "guild_emblems": [{"id": r["ID"]["Value"], "unlock_level": r["UnLockGuildLevel"]} for r in rows(tables, "GuildEmblem")],
        "guild_storage_extensions": [{"id": r["ID"]["Value"], "cost": r["Cost"], "currency": enum(r["CurrencyType"]), "level": r["NeedGuildLevel"]}
                                     for r in rows(tables, "GuildStorageExtension")],
        "guild_agits": [{"id": r["ID"]["Value"], "race": enum(r["Race"]), "map": value(r["MapId"]), "base_map": value(r["BaseMapId"]),
                         "level": r["RentalLevel"], "days": r["RentalDay"], "cost": r["RentalCost"]} for r in rows(tables, "GuildAgit")],
        "matching": [
            {"id": r["ID"]["Value"], "type": enum(r["MatchingType"]), "teams": r["TeamCount"], "team_size": r["TeamMemberCount"],
             "party_size": r["PartyMemberCount"], "hidden": r["bTabHide"], "reward_join": r["RewardJoin"], "reward_win": r["RewardWin"],
             "schedule_group": r["MatchingScheduleGroupId"],
             "contents": [{"id": value(c["TableId"]), "prob": c["Prob"]} for c in r["ContentsInfos"]]}
            for r in rows(tables, "Matching")],
        "matching_contents": [
            {"id": r["ID"]["Value"], "type": enum(r["MatchingContentType"]), "values": r["ContentValues"], "dungeon": value(r["DungeonId"]),
             "map": value(dungeons[value(r["DungeonId"])]["MapId"]) if value(r["DungeonId"]) in dungeons else 0,
             "play_ms": r["PlayTime"], "lives": r["Life"], "respawn_ms": r["RespawnTime"]}
            for r in rows(tables, "MatchingContents")],
        "matching_schedule": [
            {"group": r["GroupId"], "day": enum(r["Day"]), "windows": [[value(t["StartTime"]), value(t["EndTime"])] for t in r["TimeInfos"]]}
            for r in rows(tables, "MatchingSchedule")],
        "arena_rewards": {r["Name"]: [{"item": i["Item"], "count": i["Count"]} for i in r["Items"]] for r in rows(tables, "RewardArena")},
        "field_events": [
            {"id": r["ID"]["Value"], "name": r["EventName"], "kind": enum(r["FieldEventKind"]), "contents": enum(r["ContentsType"]),
             "value": r["ContentsValue"], "group": r["EventGroup"], "map": value(r["MapId"]), "entrance": enum(r["EntranceType"]),
             "entrance_map": value(r["EntranceMapId"]), "progress_s": r["ProgressTime"], "ready_s": r["ReadyDuration"],
             "start_members": r["StartMemberCount"], "title": value(r["Title"])}
            for r in rows(tables, "FieldEvent")],
        "event_schedules": [
            {"id": r["ID"]["Value"], "days": [d for d in ("Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday") if r["b" + d]],
             "times_ms": [value(t) for t in r["ActiveTimeList"]],
             "events": [{"category": enum(e["Category"]), "key": e["Key"]} for e in r["EventList"]]}
            for r in rows(tables, "EventSchedule")],
        "abyss_artifacts": [
            {"id": r["ID"]["Value"], "name": r["Name"], "event": r["AbyssEventName"], "teleport": r["Teleport"],
             "cost_item": r["TeleportCostItemName"], "cost_count": r["TeleportCostItemCount"]} for r in rows(tables, "AbyssArtifact")],
        "abyss_events": [{"id": r["ID"]["Value"], "name": r["Name"], "map": value(r["MapId"]), "pre_end_s": r["PreEndTime"]}
                         for r in rows(tables, "AbyssArtifactEvent")],
        "rifts": [{"id": r["ID"]["Value"], "map": value(r["ArrivalMapId"]), "min_level": r["MinLevel"], "max_level": r["MaxLevel"],
                   "life_s": r["LifeTime"], "costs": [{"item": c["Item"], "count": c["Count"]} for c in r["CostList"]]} for r in rows(tables, "RiftData")],
        "season_ranking": [{"id": r["ID"]["Value"], "contents": enum(r["ContentsType"]), "min_point": r["MinRankingPoint"], "update_s": r["UpdatePeriod"]}
                           for r in rows(tables, "SeasonRanking")],
        "posts": [{"id": r["ID"]["Value"], "contents": enum(r["UseContents"]), "tab": enum(r["SaveTab"]), "params": [enum(p) for p in r["Params"]]}
                  for r in rows(tables, "Post")],
    }
    # Item.dat ids of the item names the rewards and costs above use (AbyssPoint, Medal2, GoldCombined, teleport scrolls).
    names = {i["item"] for items in result["arena_rewards"].values() for i in items}
    names |= {c["item"] for r in result["rifts"] for c in r["costs"]} | {a["cost_item"] for a in result["abyss_artifacts"]}
    result["item_ids"] = {r["Name"]: r["ID"]["Value"] for r in rows(tables, "Item") if r["Name"] in names}
    out.write_text(json.dumps(result, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {out}: {len(result['guild_levels'])} legion levels, {len(result['matching'])} matchings, "
          f"{len(result['field_events'])} field events, {len(result['posts'])} posts")


if __name__ == "__main__":
    main()
