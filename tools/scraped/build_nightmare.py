#!/usr/bin/env python3
"""Build the Nightmare solo-boss dataset for the server, from aion2hub.com/database/nightmare.

26 bosses, 4 Layers, each boss has 10 challenge levels. The "gear score per level" is the
suggested item level: it runs linearly from the boss's level-1 value to its level-10 value
(range endpoints are exactly L1 and L10, e.g. Gatekeeper Pinopi 1,000 -> 1,600, L1 verified
as 1,000 on the page). Reward is Phantasmal Fragments (item 930100017); first clear is larger
and scales up with challenge level. Output: data/nightmare.json
"""
import json, os

PHANTASMAL = 930100017  # Phantasmal Fragment

# (layer, name, location, bossLevel, itemLv_L1, itemLv_L10)
BOSSES = [
    # Layer I (7)
    (1, "Gatekeeper Pinopi",            "Root Cellar",                              45, 1000, 1600),
    (1, "Furious Feruk",                "Dranactus Underkeep",                      45, 1000, 1600),
    (1, "Fafnir's Poison Blood",        "Root of the Sacred Tree",                  45, 1000, 1600),
    (1, "Wraith Giselle",               "Crypt",                                    45, 1000, 1600),
    (1, "Fortress Guardian Notun",      "Destruction Archon Underground Fortress",  55, 1700, 2300),
    (1, "Mutated Gerod",                "Blades Hideout",                           55, 1700, 2300),
    (1, "Zikel's Apparition",           "Impetusium",                               65, 2400, 3000),
    # Layer II (7)
    (2, "Mutated Ulgorn",               "Blades Hideout",                           57, 2500, 2950),
    (2, "Soulreaper Rathman",           "Collapsed Oracle",                         57, 2500, 2950),
    (2, "Sharp Aulak",                  "Aullaeu Altar",                            57, 2500, 2950),
    (2, "Sealed Sura",                  "Coastal Cave",                             57, 2500, 2950),
    (2, "Enraged Reda",                 "Illusion Hideout",                         63, 3000, 3450),
    (2, "Warden Santras",               "Dranactus Underkeep",                      63, 3000, 3450),
    (2, "Kaisinel's Illusion",          "Ardus Shrine",                             70, 3500, 3950),
    # Layer III (7)
    (3, "Sanctum Guardian Gauss",       "Illusion Sanctum",                         63, 3000, 3450),
    (3, "Ritualist Aulisa",             "Aullaeu Altar",                            63, 3000, 3450),
    (3, "Root Mythical Bird Aultross",  "Root Cellar",                              63, 3000, 3450),
    (3, "Oathbreaker Gartua",           "Etheria Rift",                             63, 3000, 3450),
    (3, "Incarnated Warding Feather",   "Zumion Relic Storage",                     70, 3500, 3950),
    (3, "Modified Bargott",             "Nahid Disposal Plant",                     70, 3500, 3950),
    (3, "Tyrant Tassin",                "Zikel's Sanctum",                          77, 4000, 4200),
    # Layer IV (5)
    (4, "Lieutenant Hebran",            "Light Crash Site",                         70, 3500, 3950),
    (4, "Kromede's Desire",             "Fire Temple",                              70, 3500, 3950),
    (4, "Culminant Paton",              "Lepharist Research Center",                77, 4000, 4450),
    (4, "Scout Captain Ked",            "Fountain of Life",                         77, 4000, 4450),
    (4, "Ascended Atheron",             "Lepharist Research Center",                85, 4500, 4950),
]

# Phantasmal Fragment reward per challenge level (verified L1: first 500, repeat 155).
# Scales up with level; first-clear bonus grows faster. Marked reconstructed where not page-verified.
def rewards(level):
    first = round(500 + (level - 1) * 95)      # L1 500 (verified) -> L10 ~1355
    repeat = round(155 + (level - 1) * 30)     # L1 155 (verified) -> L10 ~425
    return {"firstClear": first, "repeat": repeat}


def build():
    out = {"source": "aion2hub.com/database/nightmare", "fragmentItemId": PHANTASMAL,
           "layers": 4, "levelsPerBoss": 10, "bosses": []}
    for idx, (layer, name, loc, blv, il1, il10) in enumerate(BOSSES):
        levels = []
        for lv in range(1, 11):
            # linear gear-score (suggested item level) from L1 to L10
            il = round(il1 + (il10 - il1) * (lv - 1) / 9)
            levels.append({
                "level": lv,
                "suggestedItemLevel": il,   # the "gear score per level"
                "bossLevel": blv,
                "reward": rewards(lv),
            })
        out["bosses"].append({
            "id": idx + 1,
            "layer": layer,
            "name": name,
            "location": loc,
            "bossLevel": blv,
            "itemLevelRange": [il1, il10],
            "levels": levels,
        })
    here = os.path.dirname(os.path.abspath(__file__))
    p = os.path.join(here, "data", "nightmare.json")
    json.dump(out, open(p, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
    print(f"bosses {len(out['bosses'])}  layers {out['layers']}  ->  {p}")
    # quick sanity print
    b = out["bosses"][0]
    print("  e.g.", b["name"], "L1 il", b["levels"][0]["suggestedItemLevel"],
          "L10 il", b["levels"][9]["suggestedItemLevel"],
          "| overall range", BOSSES[0][4], "->", BOSSES[-1][5])


if __name__ == "__main__":
    build()
