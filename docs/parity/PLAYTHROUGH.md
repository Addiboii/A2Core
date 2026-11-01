# E2E playthrough (packet harness)

Scenarios live in `tests/Aion.World.Tests` (`CampaignPlaythroughHelpers`, `StarterPlaythroughHelpers`, `PlaythroughCampaignTests`, `PlaythroughSideQuestTests`, `CampaignPlaythroughScenarioTests`, `CampaignPlaythroughParityTests`).

**Client pin:** Steam build **25767555** (Oct 7 2026). **Level cap:** `Aion:Progression:LevelCap` = **45**.

## Wave 9 — Asmodian starter (levels 1–10)

**Test:** `PlaythroughAsmodianTests.Asmodian_starter_zone_levels_1_through_10_packet_playthrough`

**Harness (strict):** Fresh Dark character on **SharedWorld** (`EnterFreshSharedWorldAsync`), opening quest `HQ2100010` already **Rewarded**, hero + side quests driven with **`allowDevShortcuts: false`** (no dev **`QuestComplete`** / **`QuestCompleteStep`**). Test asserts **`PacketFallbacks` is empty**.

| Track | Scope | Pin |
|-------|--------|-----|
| **Hero** | `hero-chains.json` Dark through `recommended_level` ≤ 10 | 13 quests |
| **Side** | `EQ2820001`–`EQ2820015` (`tools/data/starter-playthrough.json`) | 15 quests |

**Side accept:** `TryAcceptQuestForStarterAsync` on map **1110** (SubZone + CloseEnvObj from JSON), then Auto-acquire (`0x8d11`).

### Server fixes (starter + campaign)

| Class | Fix |
|-------|-----|
| **Kill gating** | `PlayerMayDamageNpc`: **open current-step kill ids win** over `PendingKillNpcIds` (`GameSession.Quests.Gating.cs`) |
| **Neutral kills** | `CombatMayHitNeutralForOpenKillObjective` for watchstones / instance bosses on open steps |
| **Instance boss** | `RespawnOpenInstanceLayerBosses` when a **ClearMapEvent** step is still open |
| **Field instance key** | `LoadCompleteAsync` forces **`instanceKey = 0`** on non-instance maps |
| **NPC streaming** | On large maps, **open talk / kill / ClearMapEvent entry** NPCs (e.g. Robin **2951078** on **1100**) stay streamed beyond view range |
| **Starter weapon** | Equipped begin weapon on **create** (`PostgresGameplayStore` / `FakeCharacterStore`); dev-only phantom stats when **`Store is null`** |
| **Layer walk-in** | Indexed **`WalkInLayer`**, quest-gated **`MeetsEntry`**, **`TrainingDummy`** disables walk-in in parity maps |

## Wave 9 — Main story levels 10–45 (both factions)

**Test:** `PlaythroughCampaignTests.Main_story_campaign_levels_10_to_45_both_factions`

Walks the hero chain on SharedWorld and logs every quest whose dat **`level`** is **10–45**. **Dev `QuestComplete` fallback** should stay **0** for the segment.

| Faction | Segment quests | Final level | Last segment quest | Post-segment gate |
|---------|----------------|-------------|--------------------|-------------------|
| **Light** | **78** | **45** | `HQ1102995` | `HQ1103010` (Unlock **46**) |
| **Dark** | **77** | **45** | `HQ2102810` | `HQ2103010` (Unlock **46**) |

Harness **ClearMapEvent** on instance layers: **`EnterDungeonById`** / **`EnterInstanceLayerAsync`**, then `PlayMapEventScriptAsync` (once per event name per quest).

## Wave 9 — side, region, ascension (levels 10–45)

At cap **45**, **131** side quests (`District` / `DutyMission`) per faction are completable after the hero chain to `HQ1103010` / `HQ2103010`. **28** side quests per faction are **`unavailable`** in `tools/data/quests.json` (same flag as UnlockLevel > 45 rows): they need **hero** `ClearQuestStep` or **prereq** progress on the post-gate hero chain (UnlockLevel **46+**). They are **not** faked in the harness.

