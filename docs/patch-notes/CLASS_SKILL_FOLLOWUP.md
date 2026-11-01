# Follow-up: Oct 28 2026 class / skill data (not implemented)

Checklist for a future PR after re-exporting client tables. Sources: [2026-10-28-global.md](./2026-10-28-global.md).

## Base HP at level 45

- `tools/data/levels.json` (or class level stat tables from `tools/levels.py` / character growth export)
- `server/Aion.World/Combat.cs` or wherever max HP is computed from class + level
- Character login / `GameSession` stat packets if base HP is sent explicitly
- Tests: any fixed HP assertions in `tests/` or Native checks

## Assassin — Illusive Clone cooldown

- Skill definition: `tools/data/class-skills.json` or embedded skill table used by `GameData`
- Cooldown application: skill cast / buff code in `GameSession.cs` (cast acceptance, cooldown sync packets)
- Verify skill id/name mapping to **Illusive Clone** in Skill.dat export

## Assassin — Heightened Sixth Sense HP effect

- Abnormal or skill level table linking levels 5/10/20 → 1000/2000/4000 HP
- `Combat.cs` / abnormal handlers if HP bonus is applied as a buff
- PvP and PvE tests if abnormal stacking is covered

## Dungeon / boss nerfs (optional, separate from class patch)

- Expedition / Transcendence boss templates in NPC or instance data
- Map event or dungeon scripts if attack/damage boost are hard-coded

## Gathering cap

- If emulated: gather session limits in `GameSession.Gather.cs` + config
- Not required for combat/quest parity
