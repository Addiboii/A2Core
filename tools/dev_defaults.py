"""Development wire placeholders shared with server/Aion.World/DevelopmentDefaults.cs."""

ACCOUNT_SID = "1000001"
NULL_GUID = "00000000-0000-0000-0000-000000000000"
WORLD_ID = 1204
WORLD_ID_STRING = "1204"
OPENING_QUEST_LIGHT = "HQ1100010"
OPENING_QUEST_DARK = "HQ2100010"


def opening_quest_for_race(race: int) -> str:
    return OPENING_QUEST_DARK if race == 2 else OPENING_QUEST_LIGHT


def account_session(guid: str) -> str:
    return f"{ACCOUNT_SID}:{guid}"