- **`unavailable_reason`:** `cap_45: requires hero quest progress above live level gate (UnlockLevel 46+ / post-gate hero chain)`
- **Full code list (56 rows, both factions):** `progression.side_cap_unavailable_codes` in `tools/data/quests.json` (recomputed by `python tools/quests.py mark-cap`).
- **Progression totals at cap 45:** 169 unavailable (UnlockLevel > 45) + 56 side hero-gate = **225** unavailable rows.

### Light (28)

`DQ1203010`, `DQ1203020`, `DQ1203030`, `DQ1203040`, `DQ1203050`, `DQ1203060`, `DQ1203070`, `DQ1203080`, `DQ1203090`, `DQ1203100`, `DQ1203110`, `DQ1203120`, `DQ1203130`, `DQ1203140`, `DQ1203150`, `DQ1203160`, `DQ1203170`, `DQ1203180`, `DQ1203190`, `DQ1203200`, `DQ1203210`, `DQ1203220`, `DQ1203230`, `DQ1203240`, `DQ1203250`, `DQ1203260`, `DQ1203270`, `DQ1203280`

### Dark (28)

`DQ2203010`, `DQ2203020`, `DQ2203030`, `DQ2203040`, `DQ2203050`, `DQ2203060`, `DQ2203070`, `DQ2203080`, `DQ2203090`, `DQ2203100`, `DQ2203110`, `DQ2203120`, `DQ2203130`, `DQ2203140`, `DQ2203150`, `DQ2203160`, `DQ2203170`, `DQ2203180`, `DQ2203190`, `DQ2203210`, `DQ2203220`, `DQ2203230`, `DQ2203240`, `DQ2203250`, `DQ2203260`, `DQ2203270`, `DQ2203280`, `DQ2203290`

### Parity counts (per faction)

| Metric | Value |
|--------|--------|
| Side completable (packet sweep) | **131** |
| Side blocked in sweep | **0** |
| Side unavailable (hero gate @ cap 45) | **28** |
| Region completable | **204** |
| Ascension completable | **5** |

### Tests

| Test | CI default |
|------|------------|
| `PlaythroughSideQuestTests.Side_region_ascension_representative_packet_playthrough` | **Yes** — 8 side + 3 region + 5 ascension, full packets |
| `PlaythroughSideQuestTests.Side_region_ascension_full_objectives_and_reward` | **No** — trait `Category=LongPlaythrough` (~65 min); run with `dotnet test --filter Category=LongPlaythrough` |
| `CampaignPlaythroughParityTests` | Asserts side **131** completed, **0** blocked, **28** unavailable in data |

### HOME gap (unchanged)

**Daily / weekly (`DutyScroll`)** — live accept needs **`Quest.dat` `AcquireItemList`** and scroll item use; parity uses harness **`QuestAdd`**. See `docs/HOME_TODO.ru.md` P5.

### Commands

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test tests/Aion.World.Tests/Aion.World.Tests.csproj --filter PlaythroughAsmodian
dotnet test tests/Aion.World.Tests/Aion.World.Tests.csproj --filter PlaythroughCampaign
dotnet test tests/Aion.World.Tests/Aion.World.Tests.csproj --filter CampaignPlaythrough
dotnet test tests/Aion.World.Tests/Aion.World.Tests.csproj --filter PlaythroughSideQuest
dotnet test tests/Aion.World.Tests/Aion.World.Tests.csproj --filter "Category!=LongPlaythrough"
dotnet test tests/Aion.World.Tests/Aion.World.Tests.csproj --filter "Category=LongPlaythrough"
python tools/quests.py mark-cap --cap 45
dotnet run --project tests/Aion.Native.Checks
```

See also: [PLAYTHROUGH.ru.md](./PLAYTHROUGH.ru.md) (full hero chain + cap parity).
