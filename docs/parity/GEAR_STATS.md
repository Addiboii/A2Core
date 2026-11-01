# Equipment stat aggregation (server)

The world server builds combat stats from every equipped item, not only enchant / title / Daevanion / set bonuses.

## Sources (per stat)

| Source | Applied via |
|--------|-------------|
| Item.dat **MainStats** (all slots) | `tools/data/items.json` → `EquipTemplateStat` |
| **Random options** / arcana lines | `game.item_instances.rolled_stats` JSON |
| **Manastones** / imprint extras | `rolled_stats` or `upgrades.magicstones` |
| **Enchant** / **Amplify ◆** | Existing `EnchantStat` / `ExceedStat` |
| **Set tiers** | `SetStat` (`item-sets.json`) |
| **Titles**, **boards**, **rides**, **buffs**, **Daevanion** | Unchanged |

Aggregation entry points:

- **`AggregatedStat(name, wire)`** — full combat total (used in `CombatStat`, 0x364a extras, attack speed).
- **`PlayerWeaponStats` / `PlayerSkillHits`** — damage uses weapon template plus `AggregatedStat` lines (enchant, titles, sets, Daevanion, buffs) and primary attributes via `CombatAttributes` (CANDIDATE coefficients).
- **`RefreshStats()`** — sends **0x364a** with updated wires after equip, enchant, etc.

## Wire map

`tools/data/item-stats.json` (built by `python tools/item_stat_data.py`) lists every **EStat** name the server applies, its wire id, and which stats are **basis points** (1% = 100).

Legacy **DefaultStats** rows (weapon damage 31/33, crit 128, accuracy 318, HP 193, defense 307, …) are still computed explicitly; any other non-zero combat stat is merged from **`ExtraSheetStats()`**.

## Instance JSON (`rolled_stats`)

Supported shapes (all optional):

```json
{
  "sub_stats": [["CombatSpeed", 660], ["Critical", 100]],
  "add_sub_stats": [],
  "soul_add_sub_stats": [],
  "arcana_main_stats": [],
  "magicstones": [{"stat": "Critical", "value": 50}]
}
```

Pairs may use **EStat name** or **wire id** as the key. Aliases (`PveAttack` → `FixingDamage`, `WeaponFixingDamage` → `FixingDamage`) match set bonus naming.

## Coverage audit (before this change)

| Stat family | Previously on sheet / combat | Now includes gear template + instance |
|-------------|------------------------------|----------------------------------------|
| Weapon min/max, accuracy, crit | Weapon slot MainStats only | All slots + instance rolls |
| Armor defense, HP | Sum MainStats | + instance rolls |
| **CombatSpeed**, **CoolTimeDecrease** | Buffs/titles/enchant/sets/Daevanion only | + all equipped template & rolls |
| Amplify / PvP / pierce / boss lines | Mostly ignored | Aggregated + damage formula |

## Tests

- `GearStatAggregationTests` — substat → 0x364a wire 282, attack speed, swing lockout, amplify damage.

Regenerate data after usmap changes:

```bash
python tools/item_stat_data.py
python tools/item_data.py   # when research/local catalog is present
```
