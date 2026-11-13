# Monster / boss / NPC behaviour audit — 2026-11-13 (READ-ONLY)

Auditor: au-npcs agent. Nothing in code, data, database or services was changed.
Sources: client tables `C:\A2Core\research\local\tables-20261109-023531` on the client PC (NpcData 13,493 rows, NpcAggroData,
NpcBehaviorCase, Skill / SkillEffect / SkillAbnormal*, Map_*_MapData SpawnInfoList 73,551 records, PeriodSpawn, WorldMapFieldNamed,
BossChallenge*, RewardData / RewardItem / LevelScaleDropGroup, Dungeon, Map, AbyssRift*, PeriodObjectVisibility); server
`G:\A2Core-src\editing` HEAD 601e9e4 (upgrade/stage1-step10): NpcAi.cs, NpcTactics.cs, Combat.cs, BossAi.cs, BossControl.cs,
GameSession.BossClient.cs, GameSession.BossMechanics.cs, FieldBosses.cs, GameSession.FieldBosses.cs, GameSession.Loot.cs,
LootTables.cs, DropDB.cs, GameSession.cs (RespawnTargets, AI tick), GameSession.MapEvents.cs / MapEvents.cs,
gamedata/provisional/{boss-ai,boss-client,boss-mechanics,npc-hp}.xml, gamedata/server-rules.xml, tools/world_npcs.py +
tools/data/world-npcs.json, client-combat.json, npc-teleports.json, loot.json, map-events.json; 20 official recordings
`capture-13700-2026110[67]-*` (5,603 NpcAppear decoded, casts 0x3809/0x3802 per NPC, deaths 0x8d04, respawns by spawn unique key).
Builds on monster-audit-2026-11-11.md, content-audit-2026-11-09.md, quest-objects-audit-2026-11-12.md (their fixes are taken as done).

Legend: **S** Supported, **P** Partial (works, but numbers/wire CANDIDATE or only some cases), **M** Missing.

## 0. Summary

- **Bosses: 948** = 648 placed HeroMonster/LegendMonster types on the 739 served maps + 300 Boss Challenge "NightMare" bosses.
  Every mechanic the client data shows works for **69** (7 %); **344** have only partial mechanics (telegraph timings, groggy,
  berserk are CANDIDATE); **535** (56 %) have at least one mechanic that does not exist on our server.
- Only **7 boss fights are in the official recordings** (2400017, 2400140, 2500016, 2701034, 2701090, 2701128, 2980044): every
  phase/enrage/groggy number for the other 941 is a guess or the client's static data.
- Biggest holes: invisible helper adds (the bosses' real AoE mechanics) for 421 bosses; 578 monster types with client loot tables
  that drop nothing (loot.json built from the old 2026-10-16 export); field bosses / elites respawning in 30 s like trash; boss
  loot for the last hitter only; boss self-buffs, dispels, chained skills and threat modifiers ignored.
- Normal monsters are in good shape after 11-11/11-12 (aggro, area, DoT, stationary, traps). Official respawn of normal monsters
  measured here: median 29.2 s over 407 respawns (clusters at 5.2 / 29.2 / 54.2 s) = ours 30 s.

## 1. NpcData fields that affect behaviour

Counts are NpcData rows (13,493) unless "spawns" (placements in world-npcs.json, 82,547).

| field | values (rows) | server use | status | where / evidence |
|---|---|---|---|---|
| NpcType / NpcSubType | Monster 8,277: Normal 5,499, Elite 1,421, Hero 1,356, Legend 1; Citizen 3,753; Summon 1,212; Bot 180 | rank (NpcTactics.RankOf), HP/damage units, CcImmune for Hero | S | Combat.cs, NpcTactics.cs, world_npcs.py `sub_type` |
| Level / Grade | 1-100 / 0-40 | HP curve + npc-hp.xml rows, damage scale | P | HP is not in the client (stat table encrypted); 5 observed HP rows in npc-hp.xml, the rest a curve (CANDIDATE) |
| RelationshipEntity | 25 values | hostility, relationship changes by map events | S | Combat.Hostile; Monster_Invisible (243 rows) not in world_npcs.py RELATIONSHIP (falls back to 6) |
| AggroType | Aggro_A 6,542, AggroNormal 6,324, Revenge_A 284, ParentTarget 221, Devotion_A 83, Magnetic_A 19, SearchOnlyPC 9, Guard 1 | Aggro_A / SearchOnlyPC / Revenge_A aggressive; ParentTarget for summons | P | world_npcs.py AGGRESSIVE_TYPES. **Devotion_A (329 spawns) and Magnetic_A (100 spawns) behave as passive** (no recording) |
| AggroDetectRange -> NpcAggroData | 43 shapes / 56 rows | detection arc/circle/height | S | NpcAi.Detects |
| ActiveSkillList | 4,270 distinct monster skills | AI skill use | S/P | section 2: Damage, Abnormal, Teleport, visible Spawn S; heal, dispel, CasterSkill, AggroTransfer, self-buffs M |
| BaseWalkSpeed / BaseMoveSpeed | | walk / run | S | |
| BaseFly* / BaseSwim*, ObjectTerrainType Air 421 / Water 96 / All 204 | | air monsters keep their height | P | fly/swim speeds unused; water monsters (541 spawns) move like ground ones |
| bNoRotation / RotateAngleMax | 431 / 2,229 rows | NoRotation S; RotateAngleMax kept in the json, **unused in C#** | P | GameData.cs |
| CollisionCylinderRadius / Height | | reach, body spacing | S | |
| bCanBeAttacked false (3,774 rows, **5,903 monster spawns**) / NpcTargetAble Unable (1,425 attackable monster spawns) | | only traps.xml NPCs are untouchable | **M** | GameSession.cs:3086 `Traps.Untouchable`; every other one can be hit (area skills, auto target) |
| bHitCountBased | 325 rows, **242 monster types / 5,357 spawns** | HitGauge only from spawn-poses.xml (2 NPCs) | **M** | they die by damage, not by number of hits |
| NpcGroggyType / GroggyTime / GroggyAttackMaxCount | GroggyGuard 1,038, GroggyAttack 1; 5000 ms 1,091 | guard energy + groggy window | P | GameSession.BossClient.cs GroggyHit; energy 100 CANDIDATE; 0x3b04 off (groggy_wire=false); GroggyAttack / MaxCount unused |
| bUseBossBerserk / BossBerserkInvokeTime | 384 (300 s: 377) | timer in 0x8d21, damage x2 after it | P | NpcAi.cs:640, 1117; the x2 is CANDIDATE (no official enraged boss) |
| bNamed | 2,303 | - | M (minor) | named = no different respawn or loot rule |
| LevelScaleType / WorldLevelScaleType | Exceed 1,797 / All 409; World Exceed 3,465 (**19,508 monster spawns**) | - | **M?** | monster level scaling to the player not modelled; needs a recording of a high-level player next to a scaled monster |
| DropRadiusMin / Max | 100-150 (9,438), 50-300 (3,826) | - | M (P3) | GameSession.Loot.cs uses a fixed 60-150 cm |
| SpawnActName / DespawnActName / DeadDespawnActName / DieActName / bSkipDeadDespawn | 1,112 / 436 / 29 / 215 / 1,045 | SpawnAct for summons and map-event spawns | P | respawns send no spawn act; die/despawn acts and SkipDeadDespawn (2,519 spawns) unused |
| bPeriodObj (+ PeriodObjectVisibility 1,612 NPC rows by quest state) | 1,389 | - | ? | probably client-side filtering (EResult NpcTouch_PeriodObj exists); verify with a recording |
| bAutoBattleTargetException | 1,215 | - | n/a | client auto-battle |
| Tribe / bTribalistic | 264 / 4 | - | M (P3) | tribe infighting not modelled (6 spawns) |
| FunctionType Companion (141) | | companion follows and fights (CompanionPrey) | S | NpcAi.cs:283 |
| bBlockedOthersForceMove | 40 | - | M (P3) | knockbacks on these NPCs are not blocked |
| NpcNameplate / bMouseOverAble / bHide* / Portrait / Scale / FxScale / offsets | | - | n/a | client display |
| PartsTableRefList / MonsterPartsData | all empty / 4 test rows | - | n/a | no live data |

## 2. Boss mechanic types (client data vs server)

Counts over the 948 bosses (bosses that have the mechanic at least once).

| mechanic | client source | bosses | server | status | code / evidence |
|---|---|---|---|---|---|
| Skill use (rotation, cooldown, cast, range) | ActiveSkillList + Skill | 948 | NpcAi skill choice + NpcTactics scoring | S | priority order only for 2701034 (boss-ai.xml); others OURS scoring |
| Skills our AI never casts (no damage, cast 0, not a leap/debuff) | | **172** | NpcAi.SkillData returns null | **M** | e.g. 2701892 DarkLeda 1222080 (Spawn, cd 35 s, cast 0); 2700003 Perion 1600360/1600390 (NoEffect markers); 2300190 ToxFungus 1607950 (self abnormal) |
| Telegraphed AoE (0x3848) | WideCastingAlertTime / GroundRadius / SkillEffectFilterLocation (client-combat.json npc_skills 1,287) | 423 | NpcAi.Telegraph | P | layout OFFICIAL (83 samples), warn time / spot radius CANDIDATE |
| Leap / charge | SkillEffect Teleport_Caster (npc-teleports.json, 214 skills) | 515 | NpcAi.Leap | S | |
| Pre-cast / pre-start teleport | Pre_Cast_Teleport_Caster, Pre_SkillStart_Teleport_Caster | 92 | treated as a normal leap at the skill start | P | npc_teleports.py matches `endswith('Teleport_Caster')` |
| Adds (fighting monsters) | SkillEffect Spawn -> visible monster | 44 | GameSession.BossClient.cs BossAddsTick (solo and shared maps) | P | placement radius 400 / max 8 CANDIDATE |
| **Helper adds (invisible casters that do the AoE)** | SkillEffect Spawn -> InvisibleNpc summon with its own skill | **421** | only 2701034 Lake Guardian (boss-mechanics.xml), **solo copies only**; BossAddsTick skips every non-fighting add | **M** | GameSession.cs:4531 `aiMap.Ai.TakeCasts(); // boss helpers: solo copies only`; official sample: 2980044 + helper 2920761 casting 1221085 x15 (rec 20261106-115817) |
| HP phases | none in the client (server AI); map events HpRemaining (858 uses) | 251 by map events; 1 by boss-ai.xml | MapEvents HpRemaining S; NpcTactics 75/50/25 % forced mechanic + enrage below 30 % (OURS) | P | field / world bosses have no phase data at all |
| Enrage / berserk timer | bUseBossBerserk | 346 | sent, x2 damage | P | section 1 |
| Groggy / stagger | NpcGroggyType GroggyGuard + player GroggyGuardDecreaseEnergy (3,316 skills) | 818 | GroggyHit, no 0x3b04 | P | no 0x3b04 in any recording although 4 guard bosses were fought |
| Stun DR / stagger gauge | none (OURS, user 2026-11-08) | Hero | BossControl.cs | P (mostly dead code) | Hero carries CcImmune 10000001: AddNpcBuff blocks the stun before BossControl runs |
| Immunity windows (scripted) | map events AddAbnormal on a spawner (985 uses) | 59 | AddNpcBuff (held / immune / relationship) | P | only abnormals modelled as held / immune / relationship act |
| Boss self-buffs | Positive Self skills: StatChange 85, BlockAniHit 8, TriggerSkillByGetDamage 8, CastSkill 8, HpBarrier 6, DamageReflect 5 ... | 64 | cast animation only | **M** | 11-11 audit: "NPC self-buff skills not applied" |
| Self-heal | HpHealRatio / HpHeal | 12 | - | **M** | |
| Dispel of the player's buffs | Dispel_True (MainTarget 190 rows, Self area 95) | 77 | - | **M** | |
| Chained skill | CasterSkill | 20 | - | **M** | |
| Aggro transfer | AggroTransfer | 3 | - | **M** | |
| Threat / aggro table | SkillEffect AggroRatio / AggroAbsolute on 3,953 effects (10000 = x1; tank skills 15000-50000; heal threat on 56 HpHeal rows), Taunt abnormal (7) | all | Hate = damage (OURS); random target switch for Hero (OURS) | **M** | NpcAi.cs:572, 1785 |
| Leash / reset | none in the client | all | 10-15 m chase, 10 s no hit; bosses 30 s no hit, 15 s lost grace (server-rules.xml, user rules) | P | not official numbers; a reset boss loses its adds / berserk (CANDIDATE) |
| Call for help / flee / kite / random target | none | | NpcTactics (OURS, user 2026-11-07 "smarter than official") | ? | conflicts with the later "official copy only" rule (memory official-copy.md): decision needed |
| Map-event boss control | ChangeBT 34, RemoveFromAggroList 93, NpcChangePhase 17 | | ChangeBT / NpcChangePhase ignored (MapEvents.cs:914); RemoveFromAggroList not implemented | M | content_audit |
| Barks | NpcBehaviorCase 2,519 rows, 1,071 NPCs (CombatStart 949, Idle 523, Casting 484, Die 188) | | - | ? | not in the server; probably client-side - verify |
| GimmickGauge / MonsterParts / ElementalSummon | 3 / 4 test / 5 groups (players') | | - | n/a | no live boss uses them |

## 3. Bosses

### 3.1 Coverage by category

Numbers of bosses having each mechanic: TG telegraph, LP leap, PT pre-teleport, AF fighting adds, AH helper adds,
GG groggy guard, BK berserk, HP map-event HP trigger, NA map-event abnormal on the boss, SB self-buff, HL self-heal,
DP dispel, CS chained skill, AT aggro transfer, NC skill never cast by our AI.
"all-supported" = only S mechanics; "only-partial" = no M but some P; "has-missing" = at least one M.
Category: FieldBoss(WorldMap) = the 85 WorldMapFieldNamed bosses (12 of them PeriodSpawn), FieldNamed = other Hero on field maps,
Dungeon:<DungeonType>[/SubType] from Dungeon.dat, InstanceLayer = story layers, BossChallenge = the 300 NightMare templates.

| category | bosses | all-supported | only-partial | has-missing | TG | LP | PT | AF | AH | GG | BK | HP | NA | SB | HL | DP | CS | AT | NC | recorded |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BossChallenge | 300 | 0 | 100 | 200 | 150 | 230 | 0 | 30 | 150 | 300 | 300 | 0 | 0 | 20 | 10 | 20 | 0 | 0 | 90 | 1 |
| Dungeon:Party | 196 | 23 | 18 | 155 | 112 | 80 | 40 | 3 | 127 | 173 | 42 | 12 | 3 | 15 | 0 | 44 | 13 | 0 | 43 | 0 |
| InstanceLayer | 108 | 2 | 68 | 38 | 10 | 53 | 1 | 1 | 31 | 101 | 0 | 93 | 2 | 14 | 0 | 0 | 0 | 0 | 3 | 3 |
| FieldBoss(WorldMap) | 85 | 38 | 16 | 31 | 28 | 36 | 1 | 0 | 14 | 0 | 0 | 0 | 0 | 8 | 0 | 2 | 6 | 2 | 7 | 2 |
| Dungeon:Matching/Suppression | 72 | 0 | 42 | 30 | 42 | 18 | 42 | 0 | 30 | 72 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Dungeon:Seal | 60 | 0 | 45 | 15 | 9 | 21 | 0 | 0 | 10 | 60 | 0 | 59 | 1 | 4 | 1 | 3 | 0 | 1 | 3 | 0 |
| Dungeon:Quest | 51 | 2 | 20 | 29 | 19 | 35 | 0 | 2 | 26 | 49 | 0 | 45 | 3 | 2 | 1 | 2 | 0 | 0 | 11 | 1 |
| Dungeon:Awaken | 44 | 0 | 26 | 18 | 37 | 32 | 0 | 7 | 18 | 44 | 0 | 32 | 44 | 0 | 0 | 0 | 0 | 0 | 6 | 0 |
| Dungeon:Raid | 17 | 4 | 1 | 12 | 6 | 4 | 7 | 0 | 9 | 10 | 4 | 1 | 5 | 0 | 0 | 5 | 1 | 0 | 7 | 0 |
| Dungeon:Ascension | 9 | 0 | 2 | 7 | 4 | 6 | 1 | 1 | 6 | 9 | 0 | 9 | 1 | 1 | 0 | 1 | 0 | 0 | 2 | 0 |
| FieldNamed | 6 | 0 | 6 | 0 | 6 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| **total** | 948 | 69 | 344 | 535 | 423 | 515 | 92 | 44 | 421 | 818 | 346 | 251 | 59 | 64 | 12 | 77 | 20 | 3 | 172 | 7 |

### 3.2 Recorded boss fights (the only official evidence)

| boss | recording | what the official did | ours |
|---|---|---|---|
| 2701034 Lake Guardian (Dungeon_L_0016/0018 cave) | 20261107-012837 | 1603870 at 2.3 s, leap 1603852 to the arena centre at ~34 %, roar 1603860 + 4 helpers 2920328 firing 1603861 projectiles | S (boss-ai.xml + boss-mechanics.xml), solo only |
| 2980044 NightMare_PinoBoss_01_005 (Boss Challenge) | 20261106-115817 | 0x8d21 berserk time (+365 d +300 s); 8 skills; helper 2920761 casting 1221085 five-spot telegraph x15 | berserk S; telegraph S on the boss's own skills; **helper 2920761 missing** |
| 2701128 DrakanElite (L_Starter layer 2) | 20261107-014953 | 8 skills incl. 1223815 x4, 1223800 | P (PT, GG) |
| 2500016 CorpeSpider (FQ D1) | rec 2026-11-06 | 1238580 cast, 1238615 x9, CC immunity 10000001 | GG P |
| 2400017 DranaVar, 2400140 DracoWar (field Hero, 1110) | 2026-11-06/07 | casts only, both grade 4, HP 80,000,000 | S |
| 2701090 Bargot (L_Starter layer 1) | 2026-11-07 | 3 skills | S |

### 3.3 Field bosses (85, WorldMapFieldNamed) - what works

- Spawn / schedule: the 12 PeriodSpawn bosses (Abyss 20 / 22) follow PeriodSpawn Day/Cycle times (KST CANDIDATE), kills survive a
  restart (game.field_boss_kills) - S. The **73 Basic field bosses respawn 30 s after death** (GameSession.FieldBosses.cs:101
  `now + RespawnMs`) - M (no client value; official timer only visible in the world map 0x8dd2 reply, never recorded).
- World map tab 0x8dd2: answered, layout CANDIDATE (usmap) - P.
- Mechanics: 38 fully supported, 16 partial, 31 with a missing mechanic: AH helper adds on 14 (all the Abyss AbBoss/EleKing/
  Radvima types: e.g. 2600096 Radvima 9 helper spawns, 2600084 AbBoss 5), SB self-buff 8, CS chained skill 6, DP 2, AT 2, NC 7.
  No field boss has groggy or berserk in the client data. **On shared maps no helper mechanics run at all.**
- Loot: last hitter only (see 4.2).

### 3.4 Dungeon / instance bosses

Phases and immunity windows of dungeon bosses mostly come from the map events (HpRemaining 251 bosses, AddAbnormal 59, Spawner
swaps): S through MapEvents. What the map events do not cover is the boss's own kit: helper adds (Party 127 of 196, Quest 26,
InstanceLayer 31, Suppression 30, Raid 9 of 17), self-buffs, dispels, chained skills. Raid bosses (17): 12 with a missing mechanic.
Boss Challenge (300 NightMare templates, 26 groups x 10 levels): all have groggy + berserk (P), 150 have helper adds (M), 90 a
never-cast skill.

The full per-boss list is in section 7.

## 4. Spawns, respawn, drops

### 4.1 Spawns

| topic | client data | server | status | evidence |
|---|---|---|---|---|
| Placements | MapData SpawnInfoList 73,551 records (NpcIdList, Positions, MaintainingTag, UniqueKey); no respawn time, no count, no time-of-day field | world-npcs.json 82,547 spawns on 739 maps; one NPC per position, ids cycled | S | tools/world_npcs.py build |
| Multi-id records | NpcIdList > 1 on 4,200+ records | ids cycled over the positions (CANDIDATE: "official pick looked random") | P | e.g. Draconic_00x *_Multi_Boss: 4 bosses (Nahid/Radvima/BeritraD/BeritraB) at 4 corners -> we spawn all 4 at once; official may pick one. Needs a recording |
| Respawn, normal monsters | none in client | 30 s flat (GameSession.RespawnMs), corpse 4 s | S | official: 407 respawns, median 29.2 s (clusters 5.2 / 29.2 / 54.2 s: 5 s + 24-25 s steps?) |
| Respawn, elites / named / Basic field bosses | none in client | **30 s like trash** | **M** | no official respawn of an elite or Hero in any recording |
| Respawn in dungeons / layers | | none for the run (RunsMapEvents) | S | |
| Timed spawns | PeriodSpawn 22 rows: 12 field bosses (S), **10 rift portals** (EnvObj_D1_RiftPortal_1..3, Verteron/Eltnen/D2 rift portals, every 3 h from 02:00) | rift portals unused | M | FieldBosses only reads WorldMapFieldNamed-linked rows |
| Day/night spawns | none found in client tables (MapEnvState is weather/TOD for visuals) | - | n/a | |
| Rare spawns | no rare-spawn table; only multi-id records | - | n/a | |
| Spawn groups / waves | WaveStepData + map events StartWave/ClearWave 115 waves; AbyssRiftPhase 8 phases / 64 spawner groups; RiftData 16 | waves S (content audit 11-10); **Abyss Rift phases not implemented** | P | |
| Patrols | WaypointsDataMap: 5,339 NPC routes; linked to a spawn only by name OnWay/Roam + nearest start (CANDIDATE) | 269 of 400 OnWay/Roam spawns get a route; 4,005 routes kept for map events | P | 131 OnWay/Roam spawns stand still |
| Quest-phased NPCs | PeriodObjectVisibility 1,612 NPC rows | - | ? | probably client-side |

### 4.2 Drops

| topic | client data | server | status | evidence |
|---|---|---|---|---|
| Per-NPC reward tables | RewardData 2,301 rows (RewardName, BasicRewardName, LevelscaleBasicRewardName, GroupLevelscaleRewardName, BasicKeyRewards, *NoLvgap) | loot.json 1,496 NPCs, built from the **2026-10-16** export (research/local/pet-tables-20261016) | **P** | **578 spawned monster types have RewardData + existing reward tables in the 11-09 export but no loot.json entry**: 368 Elite, 139 Normal, 71 Hero (e.g. 2330709 Arkanis Thalythra reward_PartyDungeonBoss_H2_2330709 + reward_PD_Direct_Arkanis_G_01_Boss; all M_PD_Arkanis_* elites) - they drop nothing but Kina |
| Level-scaled basic drops | GroupLevelscaleRewardName (1,058 rows) -> LevelScaleDropGroup (55,000 rows) by the player's level | only the fixed-level BasicRewardName suffix is rolled | P | tools/loot_data.py ignores GroupLevelscaleRewardName / LevelScaleDropGroup / RewardLevelScaleData |
| Key rewards / NoLvgap | BasicKeyRewards 17, *NoLvgap 8 | ignored | M (P3) | |
| Roll reading | ItemProb of 1e8, independent per entry | independent rolls | P | CANDIDATE (tables often sum above 1e8) |
| Boss extra drops | aion2hub.com rates (UNOFFICIAL) | DropDB for a dungeon's final cube boss | P | gamedata/drops/aion2hub.xml |
| Party / participant rewards | RewardItem tables named *_participant_all (party dungeon bosses) | loot only for the killer's session | **M** | GameSession.Loot.cs DropLoot: "the drops for the killer"; on shared maps field-boss helpers of the fight get nothing |
| Drop scatter | DropRadiusMin/Max | 60-150 cm fixed | M (P3) | |
| Kill Kina | not per NPC (official: stack update reason 11) | by level and rank (USER rule) | P | |
| NpcLoot (1,476 rows: LootList + VehicleSoulItemId) | client "possible drops" list | not used | n/a | UI preview only |

## 5. NPC behaviour (non-boss)

| behaviour | client data | server | status | evidence |
|---|---|---|---|---|
| Aggro / provoked start / Revenge_A | NpcAggroData, AggroType | official message order (11-11 audit) | S | |
| Devotion_A (329 spawns) / Magnetic_A (100) | AggroType | passive | M | names suggest protect-the-parent / pull; no recording |
| ParentTarget (221 rows, summons/adds) | AggroType | summons only | P | boss adds go for the boss's victim (OURS) |
| Guards: Citizen NPC_Light/Dark with Aggro_A + skills | 1,283 field + 220 Abyss + 177 instance spawns | never fight on their own (only SetAggroNpc/Pc scripts) | M | Abyss guards should attack the enemy faction (needs a recording) |
| Companions | FunctionType Companion 141, ChangeBT Def_Companion_NPC_01 / NoCombat | follow + fight nearest monster within 10 m (OURS range) | P | ChangeBT ignored: a "NoCombat" companion still fights |
| Patrols | WaypointsDataMap | 269 routed spawns; Circular / TwoWay / OneWay; official 0x371c every 1 s | S/P | 131 OnWay/Roam unrouted |
| Idle wander | none | short walks near the player (OURS) | P | official field monsters: check a recording for idle walks before adding more |
| Social / idle animations | none (npc-social-actions.json = ids seen in recordings) | sent in the appear | P | only recorded spawns have one |
| Barks (NpcBehaviorCase) | 2,519 rows | - | ? | likely client-side |
| Call for help / flee / kite | none | OURS (NpcTactics) | ? | decide against "official copy only" |
| Hit-count monsters | bHitCountBased 242 types | damage-based | M | |
| Untouchable monsters | bCanBeAttacked / NpcTargetAble | traps.xml only | M | |
| Water monsters | ObjectTerrainType Water 541 spawns, swim speeds | ground movement | M (P3) | |
| Monster level scaling | LevelScaleType 19,508 spawns | none | M? | |

## 6. Prioritised GAP LIST

P1 breaks play, P2 wrong vs official, P3 cosmetic / small. Size S < 1 day, M 1-3 days, L > 3 days.

| # | gap | which NPCs / bosses | how to implement (data / recordings) | size | prio |
|---|---|---|---|---|---|
| 1 | **Boss helper adds (invisible casters doing the AoE) never appear**; Lake Guardian's helpers run in solo copies only | 421 bosses (Party 127, BossChallenge 150, InstanceLayer 31, Suppression 30, Quest 26, Awaken 18, FieldBoss 14, Seal 10, Raid 9, Ascension 6) | Generalise BossMechanicTable from client data: SkillEffect Spawn row (values [0] distance, [9] NPC, [10] count) -> the helper's NpcData ActiveSkillList skill (Skill CastingTime, projectile ProjectileId, SkillEffectFilter area, client-combat.json filter/alert). Place, cast (0x3809/0x3802/0x3803), hit, despawn (0x3642 flag 3) as boss-mechanics.xml does; run it from the shared-map AI tick too (GameSession.cs:4531). Official samples: 2701034+2920328 (rec 012837), 2980044+2920761 1221085 x15 (rec 115817) | L | P1 |
| 2 | **578 monster types drop nothing although the client has their loot** | 368 Elite, 139 Normal, 71 Hero spawned types (all M_PD_* party dungeon mobs/bosses, e.g. 2330709 Thalythra) | Re-run tools/loot_data.py against tables-20261109-023531 (Table_RewardData / RewardItem / RewardPackage / RewardPackageItem; adapt `_rows` to the Table_*.json names); also read GroupLevelscaleRewardName -> LevelScaleDropGroup by player level | S | P1 |
| 3 | **Field bosses, named and elites respawn 30 s after death** | 73 Basic field bosses (WorldMapFieldNamed), 2,303 bNamed rows, Elite 718 spawned types | Respawn time per rank in server-rules.xml (field boss / named / elite); value from a recording of the world map Field Boss tab (0x8dd2 respawn time) or official patch notes; mark CANDIDATE until then | S | P1 |
| 4 | **Boss loot only for the last hitter** on shared maps; party rewards (*_participant_all) unused | all field bosses; party dungeon bosses | Hate table = contributors: roll the reward for every player who hit / healed within the fight (or the party), owner per drop; use *_participant_all tables for all party members. Needs one official field-boss kill with 2+ players recorded | M | P1 |
| 5 | Skills our AI never casts (instant no-damage skills: Spawn, NoEffect markers, self abnormals) | 172 bosses (39 Spawn skills, 63 NoEffect, self buffs) | NpcAi.SkillData: accept any skill with a cooldown that has a Spawn / Abnormal(Self) / Dispel / Heal / CasterSkill effect; send 0x3802 with no hit | S | P1 (blocks #1 for 33 bosses) |
| 6 | Boss self-buffs / shields / reflect not applied | 64 bosses (StatChange 85 rows, HpBarrier 6, DamageReflect 5, BlockAniHit 8, TriggerSkillByGetDamage 8) | Apply Positive Self abnormals through AddNpcBuff and make NPC stats read StatChange (DefenseRatio, AttackRatio, MoveSpeed), HpBarrier as an absorb pool, DamageReflect on player hits; wire 0x382a as for players | M | P2 |
| 7 | Threat model ignores client aggro numbers | every fight with 2+ players (3,953 SkillEffect rows with AggroRatio/AggroAbsolute; heal threat; Taunt 7; AggroTransfer 3 bosses) | Hate += damage x AggroRatio/10000 + AggroAbsolute; heals add threat to every monster fighting the healed player; Taunt forces the victim; AggroTransfer moves hate; drop the OURS random target switch | M | P2 |
| 8 | Dispel of player buffs | 77 bosses (Dispel_True: MainTarget 190 rows, Self-area 95) | On the hit: remove N Buff-category abnormals from the victim (0x382c); values list = count / category | S | P2 |
| 9 | Untouchable monsters can be hit | 5,903 bCanBeAttacked=false + 1,425 NpcTargetAble=Unable monster spawns | Carry bCanBeAttacked / NpcTargetAble into world-npcs.json templates; skip them in area hits, auto-target, companions (as Traps.Untouchable) | S | P2 |
| 10 | Hit-count monsters die by damage | 242 types / 5,357 spawns (bHitCountBased) | HitGauge for every bHitCountBased NPC; hit count from the official appear HP of such NPCs (recordings) | S | P2 |
| 11 | Groggy gauge: no client wire, energy guessed | 818 bosses (GroggyGuard) | Record a fight where the guard breaks (check 0x3b04 / abnormal); then turn groggy_wire on and set the energy | S (after recording) | P2 |
| 12 | Berserk effect guessed (x2 damage) | 346 bosses (300 s) | Record a boss past 5 min (Boss Challenge is easiest) to see what changes (buff 0x382a? skill?) | S (after recording) | P2 |
| 13 | Chained skills (CasterSkill) not cast | 20 bosses | After the parent skill's hit, start the linked skill (SkillEffect CasterSkill value = skill id) | S | P2 |
| 14 | Map-event RemoveFromAggroList / ChangeBT / NpcChangePhase ignored | 93 / 34 / 17 uses in boss rooms and companion scenes | RemoveFromAggroList: drop the players from the spawner NPCs' hate and reset; ChangeBT NoCombat: companion stops fighting; NpcChangePhase: needs a recording (0x? phase message) | S | P2 |
| 15 | Monster level scaling to the player | LevelScaleType Exceed/All on 19,508 monster spawns | Record a high-level character next to a low-level Exceed monster (0x3641 level/HP) and scale level/HP/damage as observed | M | P2 |
| 16 | Guards never fight | 1,283 field + 220 Abyss Aggro_A NPC_Light/Dark guards | Abyss: attack enemy-faction players in their aggro shape; field: verify by recording whether guards fight monsters | M | P2 |
| 17 | Devotion_A / Magnetic_A passive | 329 + 100 monster spawns | Needs a recording (name: defend the parent / group pull); then add to NpcAi | M | P2 |
| 18 | Invented behaviours (flee, kite, random target switch, enrage below 30 %, call for help, generic 75/50/25 % phases) | all monsters / bosses | Decide with the user against "official copy only"; put each behind a server-rules.xml switch, default off where no recording supports it | S | P2 |
| 19 | Abyss Rift phases / rift portals | AbyssRiftPhase 8, spawner groups 64, RiftData 16, 10 PeriodSpawn rift portals | Feature: PeriodSpawn rift portals every 3 h; phases end by Time / KillNpcCount (client table), spawn AbyssRiftPhaseSpawnerGroup | L | P2 |
| 20 | Pre-cast teleports done as normal leaps | 92 bosses | Move the caster at cast start (Pre_Cast) or skill start (Pre_SkillStart) before the hit; check against rec 014953 (2701128 PT skill) | S | P3 |
| 21 | Spawn records with several NPC ids spawn all of them | Draconic *_Multi_Boss (4 bosses at once), 4,200 records | Record a Draconic Crisis entry; if official picks one, pick one id per record (or per position) at random | S | P3 |
| 22 | Drop scatter, spawn/die acts, RotateAngleMax, water movement, tribes, ForceMove block | various | read the NpcData fields into templates | S each | P3 |
| 23 | 131 OnWay/Roam spawns without a route | field maps | widen ROUTE_LINK_RANGE or link by label name | S | P3 |
| 24 | BossControl stun DR / stagger is dead code for Hero (CcImmune blocks first) | Hero | remove or wire to the groggy guard to avoid two models | S | P3 |
| 25 | Barks (NpcBehaviorCase) | 1,071 NPCs | confirm client-side in a recording; nothing to do if so | S | P3 |

## 7. Per-boss list

Mechanics: count of skills with it; `~` partial, `!` missing, no mark = supported. GG groggy guard, BK berserk seconds,
HP = map-event HpRemaining triggers on its spawner, NA = map-event abnormals on it. rec = fight in an official recording.

### BossChallenge (300)

| id | name | lvl | maps | skills | mechanics (S ok, ~ partial, ! missing) | rec |
|---|---|---|---|---|---|---|
| 2980000 | NightMare_DarkLeda_01_001 | 45 | 200003 | 10 | TG1~ LP1 AH2! GG~ BK300s~ NC1! |  |
| 2980001 | NightMare_DarkLeda_01_002 | 45 | 200003 | 10 | TG1~ LP1 AH2! GG~ BK300s~ NC1! |  |
| 2980002 | NightMare_DarkLeda_01_003 | 45 | 200003 | 10 | TG1~ LP1 AH2! GG~ BK300s~ NC1! |  |
| 2980003 | NightMare_DarkLeda_01_004 | 45 | 200003 | 10 | TG1~ LP1 AH2! GG~ BK300s~ NC1! |  |
| 2980004 | NightMare_DarkLeda_01_005 | 45 | 200003 | 10 | TG1~ LP1 AH2! GG~ BK300s~ NC1! |  |
| 2980005 | NightMare_DarkLeda_01_006 | 45 | 200003 | 10 | TG1~ LP1 AH2! GG~ BK300s~ NC1! |  |
| 2980006 | NightMare_DarkLeda_01_007 | 45 | 200003 | 10 | TG1~ LP1 AH2! GG~ BK300s~ NC1! |  |
| 2980007 | NightMare_DarkLeda_01_008 | 45 | 200003 | 10 | TG1~ LP1 AH2! GG~ BK300s~ NC1! |  |
| 2980008 | NightMare_DarkLeda_01_009 | 45 | 200003 | 10 | TG1~ LP1 AH2! GG~ BK300s~ NC1! |  |
| 2980009 | NightMare_DarkLeda_01_010 | 45 | 200003 | 10 | TG1~ LP1 AH2! GG~ BK300s~ NC1! |  |
| 2980010 | NightMare_Perion_01_001 | 57 | 200003 | 12 | TG2~ LP1 GG~ BK300s~ HL1! NC2! |  |
| 2980011 | NightMare_Perion_01_002 | 57 | 200003 | 12 | TG2~ LP1 GG~ BK300s~ HL1! NC2! |  |
| 2980012 | NightMare_Perion_01_003 | 57 | 200003 | 12 | TG2~ LP1 GG~ BK300s~ HL1! NC2! |  |
| 2980013 | NightMare_Perion_01_004 | 57 | 200003 | 12 | TG2~ LP1 GG~ BK300s~ HL1! NC2! |  |
| 2980014 | NightMare_Perion_01_005 | 57 | 200003 | 12 | TG2~ LP1 GG~ BK300s~ HL1! NC2! |  |
| 2980015 | NightMare_Perion_01_006 | 57 | 200003 | 12 | TG2~ LP1 GG~ BK300s~ HL1! NC2! |  |
| 2980016 | NightMare_Perion_01_007 | 57 | 200003 | 12 | TG2~ LP1 GG~ BK300s~ HL1! NC2! |  |
| 2980017 | NightMare_Perion_01_008 | 57 | 200003 | 12 | TG2~ LP1 GG~ BK300s~ HL1! NC2! |  |
| 2980018 | NightMare_Perion_01_009 | 57 | 200003 | 12 | TG2~ LP1 GG~ BK300s~ HL1! NC2! |  |
| 2980019 | NightMare_Perion_01_010 | 57 | 200003 | 12 | TG2~ LP1 GG~ BK300s~ HL1! NC2! |  |
| 2980020 | NightMare_Gartua_01_001 | 63 | 200003 | 13 | TG2~ LP2 AH23! GG~ BK300s~ |  |
| 2980021 | NightMare_Gartua_01_002 | 63 | 200003 | 13 | TG2~ LP2 AH23! GG~ BK300s~ |  |
| 2980022 | NightMare_Gartua_01_003 | 63 | 200003 | 13 | TG2~ LP2 AH23! GG~ BK300s~ |  |
| 2980023 | NightMare_Gartua_01_004 | 63 | 200003 | 13 | TG2~ LP2 AH23! GG~ BK300s~ |  |
| 2980024 | NightMare_Gartua_01_005 | 63 | 200003 | 13 | TG2~ LP2 AH23! GG~ BK300s~ |  |
| 2980025 | NightMare_Gartua_01_006 | 63 | 200003 | 13 | TG2~ LP2 AH23! GG~ BK300s~ |  |
| 2980026 | NightMare_Gartua_01_007 | 63 | 200003 | 13 | TG2~ LP2 AH23! GG~ BK300s~ |  |
| 2980027 | NightMare_Gartua_01_008 | 63 | 200003 | 13 | TG2~ LP2 AH23! GG~ BK300s~ |  |
| 2980028 | NightMare_Gartua_01_009 | 63 | 200003 | 13 | TG2~ LP2 AH23! GG~ BK300s~ |  |
| 2980029 | NightMare_Gartua_01_010 | 63 | 200003 | 13 | TG2~ LP2 AH23! GG~ BK300s~ |  |
| 2980040 | NightMare_PinoBoss_01_001 | 45 | 200003 | 8 | AH1! GG~ BK300s~ SB1! NC1! |  |
| 2980041 | NightMare_PinoBoss_01_002 | 45 | 200003 | 8 | AH1! GG~ BK300s~ SB1! NC1! |  |
| 2980042 | NightMare_PinoBoss_01_003 | 45 | 200003 | 8 | AH1! GG~ BK300s~ SB1! NC1! |  |
| 2980043 | NightMare_PinoBoss_01_004 | 45 | 200003 | 8 | AH1! GG~ BK300s~ SB1! NC1! |  |
| 2980044 | NightMare_PinoBoss_01_005 | 45 | 200003 | 8 | AH1! GG~ BK300s~ SB1! NC1! | yes |
| 2980045 | NightMare_PinoBoss_01_006 | 45 | 200003 | 8 | AH1! GG~ BK300s~ SB1! NC1! |  |
| 2980046 | NightMare_PinoBoss_01_007 | 45 | 200003 | 8 | AH1! GG~ BK300s~ SB1! NC1! |  |
| 2980047 | NightMare_PinoBoss_01_008 | 45 | 200003 | 8 | AH1! GG~ BK300s~ SB1! NC1! |  |
| 2980048 | NightMare_PinoBoss_01_009 | 45 | 200003 | 8 | AH1! GG~ BK300s~ SB1! NC1! |  |
| 2980049 | NightMare_PinoBoss_01_010 | 45 | 200003 | 8 | AH1! GG~ BK300s~ SB1! NC1! |  |
| 2980050 | NightMare_Owltros_01_001 | 63 | 200003 | 7 | TG1~ LP3 AH4! GG~ BK300s~ |  |
| 2980051 | NightMare_Owltros_01_002 | 63 | 200003 | 7 | TG1~ LP3 AH4! GG~ BK300s~ |  |
| 2980052 | NightMare_Owltros_01_003 | 63 | 200003 | 7 | TG1~ LP3 AH4! GG~ BK300s~ |  |
| 2980053 | NightMare_Owltros_01_004 | 63 | 200003 | 7 | TG1~ LP3 AH4! GG~ BK300s~ |  |
| 2980054 | NightMare_Owltros_01_005 | 63 | 200003 | 7 | TG1~ LP3 AH4! GG~ BK300s~ |  |
| 2980055 | NightMare_Owltros_01_006 | 63 | 200003 | 7 | TG1~ LP3 AH4! GG~ BK300s~ |  |
| 2980056 | NightMare_Owltros_01_007 | 63 | 200003 | 7 | TG1~ LP3 AH4! GG~ BK300s~ |  |
| 2980057 | NightMare_Owltros_01_008 | 63 | 200003 | 7 | TG1~ LP3 AH4! GG~ BK300s~ |  |
| 2980058 | NightMare_Owltros_01_009 | 63 | 200003 | 7 | TG1~ LP3 AH4! GG~ BK300s~ |  |
| 2980059 | NightMare_Owltros_01_010 | 63 | 200003 | 7 | TG1~ LP3 AH4! GG~ BK300s~ |  |
| 2980060 | NightMare_Owlamus_01_001 | 45 | 200003 | 6 | TG1~ LP1 AF1 GG~ BK300s~ NC1! |  |
| 2980061 | NightMare_Owlamus_01_002 | 45 | 200003 | 6 | TG1~ LP1 AF1 GG~ BK300s~ NC1! |  |
| 2980062 | NightMare_Owlamus_01_003 | 45 | 200003 | 6 | TG1~ LP1 AF1 GG~ BK300s~ NC1! |  |
| 2980063 | NightMare_Owlamus_01_004 | 45 | 200003 | 6 | TG1~ LP1 AF1 GG~ BK300s~ NC1! |  |
| 2980064 | NightMare_Owlamus_01_005 | 45 | 200003 | 6 | TG1~ LP1 AF1 GG~ BK300s~ NC1! |  |
| 2980065 | NightMare_Owlamus_01_006 | 45 | 200003 | 6 | TG1~ LP1 AF1 GG~ BK300s~ NC1! |  |
| 2980066 | NightMare_Owlamus_01_007 | 45 | 200003 | 6 | TG1~ LP1 AF1 GG~ BK300s~ NC1! |  |
| 2980067 | NightMare_Owlamus_01_008 | 45 | 200003 | 6 | TG1~ LP1 AF1 GG~ BK300s~ NC1! |  |
| 2980068 | NightMare_Owlamus_01_009 | 45 | 200003 | 6 | TG1~ LP1 AF1 GG~ BK300s~ NC1! |  |
| 2980069 | NightMare_Owlamus_01_010 | 45 | 200003 | 6 | TG1~ LP1 AF1 GG~ BK300s~ NC1! |  |
| 2980070 | NightMare_Owlac_01_001 | 57 | 200003 | 8 | GG~ BK300s~ |  |
| 2980071 | NightMare_Owlac_01_002 | 57 | 200003 | 8 | GG~ BK300s~ |  |
| 2980072 | NightMare_Owlac_01_003 | 57 | 200003 | 8 | GG~ BK300s~ |  |
| 2980073 | NightMare_Owlac_01_004 | 57 | 200003 | 8 | GG~ BK300s~ |  |
| 2980074 | NightMare_Owlac_01_005 | 57 | 200003 | 8 | GG~ BK300s~ |  |
| 2980075 | NightMare_Owlac_01_006 | 57 | 200003 | 8 | GG~ BK300s~ |  |
| 2980076 | NightMare_Owlac_01_007 | 57 | 200003 | 8 | GG~ BK300s~ |  |
| 2980077 | NightMare_Owlac_01_008 | 57 | 200003 | 8 | GG~ BK300s~ |  |
| 2980078 | NightMare_Owlac_01_009 | 57 | 200003 | 8 | GG~ BK300s~ |  |
| 2980079 | NightMare_Owlac_01_010 | 57 | 200003 | 8 | GG~ BK300s~ |  |
| 2980080 | NightMare_Owlapri_01_001 | 63 | 200003 | 10 | TG1~ LP1 AF2 GG~ BK300s~ |  |
| 2980081 | NightMare_Owlapri_01_002 | 63 | 200003 | 10 | TG1~ LP1 AF2 GG~ BK300s~ |  |
| 2980082 | NightMare_Owlapri_01_003 | 63 | 200003 | 10 | TG1~ LP1 AF2 GG~ BK300s~ |  |
| 2980083 | NightMare_Owlapri_01_004 | 63 | 200003 | 10 | TG1~ LP1 AF2 GG~ BK300s~ |  |
| 2980084 | NightMare_Owlapri_01_005 | 63 | 200003 | 10 | TG1~ LP1 AF2 GG~ BK300s~ |  |
| 2980085 | NightMare_Owlapri_01_006 | 63 | 200003 | 10 | TG1~ LP1 AF2 GG~ BK300s~ |  |
| 2980086 | NightMare_Owlapri_01_007 | 63 | 200003 | 10 | TG1~ LP1 AF2 GG~ BK300s~ |  |
| 2980087 | NightMare_Owlapri_01_008 | 63 | 200003 | 10 | TG1~ LP1 AF2 GG~ BK300s~ |  |
| 2980088 | NightMare_Owlapri_01_009 | 63 | 200003 | 10 | TG1~ LP1 AF2 GG~ BK300s~ |  |
| 2980089 | NightMare_Owlapri_01_010 | 63 | 200003 | 10 | TG1~ LP1 AF2 GG~ BK300s~ |  |
| 2980090 | NightMare_Hiladr_01_001 | 45 | 200003 | 9 | LP1 GG~ BK300s~ NC1! |  |
| 2980091 | NightMare_Hiladr_01_002 | 45 | 200003 | 9 | LP1 GG~ BK300s~ NC1! |  |
| 2980092 | NightMare_Hiladr_01_003 | 45 | 200003 | 9 | LP1 GG~ BK300s~ NC1! |  |
| 2980093 | NightMare_Hiladr_01_004 | 45 | 200003 | 9 | LP1 GG~ BK300s~ NC1! |  |
| 2980094 | NightMare_Hiladr_01_005 | 45 | 200003 | 9 | LP1 GG~ BK300s~ NC1! |  |
| 2980095 | NightMare_Hiladr_01_006 | 45 | 200003 | 9 | LP1 GG~ BK300s~ NC1! |  |
| 2980096 | NightMare_Hiladr_01_007 | 45 | 200003 | 9 | LP1 GG~ BK300s~ NC1! |  |
| 2980097 | NightMare_Hiladr_01_008 | 45 | 200003 | 9 | LP1 GG~ BK300s~ NC1! |  |
| 2980098 | NightMare_Hiladr_01_009 | 45 | 200003 | 9 | LP1 GG~ BK300s~ NC1! |  |
| 2980099 | NightMare_Hiladr_01_010 | 45 | 200003 | 9 | LP1 GG~ BK300s~ NC1! |  |
| 2980100 | NightMare_Nua_01_001 | 45 | 200003 | 8 | LP2 GG~ BK300s~ |  |
| 2980101 | NightMare_Nua_01_002 | 45 | 200003 | 8 | LP2 GG~ BK300s~ |  |
| 2980102 | NightMare_Nua_01_003 | 45 | 200003 | 8 | LP2 GG~ BK300s~ |  |
| 2980103 | NightMare_Nua_01_004 | 45 | 200003 | 8 | LP2 GG~ BK300s~ |  |
| 2980104 | NightMare_Nua_01_005 | 45 | 200003 | 8 | LP2 GG~ BK300s~ |  |
| 2980105 | NightMare_Nua_01_006 | 45 | 200003 | 8 | LP2 GG~ BK300s~ |  |
| 2980106 | NightMare_Nua_01_007 | 45 | 200003 | 8 | LP2 GG~ BK300s~ |  |
| 2980107 | NightMare_Nua_01_008 | 45 | 200003 | 8 | LP2 GG~ BK300s~ |  |
| 2980108 | NightMare_Nua_01_009 | 45 | 200003 | 8 | LP2 GG~ BK300s~ |  |
| 2980109 | NightMare_Nua_01_010 | 45 | 200003 | 8 | LP2 GG~ BK300s~ |  |
| 2980110 | NightMare_Bargot_01_001 | 70 | 200003 | 8 | AH1! GG~ BK300s~ |  |
| 2980111 | NightMare_Bargot_01_002 | 70 | 200003 | 8 | AH1! GG~ BK300s~ |  |
| 2980112 | NightMare_Bargot_01_003 | 70 | 200003 | 8 | AH1! GG~ BK300s~ |  |
| 2980113 | NightMare_Bargot_01_004 | 70 | 200003 | 8 | AH1! GG~ BK300s~ |  |
| 2980114 | NightMare_Bargot_01_005 | 70 | 200003 | 8 | AH1! GG~ BK300s~ |  |
| 2980115 | NightMare_Bargot_01_006 | 70 | 200003 | 8 | AH1! GG~ BK300s~ |  |
| 2980116 | NightMare_Bargot_01_007 | 70 | 200003 | 8 | AH1! GG~ BK300s~ |  |
| 2980117 | NightMare_Bargot_01_008 | 70 | 200003 | 8 | AH1! GG~ BK300s~ |  |
| 2980118 | NightMare_Bargot_01_009 | 70 | 200003 | 8 | AH1! GG~ BK300s~ |  |
| 2980119 | NightMare_Bargot_01_010 | 70 | 200003 | 8 | AH1! GG~ BK300s~ |  |
| 2980120 | NightMare_Ulgorn_01_001 | 57 | 200003 | 9 | LP1 GG~ BK300s~ |  |
| 2980121 | NightMare_Ulgorn_01_002 | 57 | 200003 | 9 | LP1 GG~ BK300s~ |  |
| 2980122 | NightMare_Ulgorn_01_003 | 57 | 200003 | 9 | LP1 GG~ BK300s~ |  |
| 2980123 | NightMare_Ulgorn_01_004 | 57 | 200003 | 9 | LP1 GG~ BK300s~ |  |
| 2980124 | NightMare_Ulgorn_01_005 | 57 | 200003 | 9 | LP1 GG~ BK300s~ |  |
| 2980125 | NightMare_Ulgorn_01_006 | 57 | 200003 | 9 | LP1 GG~ BK300s~ |  |
| 2980126 | NightMare_Ulgorn_01_007 | 57 | 200003 | 9 | LP1 GG~ BK300s~ |  |
| 2980127 | NightMare_Ulgorn_01_008 | 57 | 200003 | 9 | LP1 GG~ BK300s~ |  |
| 2980128 | NightMare_Ulgorn_01_009 | 57 | 200003 | 9 | LP1 GG~ BK300s~ |  |
| 2980129 | NightMare_Ulgorn_01_010 | 57 | 200003 | 9 | LP1 GG~ BK300s~ |  |
| 2980130 | NightMare_Kaisinel_01_001 | 70 | 200003 | 9 | LP1 AF1 AH6! GG~ BK300s~ |  |
| 2980131 | NightMare_Kaisinel_01_002 | 70 | 200003 | 9 | LP1 AF1 AH6! GG~ BK300s~ |  |
| 2980132 | NightMare_Kaisinel_01_003 | 70 | 200003 | 9 | LP1 AF1 AH6! GG~ BK300s~ |  |
| 2980133 | NightMare_Kaisinel_01_004 | 70 | 200003 | 9 | LP1 AF1 AH6! GG~ BK300s~ |  |
| 2980134 | NightMare_Kaisinel_01_005 | 70 | 200003 | 9 | LP1 AF1 AH6! GG~ BK300s~ |  |
| 2980135 | NightMare_Kaisinel_01_006 | 70 | 200003 | 9 | LP1 AF1 AH6! GG~ BK300s~ |  |
| 2980136 | NightMare_Kaisinel_01_007 | 70 | 200003 | 9 | LP1 AF1 AH6! GG~ BK300s~ |  |
| 2980137 | NightMare_Kaisinel_01_008 | 70 | 200003 | 9 | LP1 AF1 AH6! GG~ BK300s~ |  |
| 2980138 | NightMare_Kaisinel_01_009 | 70 | 200003 | 9 | LP1 AF1 AH6! GG~ BK300s~ |  |
| 2980139 | NightMare_Kaisinel_01_010 | 70 | 200003 | 9 | LP1 AF1 AH6! GG~ BK300s~ |  |
| 2980140 | NightMare_DarkLeda_02_001 | 63 | 200003 | 11 | TG2~ LP3 GG~ BK300s~ |  |
| 2980141 | NightMare_DarkLeda_02_002 | 63 | 200003 | 11 | TG2~ LP3 GG~ BK300s~ |  |
| 2980142 | NightMare_DarkLeda_02_003 | 63 | 200003 | 11 | TG2~ LP3 GG~ BK300s~ |  |
| 2980143 | NightMare_DarkLeda_02_004 | 63 | 200003 | 11 | TG2~ LP3 GG~ BK300s~ |  |
| 2980144 | NightMare_DarkLeda_02_005 | 63 | 200003 | 11 | TG2~ LP3 GG~ BK300s~ |  |
| 2980145 | NightMare_DarkLeda_02_006 | 63 | 200003 | 11 | TG2~ LP3 GG~ BK300s~ |  |
| 2980146 | NightMare_DarkLeda_02_007 | 63 | 200003 | 11 | TG2~ LP3 GG~ BK300s~ |  |
| 2980147 | NightMare_DarkLeda_02_008 | 63 | 200003 | 11 | TG2~ LP3 GG~ BK300s~ |  |
| 2980148 | NightMare_DarkLeda_02_009 | 63 | 200003 | 11 | TG2~ LP3 GG~ BK300s~ |  |
| 2980149 | NightMare_DarkLeda_02_010 | 63 | 200003 | 11 | TG2~ LP3 GG~ BK300s~ |  |
| 2980150 | NightMare_Ferk_01_001 | 45 | 200003 | 9 | GG~ BK300s~ SB1! NC1! |  |
| 2980151 | NightMare_Ferk_01_002 | 45 | 200003 | 9 | GG~ BK300s~ SB1! NC1! |  |
| 2980152 | NightMare_Ferk_01_003 | 45 | 200003 | 9 | GG~ BK300s~ SB1! NC1! |  |
| 2980153 | NightMare_Ferk_01_004 | 45 | 200003 | 9 | GG~ BK300s~ SB1! NC1! |  |
| 2980154 | NightMare_Ferk_01_005 | 45 | 200003 | 9 | GG~ BK300s~ SB1! NC1! |  |
| 2980155 | NightMare_Ferk_01_006 | 45 | 200003 | 9 | GG~ BK300s~ SB1! NC1! |  |
| 2980156 | NightMare_Ferk_01_007 | 45 | 200003 | 9 | GG~ BK300s~ SB1! NC1! |  |
| 2980157 | NightMare_Ferk_01_008 | 45 | 200003 | 9 | GG~ BK300s~ SB1! NC1! |  |
| 2980158 | NightMare_Ferk_01_009 | 45 | 200003 | 9 | GG~ BK300s~ SB1! NC1! |  |
| 2980159 | NightMare_Ferk_01_010 | 45 | 200003 | 9 | GG~ BK300s~ SB1! NC1! |  |
| 2980160 | NightMare_Rathman_01_001 | 57 | 200003 | 10 | TG1~ LP2 GG~ BK300s~ |  |
| 2980161 | NightMare_Rathman_01_002 | 57 | 200003 | 10 | TG1~ LP2 GG~ BK300s~ |  |
| 2980162 | NightMare_Rathman_01_003 | 57 | 200003 | 10 | TG1~ LP2 GG~ BK300s~ |  |
| 2980163 | NightMare_Rathman_01_004 | 57 | 200003 | 10 | TG1~ LP2 GG~ BK300s~ |  |
| 2980164 | NightMare_Rathman_01_005 | 57 | 200003 | 10 | TG1~ LP2 GG~ BK300s~ |  |
| 2980165 | NightMare_Rathman_01_006 | 57 | 200003 | 10 | TG1~ LP2 GG~ BK300s~ |  |
| 2980166 | NightMare_Rathman_01_007 | 57 | 200003 | 10 | TG1~ LP2 GG~ BK300s~ |  |
| 2980167 | NightMare_Rathman_01_008 | 57 | 200003 | 10 | TG1~ LP2 GG~ BK300s~ |  |
| 2980168 | NightMare_Rathman_01_009 | 57 | 200003 | 10 | TG1~ LP2 GG~ BK300s~ |  |
| 2980169 | NightMare_Rathman_01_010 | 57 | 200003 | 10 | TG1~ LP2 GG~ BK300s~ |  |
| 2980170 | NightMare_Nothun_01_001 | 55 | 200003 | 9 | AH2! GG~ BK300s~ |  |
| 2980171 | NightMare_Nothun_01_002 | 55 | 200003 | 9 | AH2! GG~ BK300s~ |  |
| 2980172 | NightMare_Nothun_01_003 | 55 | 200003 | 9 | AH2! GG~ BK300s~ |  |
| 2980173 | NightMare_Nothun_01_004 | 55 | 200003 | 9 | AH2! GG~ BK300s~ |  |
| 2980174 | NightMare_Nothun_01_005 | 55 | 200003 | 9 | AH2! GG~ BK300s~ |  |
| 2980175 | NightMare_Nothun_01_006 | 55 | 200003 | 9 | AH2! GG~ BK300s~ |  |
| 2980176 | NightMare_Nothun_01_007 | 55 | 200003 | 9 | AH2! GG~ BK300s~ |  |
| 2980177 | NightMare_Nothun_01_008 | 55 | 200003 | 9 | AH2! GG~ BK300s~ |  |
| 2980178 | NightMare_Nothun_01_009 | 55 | 200003 | 9 | AH2! GG~ BK300s~ |  |
| 2980179 | NightMare_Nothun_01_010 | 55 | 200003 | 9 | AH2! GG~ BK300s~ |  |
| 2980180 | NightMare_Santras_01_001 | 63 | 200003 | 12 | LP1 AH1! GG~ BK300s~ NC1! |  |
| 2980181 | NightMare_Santras_01_002 | 63 | 200003 | 12 | LP1 AH1! GG~ BK300s~ NC1! |  |
| 2980182 | NightMare_Santras_01_003 | 63 | 200003 | 12 | LP1 AH1! GG~ BK300s~ NC1! |  |
| 2980183 | NightMare_Santras_01_004 | 63 | 200003 | 12 | LP1 AH1! GG~ BK300s~ NC1! |  |
| 2980184 | NightMare_Santras_01_005 | 63 | 200003 | 12 | LP1 AH1! GG~ BK300s~ NC1! |  |
| 2980185 | NightMare_Santras_01_006 | 63 | 200003 | 12 | LP1 AH1! GG~ BK300s~ NC1! |  |
| 2980186 | NightMare_Santras_01_007 | 63 | 200003 | 12 | LP1 AH1! GG~ BK300s~ NC1! |  |
| 2980187 | NightMare_Santras_01_008 | 63 | 200003 | 12 | LP1 AH1! GG~ BK300s~ NC1! |  |
| 2980188 | NightMare_Santras_01_009 | 63 | 200003 | 12 | LP1 AH1! GG~ BK300s~ NC1! |  |
| 2980189 | NightMare_Santras_01_010 | 63 | 200003 | 12 | LP1 AH1! GG~ BK300s~ NC1! |  |
| 2980190 | NightMare_Protect_01_001 | 70 | 200003 | 14 | LP1 AH13! GG~ BK300s~ NC3! |  |
| 2980191 | NightMare_Protect_01_002 | 70 | 200003 | 14 | LP1 AH13! GG~ BK300s~ NC3! |  |
| 2980192 | NightMare_Protect_01_003 | 70 | 200003 | 14 | LP1 AH13! GG~ BK300s~ NC3! |  |
| 2980193 | NightMare_Protect_01_004 | 70 | 200003 | 14 | LP1 AH13! GG~ BK300s~ NC3! |  |
| 2980194 | NightMare_Protect_01_005 | 70 | 200003 | 14 | LP1 AH13! GG~ BK300s~ NC3! |  |
| 2980195 | NightMare_Protect_01_006 | 70 | 200003 | 14 | LP1 AH13! GG~ BK300s~ NC3! |  |
| 2980196 | NightMare_Protect_01_007 | 70 | 200003 | 14 | LP1 AH13! GG~ BK300s~ NC3! |  |
| 2980197 | NightMare_Protect_01_008 | 70 | 200003 | 14 | LP1 AH13! GG~ BK300s~ NC3! |  |
| 2980198 | NightMare_Protect_01_009 | 70 | 200003 | 14 | LP1 AH13! GG~ BK300s~ NC3! |  |
| 2980199 | NightMare_Protect_01_010 | 70 | 200003 | 14 | LP1 AH13! GG~ BK300s~ NC3! |  |
| 2980200 | NightMare_Tasin_02_001 | 77 | 200003 | 12 | TG1~ LP3 GG~ BK300s~ |  |
| 2980201 | NightMare_Tasin_02_002 | 77 | 200003 | 12 | TG1~ LP3 GG~ BK300s~ |  |
| 2980202 | NightMare_Tasin_02_003 | 77 | 200003 | 12 | TG1~ LP3 GG~ BK300s~ |  |
| 2980203 | NightMare_Tasin_02_004 | 77 | 200003 | 12 | TG1~ LP3 GG~ BK300s~ |  |
| 2980204 | NightMare_Tasin_02_005 | 77 | 200003 | 12 | TG1~ LP3 GG~ BK300s~ |  |
| 2980205 | NightMare_Tasin_02_006 | 77 | 200003 | 12 | TG1~ LP3 GG~ BK300s~ |  |
| 2980206 | NightMare_Tasin_02_007 | 77 | 200003 | 12 | TG1~ LP3 GG~ BK300s~ |  |
| 2980207 | NightMare_Tasin_02_008 | 77 | 200003 | 12 | TG1~ LP3 GG~ BK300s~ |  |
| 2980208 | NightMare_Tasin_02_009 | 77 | 200003 | 12 | TG1~ LP3 GG~ BK300s~ |  |
| 2980209 | NightMare_Tasin_02_010 | 77 | 200003 | 12 | TG1~ LP3 GG~ BK300s~ |  |
| 2980210 | NightMare_Gerod_01_001 | 55 | 200003 | 12 | LP1 AH7! GG~ BK300s~ |  |
| 2980211 | NightMare_Gerod_01_002 | 55 | 200003 | 12 | LP1 AH7! GG~ BK300s~ |  |
| 2980212 | NightMare_Gerod_01_003 | 55 | 200003 | 12 | LP1 AH7! GG~ BK300s~ |  |
| 2980213 | NightMare_Gerod_01_004 | 55 | 200003 | 12 | LP1 AH7! GG~ BK300s~ |  |
| 2980214 | NightMare_Gerod_01_005 | 55 | 200003 | 12 | LP1 AH7! GG~ BK300s~ |  |
| 2980215 | NightMare_Gerod_01_006 | 55 | 200003 | 12 | LP1 AH7! GG~ BK300s~ |  |
| 2980216 | NightMare_Gerod_01_007 | 55 | 200003 | 12 | LP1 AH7! GG~ BK300s~ |  |
| 2980217 | NightMare_Gerod_01_008 | 55 | 200003 | 12 | LP1 AH7! GG~ BK300s~ |  |
| 2980218 | NightMare_Gerod_01_009 | 55 | 200003 | 12 | LP1 AH7! GG~ BK300s~ |  |
| 2980219 | NightMare_Gerod_01_010 | 55 | 200003 | 12 | LP1 AH7! GG~ BK300s~ |  |
| 2980220 | NightMare_Zikel_01_001 | 65 | 200003 | 10 | TG2~ LP1 GG~ BK300s~ DP1! |  |
| 2980221 | NightMare_Zikel_01_002 | 65 | 200003 | 10 | TG2~ LP1 GG~ BK300s~ DP1! |  |
| 2980222 | NightMare_Zikel_01_003 | 65 | 200003 | 10 | TG2~ LP1 GG~ BK300s~ DP1! |  |
| 2980223 | NightMare_Zikel_01_004 | 65 | 200003 | 10 | TG2~ LP1 GG~ BK300s~ DP1! |  |
| 2980224 | NightMare_Zikel_01_005 | 65 | 200003 | 10 | TG2~ LP1 GG~ BK300s~ DP1! |  |
| 2980225 | NightMare_Zikel_01_006 | 65 | 200003 | 10 | TG2~ LP1 GG~ BK300s~ DP1! |  |
| 2980226 | NightMare_Zikel_01_007 | 65 | 200003 | 10 | TG2~ LP1 GG~ BK300s~ DP1! |  |
| 2980227 | NightMare_Zikel_01_008 | 65 | 200003 | 10 | TG2~ LP1 GG~ BK300s~ DP1! |  |
| 2980228 | NightMare_Zikel_01_009 | 65 | 200003 | 10 | TG2~ LP1 GG~ BK300s~ DP1! |  |
| 2980229 | NightMare_Zikel_01_010 | 65 | 200003 | 10 | TG2~ LP1 GG~ BK300s~ DP1! |  |
| 2980230 | NightMare_Gauss_01_001 | 63 | 200003 | 12 | AH10! GG~ BK300s~ |  |
| 2980231 | NightMare_Gauss_01_002 | 63 | 200003 | 12 | AH10! GG~ BK300s~ |  |
| 2980232 | NightMare_Gauss_01_003 | 63 | 200003 | 12 | AH10! GG~ BK300s~ |  |
| 2980233 | NightMare_Gauss_01_004 | 63 | 200003 | 12 | AH10! GG~ BK300s~ |  |
| 2980234 | NightMare_Gauss_01_005 | 63 | 200003 | 12 | AH10! GG~ BK300s~ |  |
| 2980235 | NightMare_Gauss_01_006 | 63 | 200003 | 12 | AH10! GG~ BK300s~ |  |
| 2980236 | NightMare_Gauss_01_007 | 63 | 200003 | 12 | AH10! GG~ BK300s~ |  |
| 2980237 | NightMare_Gauss_01_008 | 63 | 200003 | 12 | AH10! GG~ BK300s~ |  |
| 2980238 | NightMare_Gauss_01_009 | 63 | 200003 | 12 | AH10! GG~ BK300s~ |  |
| 2980239 | NightMare_Gauss_01_010 | 63 | 200003 | 12 | AH10! GG~ BK300s~ |  |
| 2980240 | NightMare_FafBlood_01B_001 | 45 | 200003 | 8 | TG2~ LP1 GG~ BK300s~ |  |
| 2980241 | NightMare_FafBlood_01B_002 | 45 | 200003 | 8 | TG2~ LP1 GG~ BK300s~ |  |
| 2980242 | NightMare_FafBlood_01B_003 | 45 | 200003 | 8 | TG2~ LP1 GG~ BK300s~ |  |
| 2980243 | NightMare_FafBlood_01B_004 | 45 | 200003 | 8 | TG2~ LP1 GG~ BK300s~ |  |
| 2980244 | NightMare_FafBlood_01B_005 | 45 | 200003 | 8 | TG2~ LP1 GG~ BK300s~ |  |
| 2980245 | NightMare_FafBlood_01B_006 | 45 | 200003 | 8 | TG2~ LP1 GG~ BK300s~ |  |
| 2980246 | NightMare_FafBlood_01B_007 | 45 | 200003 | 8 | TG2~ LP1 GG~ BK300s~ |  |
| 2980247 | NightMare_FafBlood_01B_008 | 45 | 200003 | 8 | TG2~ LP1 GG~ BK300s~ |  |
| 2980248 | NightMare_FafBlood_01B_009 | 45 | 200003 | 8 | TG2~ LP1 GG~ BK300s~ |  |
| 2980249 | NightMare_FafBlood_01B_010 | 45 | 200003 | 8 | TG2~ LP1 GG~ BK300s~ |  |
| 2980250 | NightMare_Giselle_01_001 | 45 | 200003 | 9 | TG1~ LP2 GG~ BK300s~ |  |
| 2980251 | NightMare_Giselle_01_002 | 45 | 200003 | 9 | TG1~ LP2 GG~ BK300s~ |  |
| 2980252 | NightMare_Giselle_01_003 | 45 | 200003 | 9 | TG1~ LP2 GG~ BK300s~ |  |
| 2980253 | NightMare_Giselle_01_004 | 45 | 200003 | 9 | TG1~ LP2 GG~ BK300s~ |  |
| 2980254 | NightMare_Giselle_01_005 | 45 | 200003 | 9 | TG1~ LP2 GG~ BK300s~ |  |
| 2980255 | NightMare_Giselle_01_006 | 45 | 200003 | 9 | TG1~ LP2 GG~ BK300s~ |  |
| 2980256 | NightMare_Giselle_01_007 | 45 | 200003 | 9 | TG1~ LP2 GG~ BK300s~ |  |
| 2980257 | NightMare_Giselle_01_008 | 45 | 200003 | 9 | TG1~ LP2 GG~ BK300s~ |  |
| 2980258 | NightMare_Giselle_01_009 | 45 | 200003 | 9 | TG1~ LP2 GG~ BK300s~ |  |
| 2980259 | NightMare_Giselle_01_010 | 45 | 200003 | 9 | TG1~ LP2 GG~ BK300s~ |  |
| 2980260 | NightMare_Cromede_01_001 | 70 | 200003 | 13 | TG1~ LP5 AH3! GG~ BK300s~ |  |
| 2980261 | NightMare_Cromede_01_002 | 70 | 200003 | 13 | TG1~ LP5 AH3! GG~ BK300s~ |  |
| 2980262 | NightMare_Cromede_01_003 | 70 | 200003 | 13 | TG1~ LP5 AH3! GG~ BK300s~ |  |
| 2980263 | NightMare_Cromede_01_004 | 70 | 200003 | 13 | TG1~ LP5 AH3! GG~ BK300s~ |  |
| 2980264 | NightMare_Cromede_01_005 | 70 | 200003 | 13 | TG1~ LP5 AH3! GG~ BK300s~ |  |
| 2980265 | NightMare_Cromede_01_006 | 70 | 200003 | 13 | TG1~ LP5 AH3! GG~ BK300s~ |  |
| 2980266 | NightMare_Cromede_01_007 | 70 | 200003 | 13 | TG1~ LP5 AH3! GG~ BK300s~ |  |
| 2980267 | NightMare_Cromede_01_008 | 70 | 200003 | 13 | TG1~ LP5 AH3! GG~ BK300s~ |  |
| 2980268 | NightMare_Cromede_01_009 | 70 | 200003 | 13 | TG1~ LP5 AH3! GG~ BK300s~ |  |
| 2980269 | NightMare_Cromede_01_010 | 70 | 200003 | 13 | TG1~ LP5 AH3! GG~ BK300s~ |  |
| 2980270 | NightMare_Paton_01_001 | 77 | 200003 | 8 | GG~ BK300s~ |  |
| 2980271 | NightMare_Paton_01_002 | 77 | 200003 | 8 | GG~ BK300s~ |  |
| 2980272 | NightMare_Paton_01_003 | 77 | 200003 | 8 | GG~ BK300s~ |  |
| 2980273 | NightMare_Paton_01_004 | 77 | 200003 | 8 | GG~ BK300s~ |  |
| 2980274 | NightMare_Paton_01_005 | 77 | 200003 | 8 | GG~ BK300s~ |  |
| 2980275 | NightMare_Paton_01_006 | 77 | 200003 | 8 | GG~ BK300s~ |  |
| 2980276 | NightMare_Paton_01_007 | 77 | 200003 | 8 | GG~ BK300s~ |  |
| 2980277 | NightMare_Paton_01_008 | 77 | 200003 | 8 | GG~ BK300s~ |  |
| 2980278 | NightMare_Paton_01_009 | 77 | 200003 | 8 | GG~ BK300s~ |  |
| 2980279 | NightMare_Paton_01_010 | 77 | 200003 | 8 | GG~ BK300s~ |  |
| 2980280 | NightMare_Ateron_01_001 | 85 | 200003 | 11 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2980281 | NightMare_Ateron_01_002 | 85 | 200003 | 11 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2980282 | NightMare_Ateron_01_003 | 85 | 200003 | 11 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2980283 | NightMare_Ateron_01_004 | 85 | 200003 | 11 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2980284 | NightMare_Ateron_01_005 | 85 | 200003 | 11 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2980285 | NightMare_Ateron_01_006 | 85 | 200003 | 11 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2980286 | NightMare_Ateron_01_007 | 85 | 200003 | 11 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2980287 | NightMare_Ateron_01_008 | 85 | 200003 | 11 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2980288 | NightMare_Ateron_01_009 | 85 | 200003 | 11 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2980289 | NightMare_Ateron_01_010 | 85 | 200003 | 11 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2980290 | NightMare_Ged_01_001 | 77 | 200003 | 10 | TG1~ LP2 AH2! GG~ BK300s~ DP1! |  |
| 2980291 | NightMare_Ged_01_002 | 77 | 200003 | 10 | TG1~ LP2 AH2! GG~ BK300s~ DP1! |  |
| 2980292 | NightMare_Ged_01_003 | 77 | 200003 | 10 | TG1~ LP2 AH2! GG~ BK300s~ DP1! |  |
| 2980293 | NightMare_Ged_01_004 | 77 | 200003 | 10 | TG1~ LP2 AH2! GG~ BK300s~ DP1! |  |
| 2980294 | NightMare_Ged_01_005 | 77 | 200003 | 10 | TG1~ LP2 AH2! GG~ BK300s~ DP1! |  |
| 2980295 | NightMare_Ged_01_006 | 77 | 200003 | 10 | TG1~ LP2 AH2! GG~ BK300s~ DP1! |  |
| 2980296 | NightMare_Ged_01_007 | 77 | 200003 | 10 | TG1~ LP2 AH2! GG~ BK300s~ DP1! |  |
| 2980297 | NightMare_Ged_01_008 | 77 | 200003 | 10 | TG1~ LP2 AH2! GG~ BK300s~ DP1! |  |
| 2980298 | NightMare_Ged_01_009 | 77 | 200003 | 10 | TG1~ LP2 AH2! GG~ BK300s~ DP1! |  |
| 2980299 | NightMare_Ged_01_010 | 77 | 200003 | 10 | TG1~ LP2 AH2! GG~ BK300s~ DP1! |  |
| 2980300 | NightMare_Hibran_01_001 | 70 | 200003 | 10 | LP2 AH1! GG~ BK300s~ NC2! |  |
| 2980301 | NightMare_Hibran_01_002 | 70 | 200003 | 10 | LP2 AH1! GG~ BK300s~ NC2! |  |
| 2980302 | NightMare_Hibran_01_003 | 70 | 200003 | 10 | LP2 AH1! GG~ BK300s~ NC2! |  |
| 2980303 | NightMare_Hibran_01_004 | 70 | 200003 | 10 | LP2 AH1! GG~ BK300s~ NC2! |  |
| 2980304 | NightMare_Hibran_01_005 | 70 | 200003 | 10 | LP2 AH1! GG~ BK300s~ NC2! |  |
| 2980305 | NightMare_Hibran_01_006 | 70 | 200003 | 10 | LP2 AH1! GG~ BK300s~ NC2! |  |
| 2980306 | NightMare_Hibran_01_007 | 70 | 200003 | 10 | LP2 AH1! GG~ BK300s~ NC2! |  |
| 2980307 | NightMare_Hibran_01_008 | 70 | 200003 | 10 | LP2 AH1! GG~ BK300s~ NC2! |  |
| 2980308 | NightMare_Hibran_01_009 | 70 | 200003 | 10 | LP2 AH1! GG~ BK300s~ NC2! |  |
| 2980309 | NightMare_Hibran_01_010 | 70 | 200003 | 10 | LP2 AH1! GG~ BK300s~ NC2! |  |

### Dungeon:Ascension (9)

| id | name | lvl | maps | skills | mechanics (S ok, ~ partial, ! missing) | rec |
|---|---|---|---|---|---|---|
| 2701157 | M_Dungeon_0004_Narun_01 | 32 | 200004 | 5 | GG~ HP6 NA2~ SB1! NC1! |  |
| 2702111 | M_Dungeon_0006_Boss_01 | 50 | 200006 | 12 | PT1~ AH34! GG~ HP3 |  |
| 2700242 | M_L1_DreamDebris_DebrisGuardian_05_001 | 22 | 210003,220003 | 9 | TG2~ LP2 GG~ HP6 |  |
| 2701048 | M_Dungeon_L_0014_SanctuaryKeeper_01 | 45 | 210014 | 9 | AH2! GG~ HP3 |  |
| 2701155 | M_Dungeon_L_0015_DreamZikel_01 | 45 | 210015 | 11 | TG3~ LP2 AH1! GG~ HP3 DP1! |  |
| 2702110 | M_Dungeon_L_0027_Boss_01 | 50 | 210027 | 7 | LP1 GG~ HP6 |  |
| 2701051 | M_Dungeon_D_0014_DarkLeda_01_01 | 45 | 220014 | 10 | TG1~ LP1 AH2! GG~ HP3 NC1! |  |
| 2701156 | M_Dungeon_D_0015_DreamKaisinel_01 | 45 | 220015 | 9 | LP1 AF1 AH6! GG~ HP3 |  |
| 2702598 | M_Dungeon_D_0027_Boss_01 | 50 | 220027 | 8 | TG1~ LP1 AH1! GG~ HP6 |  |

### Dungeon:Awaken (44)

| id | name | lvl | maps | skills | mechanics (S ok, ~ partial, ! missing) | rec |
|---|---|---|---|---|---|---|
| 2701753 | M_Dungeon_Awaken_001_01_DebrisGuardian_05_001 | 45 | 910011 | 9 | TG2~ LP2 GG~ HP1 NA9~ |  |
| 2701789 | M_Dungeon_Awaken_001_02_DebrisGuardian_05_001 | 50 | 910012 | 9 | TG2~ LP2 GG~ HP1 NA9~ |  |
| 2701807 | M_Dungeon_Awaken_001_03_DebrisGuardian_05_001 | 55 | 910013 | 9 | TG2~ LP2 GG~ HP1 NA9~ |  |
| 2701866 | M_Dungeon_Awaken_001_04_DebrisGuardian_05_001 | 60 | 910014 | 9 | TG2~ LP2 GG~ HP1 NA9~ |  |
| 2702396 | M_Dungeon_Awaken_001_05_DebrisGuardian_05_001 | 65 | 910015 | 9 | TG2~ LP2 GG~ HP1 NA9~ |  |
| 2702409 | M_Dungeon_Awaken_001_06_DebrisGuardian_05_001 | 70 | 910016 | 9 | TG2~ LP2 GG~ HP1 NA9~ |  |
| 2701964 | M_Dungeon_Awaken_002_01_TasinB_01 | 45 | 910021 | 10 | TG1~ LP2 GG~ HP1 NA9~ |  |
| 2701990 | M_Dungeon_Awaken_002_02_TasinB_01 | 50 | 910022 | 10 | TG1~ LP2 GG~ HP1 NA9~ |  |
| 2702001 | M_Dungeon_Awaken_002_03_TasinB_01 | 55 | 910023 | 10 | TG1~ LP2 GG~ HP1 NA9~ |  |
| 2702012 | M_Dungeon_Awaken_002_04_TasinB_01 | 60 | 910024 | 10 | TG1~ LP2 GG~ HP1 NA9~ |  |
| 2702274 | M_Dungeon_Awaken_002_05_TasinB_01 | 65 | 910025 | 10 | TG1~ LP2 GG~ HP1 NA9~ |  |
| 2702286 | M_Dungeon_Awaken_002_06_TasinB_01 | 70 | 910026 | 10 | TG1~ LP2 GG~ HP1 NA9~ |  |
| 2701853 | M_Dungeon_Awaken_003_01_Duduri_01_01 | 45 | 910031 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2701854 | M_Dungeon_Awaken_003_01_Duduri_01_02 | 45 | 910031 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2701841 | M_Dungeon_Awaken_003_02_Duduri_01_01 | 50 | 910032 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2701842 | M_Dungeon_Awaken_003_02_Duduri_01_02 | 50 | 910032 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2701829 | M_Dungeon_Awaken_003_03_Duduri_01_01 | 55 | 910033 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2701830 | M_Dungeon_Awaken_003_03_Duduri_01_02 | 55 | 910033 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2701774 | M_Dungeon_Awaken_003_04_Duduri_01_01 | 60 | 910034 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2701788 | M_Dungeon_Awaken_003_04_Duduri_01_02 | 60 | 910034 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2702372 | M_Dungeon_Awaken_003_05_Duduri_01_01 | 65 | 910035 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2702373 | M_Dungeon_Awaken_003_05_Duduri_01_02 | 65 | 910035 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2702384 | M_Dungeon_Awaken_003_06_Duduri_01_01 | 70 | 910036 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2702385 | M_Dungeon_Awaken_003_06_Duduri_01_02 | 70 | 910036 | 6 | TG1~ AH2! GG~ NA11~ |  |
| 2701892 | M_Dungeon_Awaken_004_01_DarkLeda_01_001 | 45 | 910041 | 10 | TG1~ LP1 AH2! GG~ HP1 NA9~ NC1! |  |
| 2701913 | M_Dungeon_Awaken_004_02_DarkLeda_01_001 | 50 | 910042 | 10 | TG1~ LP1 AH2! GG~ HP1 NA9~ NC1! |  |
| 2701930 | M_Dungeon_Awaken_004_03_DarkLeda_01_001 | 55 | 910043 | 10 | TG1~ LP1 AH2! GG~ HP1 NA9~ NC1! |  |
| 2701947 | M_Dungeon_Awaken_004_04_DarkLeda_01_001 | 60 | 910044 | 10 | TG1~ LP1 AH2! GG~ HP1 NA9~ NC1! |  |
| 2702298 | M_Dungeon_Awaken_004_05_DarkLeda_01_001 | 65 | 910045 | 10 | TG1~ LP1 AH2! GG~ HP1 NA9~ NC1! |  |
| 2702309 | M_Dungeon_Awaken_004_06_DarkLeda_01_001 | 70 | 910046 | 10 | TG1~ LP1 AH2! GG~ HP1 NA9~ NC1! |  |
| 2702461 | M_Dungeon_Awaken_005_01_Nua_01_V01_001 | 45 | 910051 | 9 | LP3 GG~ HP1 NA9~ |  |
| 2702648 | M_Dungeon_Awaken_005_02_Nua_01_V01_001 | 50 | 910052 | 9 | LP3 GG~ HP1 NA9~ |  |
| 2702659 | M_Dungeon_Awaken_005_03_Nua_01_V01_001 | 55 | 910053 | 9 | LP3 GG~ HP1 NA9~ |  |
| 2702670 | M_Dungeon_Awaken_005_04_Nua_01_V01_001 | 60 | 910054 | 9 | LP3 GG~ HP1 NA9~ |  |
| 2702681 | M_Dungeon_Awaken_005_05_Nua_01_V01_001 | 65 | 910055 | 9 | LP3 GG~ HP1 NA9~ |  |
| 2702692 | M_Dungeon_Awaken_005_06_Nua_01_V01_001 | 70 | 910056 | 9 | LP3 GG~ HP1 NA9~ |  |
| 2702703 | M_Dungeon_Awaken_005_07_Nua_01_V01_001 | 75 | 910057 | 9 | LP3 GG~ HP1 NA9~ |  |
| 2702359 | M_Dungeon_Awaken_006_01_Owlapri_01 | 45 | 910061 | 10 | TG1~ LP1 AF2 GG~ HP1 NA9~ |  |
| 2702462 | M_Dungeon_Awaken_006_02_Owlapri_01 | 50 | 910062 | 10 | TG1~ LP1 AF2 GG~ HP1 NA9~ |  |
| 2702475 | M_Dungeon_Awaken_006_03_Owlapri_01 | 55 | 910063 | 10 | TG1~ LP1 AF2 GG~ HP1 NA9~ |  |
| 2702488 | M_Dungeon_Awaken_006_04_Owlapri_01 | 60 | 910064 | 10 | TG1~ LP1 AF2 GG~ HP1 NA9~ |  |
| 2702501 | M_Dungeon_Awaken_006_05_Owlapri_01 | 65 | 910065 | 10 | TG1~ LP1 AF2 GG~ HP1 NA9~ |  |
| 2702611 | M_Dungeon_Awaken_006_06_Owlapri_01 | 70 | 910066 | 10 | TG1~ LP1 AF2 GG~ HP1 NA9~ |  |
| 2702623 | M_Dungeon_Awaken_006_07_Owlapri_01 | 75 | 910067 | 10 | TG1~ LP1 AF2 GG~ HP1 NA9~ |  |

### Dungeon:Matching/Suppression (72)

| id | name | lvl | maps | skills | mechanics (S ok, ~ partial, ! missing) | rec |
|---|---|---|---|---|---|---|
| 2370001 | M_Draconic_001_Easy_Boss_Nahid_01_001 | 45 | 156000 | 12 | TG1~ LP1 PT1~ GG~ |  |
| 2370002 | M_Draconic_001_Easy_Boss_Radvima_01_001 | 45 | 156000 | 8 | AH9! GG~ |  |
| 2370003 | M_Draconic_001_Easy_Boss_BeritraD_02_001 | 45 | 156000 | 7 | TG1~ PT1~ GG~ |  |
| 2370004 | M_Draconic_001_Easy_Boss_BeritraB_01_001 | 45 | 156000 | 8 | TG1~ PT1~ GG~ |  |
| 2370038 | M_Draconic_001_Normal_Boss_Nahid_01_001 | 50 | 156001 | 12 | TG1~ LP1 PT1~ GG~ |  |
| 2370039 | M_Draconic_001_Normal_Boss_Radvima_01_001 | 50 | 156001 | 8 | AH9! GG~ |  |
| 2370040 | M_Draconic_001_Normal_Boss_BeritraD_02_001 | 50 | 156001 | 7 | TG1~ PT1~ GG~ |  |
| 2370041 | M_Draconic_001_Normal_Boss_BeritraB_01_001 | 50 | 156001 | 8 | TG1~ PT1~ GG~ |  |
| 2370072 | M_Draconic_001_Hard_Boss_Nahid_01_001 | 55 | 156002 | 12 | TG1~ LP1 PT1~ GG~ |  |
| 2370073 | M_Draconic_001_Hard_Boss_Radvima_01_001 | 55 | 156002 | 8 | AH9! GG~ |  |
| 2370074 | M_Draconic_001_Hard_Boss_BeritraD_02_001 | 55 | 156002 | 7 | TG1~ PT1~ GG~ |  |
| 2370075 | M_Draconic_001_Hard_Boss_BeritraB_01_001 | 55 | 156002 | 8 | TG1~ PT1~ GG~ |  |
| 2370201 | M_Draconic_002_Easy_Boss_Nahid_01_001 | 45 | 156010 | 12 | TG1~ LP1 PT1~ GG~ |  |
| 2370202 | M_Draconic_002_Easy_Boss_Radvima_01_001 | 45 | 156010 | 8 | AH9! GG~ |  |
| 2370203 | M_Draconic_002_Easy_Boss_BeritraD_02_001 | 45 | 156010 | 7 | TG1~ PT1~ GG~ |  |
| 2370204 | M_Draconic_002_Easy_Boss_BeritraB_01_001 | 45 | 156010 | 8 | TG1~ PT1~ GG~ |  |
| 2370221 | M_Draconic_002_Normal_Boss_Nahid_01_001 | 50 | 156011 | 12 | TG1~ LP1 PT1~ GG~ |  |
| 2370222 | M_Draconic_002_Normal_Boss_Radvima_01_001 | 50 | 156011 | 8 | AH9! GG~ |  |
| 2370223 | M_Draconic_002_Normal_Boss_BeritraD_02_001 | 50 | 156011 | 7 | TG1~ PT1~ GG~ |  |
| 2370224 | M_Draconic_002_Normal_Boss_BeritraB_01_001 | 50 | 156011 | 8 | TG1~ PT1~ GG~ |  |
| 2370241 | M_Draconic_002_Hard_Boss_Nahid_01_001 | 55 | 156012 | 12 | TG1~ LP1 PT1~ GG~ |  |
| 2370242 | M_Draconic_002_Hard_Boss_Radvima_01_001 | 55 | 156012 | 8 | AH9! GG~ |  |
| 2370243 | M_Draconic_002_Hard_Boss_BeritraD_02_001 | 55 | 156012 | 7 | TG1~ PT1~ GG~ |  |
| 2370244 | M_Draconic_002_Hard_Boss_BeritraB_01_001 | 55 | 156012 | 8 | TG1~ PT1~ GG~ |  |
| 2370401 | M_Draconic_004_Easy_Boss_Nahid_01_V02 | 45 | 156030 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370402 | M_Draconic_004_Easy_Boss_Radvima_01_V04 | 45 | 156030 | 8 | GG~ |  |
| 2370403 | M_Draconic_004_Easy_Boss_BeritraD_02_V02 | 45 | 156030 | 7 | TG1~ PT1~ GG~ |  |
| 2370404 | M_Draconic_004_Easy_Boss_BeritraB_01_V02 | 45 | 156030 | 6 | PT1~ AH1! GG~ |  |
| 2370430 | M_Draconic_004_Normal_Boss_Nahid_01_V02 | 50 | 156031 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370431 | M_Draconic_004_Normal_Boss_Radvima_01_V04 | 50 | 156031 | 8 | GG~ |  |
| 2370432 | M_Draconic_004_Normal_Boss_BeritraD_02_V02 | 50 | 156031 | 7 | TG1~ PT1~ GG~ |  |
| 2370433 | M_Draconic_004_Normal_Boss_BeritraB_01_V02 | 50 | 156031 | 6 | PT1~ AH1! GG~ |  |
| 2370460 | M_Draconic_004_Hard_Boss_Nahid_01_V02 | 55 | 156032 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370461 | M_Draconic_004_Hard_Boss_Radvima_01_V04 | 55 | 156032 | 8 | GG~ |  |
| 2370462 | M_Draconic_004_Hard_Boss_BeritraD_02_V02 | 55 | 156032 | 7 | TG1~ PT1~ GG~ |  |
| 2370463 | M_Draconic_004_Hard_Boss_BeritraB_01_V02 | 55 | 156032 | 6 | PT1~ AH1! GG~ |  |
| 2370700 | M_Draconic_004_Extreme_Boss_Nahid_01_V02 | 60 | 156033 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370701 | M_Draconic_004_Extreme_Boss_Radvima_01_V04 | 60 | 156033 | 8 | GG~ |  |
| 2370702 | M_Draconic_004_Extreme_Boss_BeritraD_02_V02 | 60 | 156033 | 7 | TG1~ PT1~ GG~ |  |
| 2370703 | M_Draconic_004_Extreme_Boss_BeritraB_01_V02 | 60 | 156033 | 6 | PT1~ AH1! GG~ |  |
| 2370735 | M_Draconic_004_Despair_Boss_Nahid_01_V02 | 65 | 156034 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370736 | M_Draconic_004_Despair_Boss_Radvima_01_V04 | 65 | 156034 | 8 | GG~ |  |
| 2370737 | M_Draconic_004_Despair_Boss_BeritraD_02_V02 | 65 | 156034 | 7 | TG1~ PT1~ GG~ |  |
| 2370738 | M_Draconic_004_Despair_Boss_BeritraB_01_V02 | 65 | 156034 | 6 | PT1~ AH1! GG~ |  |
| 2370770 | M_Draconic_004_Hell_Boss_Nahid_01_V02 | 76 | 156035 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370771 | M_Draconic_004_Hell_Boss_Radvima_01_V04 | 76 | 156035 | 8 | GG~ |  |
| 2370772 | M_Draconic_004_Hell_Boss_BeritraD_02_V02 | 76 | 156035 | 7 | TG1~ PT1~ GG~ |  |
| 2370773 | M_Draconic_004_Hell_Boss_BeritraB_01_V02 | 76 | 156035 | 6 | PT1~ AH1! GG~ |  |
| 2370502 | M_Draconic_005_Easy_Boss_Nahid_01_V02 | 45 | 156040 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370503 | M_Draconic_005_Easy_Boss_Radvima_01_V04 | 45 | 156040 | 8 | GG~ |  |
| 2370504 | M_Draconic_005_Easy_Boss_BeritraD_02_V02 | 45 | 156040 | 7 | TG1~ PT1~ GG~ |  |
| 2370505 | M_Draconic_005_Easy_Boss_BeritraB_01_V02 | 45 | 156040 | 6 | PT1~ AH1! GG~ |  |
| 2370533 | M_Draconic_005_Normal_Boss_Nahid_01_V02 | 50 | 156041 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370534 | M_Draconic_005_Normal_Boss_Radvima_01_V04 | 50 | 156041 | 8 | GG~ |  |
| 2370535 | M_Draconic_005_Normal_Boss_BeritraD_02_V02 | 50 | 156041 | 7 | TG1~ PT1~ GG~ |  |
| 2370536 | M_Draconic_005_Normal_Boss_BeritraB_01_V02 | 50 | 156041 | 6 | PT1~ AH1! GG~ |  |
| 2370564 | M_Draconic_005_Hard_Boss_Nahid_01_V02 | 55 | 156042 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370565 | M_Draconic_005_Hard_Boss_Radvima_01_V04 | 55 | 156042 | 8 | GG~ |  |
| 2370566 | M_Draconic_005_Hard_Boss_BeritraD_02_V02 | 55 | 156042 | 7 | TG1~ PT1~ GG~ |  |
| 2370567 | M_Draconic_005_Hard_Boss_BeritraB_01_V02 | 55 | 156042 | 6 | PT1~ AH1! GG~ |  |
| 2370800 | M_Draconic_005_Extreme_Boss_Nahid_01_V02 | 60 | 156043 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370801 | M_Draconic_005_Extreme_Boss_Radvima_01_V04 | 60 | 156043 | 8 | GG~ |  |
| 2370802 | M_Draconic_005_Extreme_Boss_BeritraD_02_V02 | 60 | 156043 | 7 | TG1~ PT1~ GG~ |  |
| 2370803 | M_Draconic_005_Extreme_Boss_BeritraB_01_V02 | 60 | 156043 | 6 | PT1~ AH1! GG~ |  |
| 2370831 | M_Draconic_005_Despair_Boss_Nahid_01_V02 | 65 | 156044 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370832 | M_Draconic_005_Despair_Boss_Radvima_01_V04 | 65 | 156044 | 8 | GG~ |  |
| 2370833 | M_Draconic_005_Despair_Boss_BeritraD_02_V02 | 65 | 156044 | 7 | TG1~ PT1~ GG~ |  |
| 2370834 | M_Draconic_005_Despair_Boss_BeritraB_01_V02 | 65 | 156044 | 6 | PT1~ AH1! GG~ |  |
| 2370862 | M_Draconic_005_Hell_Boss_Nahid_01_V02 | 76 | 156045 | 12 | TG1~ LP1 AH2! GG~ |  |
| 2370863 | M_Draconic_005_Hell_Boss_Radvima_01_V04 | 76 | 156045 | 8 | GG~ |  |
| 2370864 | M_Draconic_005_Hell_Boss_BeritraD_02_V02 | 76 | 156045 | 7 | TG1~ PT1~ GG~ |  |
| 2370865 | M_Draconic_005_Hell_Boss_BeritraB_01_V02 | 76 | 156045 | 6 | PT1~ AH1! GG~ |  |

### Dungeon:Party (196)

| id | name | lvl | maps | skills | mechanics (S ok, ~ partial, ! missing) | rec |
|---|---|---|---|---|---|---|
| 2300104 | M_PD_Krao_KraoElite_03 | 45 | 600001 | 6 | TG1~ LP1 AH1! GG~ |  |
| 2300108 | M_PD_Krao_KraoElite_03_V01 | 45 | 600001 | 8 | TG1~ LP1 GG~ |  |
| 2300171 | M_PD_Krao_Berc_P1_Type01 | 45 | 600001 | 12 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2310104 | M_PD_Krao_KraoElite_03_Easy | 22 | 600002 | 6 | TG1~ LP1 AH1! GG~ |  |
| 2310108 | M_PD_Krao_KraoElite_03_V01_Easy | 22 | 600002 | 8 | TG1~ LP1 GG~ |  |
| 2310171 | M_PD_Krao_Berc_P1_Type01_Easy | 22 | 600002 | 12 | TG2~ LP2 AH2! GG~ HP2 |  |
| 2320104 | M_PD_Krao_KraoElite_03_hard | 70 | 600004 | 6 | TG1~ LP1 AH1! GG~ |  |
| 2320108 | M_PD_Krao_KraoElite_03_V01_hard | 70 | 600004 | 8 | TG1~ LP1 GG~ |  |
| 2320171 | M_PD_Krao_Berc_P1_Type01_hard | 70 | 600004 | 12 | TG2~ LP2 AH2! GG~ BK300s~ |  |
| 2310206 | M_PD_UC_SubBoss01_Easy | 30 | 600011 | 8 | GG~ SB1! DP2! |  |
| 2310207 | M_PD_UC_SubBoss02_Easy | 30 | 600011 | 8 | GG~ SB1! |  |
| 2310218 | M_PD_UC_Owldor_01_Easy | 30 | 600011 | 12 | TG2~ LP4 AH3! GG~ HP2 DP1! NC1! |  |
| 2300206 | M_PD_UC_SubBoss01 | 54 | 600012 | 8 | GG~ SB1! DP2! |  |
| 2300207 | M_PD_UC_SubBoss02 | 54 | 600012 | 8 | GG~ SB1! |  |
| 2300218 | M_PD_UC_Owldor_01 | 54 | 600012 | 12 | TG2~ LP4 AH3! GG~ BK300s~ DP1! NC1! |  |
| 2320206 | M_PD_UC_SubBoss01_hard | 72 | 600013 | 8 | GG~ SB1! DP2! |  |
| 2320207 | M_PD_UC_SubBoss02_hard | 72 | 600013 | 8 | GG~ SB1! |  |
| 2320218 | M_PD_UC_Owldor_01_hard | 72 | 600013 | 12 | TG2~ LP4 AH3! GG~ BK300s~ DP1! NC1! |  |
| 2310310 | M_PD_Fire_Temple_Rotan_001_Easy | 37 | 600021 | 6 | TG1~ GG~ SB1! |  |
| 2310311 | M_PD_Fire_Temple_FireGuard_001_Easy | 37 | 600021 | 10 | TG1~ LP1 AH3! GG~ CS1! |  |
| 2310312 | M_PD_Fire_Temple_AshGuard_001_Easy | 37 | 600021 | 9 | LP1 AH5! GG~ |  |
| 2310371 | M_PD_Fire_Temple_Cromede_01_001_Easy | 37 | 600021 | 15 | LP2 GG~ HP2 CS1! |  |
| 2300310 | M_PD_Fire_Temple_Rotan_001 | 63 | 600022 | 6 | TG1~ GG~ SB1! |  |
| 2300311 | M_PD_Fire_Temple_FireGuard_001 | 63 | 600022 | 10 | TG1~ LP1 AH3! GG~ CS1! |  |
| 2300312 | M_PD_Fire_Temple_AshGuard_001 | 63 | 600022 | 9 | LP1 AH5! GG~ |  |
| 2300371 | M_PD_Fire_Temple_Cromede_01_001 | 63 | 600022 | 15 | LP2 GG~ BK300s~ CS1! |  |
| 2320310 | M_PD_Fire_Temple_Rotan_001_hard | 74 | 600023 | 6 | TG1~ GG~ SB1! |  |
| 2320311 | M_PD_Fire_Temple_FireGuard_001_hard | 74 | 600023 | 10 | TG1~ LP1 AH3! GG~ CS1! |  |
| 2320312 | M_PD_Fire_Temple_AshGuard_001_hard | 74 | 600023 | 9 | LP1 AH5! GG~ |  |
| 2320371 | M_PD_Fire_Temple_Cromede_01_001_hard | 74 | 600023 | 15 | TG1~ LP3 GG~ BK300s~ CS1! |  |
| 2310401 | M_PD_Draupnir_NagaElite_01_easy | 45 | 600031 | 9 | TG1~ LP1 AF1 GG~ SB1! DP1! |  |
| 2310403 | M_PD_Draupnir_NagaElite_02_easy | 45 | 600031 | 8 | TG1~ AH1! GG~ |  |
| 2310470 | M_PD_Draupnir_Bakarma_01_easy | 45 | 600031 | 0 |  |  |
| 2310471 | M_PD_Draupnir_Bakarma_02_easy | 45 | 600031 | 16 | TG1~ AH3! GG~ HP2 NC1! |  |
| 2300401 | M_PD_Draupnir_NagaElite_01 | 45 | 600032 | 9 | TG1~ LP1 AF1 GG~ SB1! DP1! |  |
| 2300403 | M_PD_Draupnir_NagaElite_02 | 45 | 600032 | 8 | TG1~ AH1! GG~ |  |
| 2300470 | M_PD_Draupnir_Bakarma_01 | 45 | 600032 | 0 |  |  |
| 2300471 | M_PD_Draupnir_Bakarma_02 | 45 | 600032 | 14 | TG1~ AH2! GG~ BK300s~ NC1! |  |
| 2320401 | M_PD_Draupnir_NagaElite_01_hard | 70 | 600033 | 9 | TG1~ LP1 AF1 GG~ SB1! DP1! |  |
| 2320403 | M_PD_Draupnir_NagaElite_02_hard | 70 | 600033 | 8 | TG1~ AH1! GG~ |  |
| 2320470 | M_PD_Draupnir_Bakarma_01_hard | 70 | 600033 | 0 |  |  |
| 2320471 | M_PD_Draupnir_Bakarma_02_hard | 70 | 600033 | 14 | TG1~ AH2! GG~ BK300s~ NC1! |  |
| 2300502 | M_PD_DDN_Easy_Durbati_01_006_1 | 65 | 600041 | 0 | GG~ |  |
| 2300507 | M_PD_DDN_Easy_BeritraA_01_V01_001 | 65 | 600041 | 11 | PT1~ AH1! GG~ NC1! |  |
| 2300508 | M_PD_DDN_Easy_BeritraD_01_V01_001 | 65 | 600041 | 10 | TG1~ LP1 AH9! GG~ NC1! |  |
| 2300509 | M_PD_DDN_Easy_Durbati_01 | 65 | 600041 | 8 | TG2~ AH4! GG~ HP2 |  |
| 2300517 | M_PD_DDN_Easy_Durbati_01_006_2 | 65 | 600041 | 0 | GG~ |  |
| 2300519 | M_PD_DDN_Easy_Invisible_Dramata_Event_001 | 65 | 600041 | 0 |  |  |
| 2310507 | M_PD_DDN_Normal_BeritraA_01_V01_001 | 65 | 600042 | 11 | PT1~ AH1! GG~ NC1! |  |
| 2310508 | M_PD_DDN_Normal_BeritraD_01_V01_001 | 65 | 600042 | 10 | TG1~ LP1 AH9! GG~ NC1! |  |
| 2310509 | M_PD_DDN_Normal_Durbati_01 | 65 | 600042 | 8 | TG2~ AH4! GG~ BK300s~ |  |
| 2310519 | M_PD_DDN_Normal_Invisible_Dramata_Event_001 | 65 | 600042 | 0 |  |  |
| 2310502 | M_PD_DDN_Durbati_01_006_1 | 65 | 600042,610044 | 0 | GG~ |  |
| 2310517 | M_PD_DDN_Durbati_01_006_2 | 65 | 600042,610044 | 0 | GG~ |  |
| 2300601 | M_PD_DeusCenter_Atiel_01_001_easy | 45 | 600051 | 13 | TG1~ LP3 AH2! GG~ HP2 DP1! |  |
| 2300615 | M_PD_DeusCenter_Nazmun_01_easy | 45 | 600051 | 9 | TG3~ AH3! GG~ NC1! |  |
| 2300616 | M_PD_DeusCenter_Gelcos_01_easy | 45 | 600051 | 10 | AH1! GG~ |  |
| 2320601 | M_PD_DeusCenter_Atiel_01_001_Grade1 | 56 | 600053 | 13 | TG1~ LP3 AH2! GG~ BK300s~ DP1! |  |
| 2320615 | M_PD_DeusCenter_Nazmun_01_Grade1 | 56 | 600053 | 9 | TG3~ AH3! GG~ NC1! |  |
| 2320616 | M_PD_DeusCenter_Gelcos_01_Grade1 | 56 | 600053 | 10 | AH1! GG~ |  |
| 2340601 | M_PD_DeusCenter_Atiel_01_001_Grade3 | 60 | 600055 | 13 | TG1~ LP3 AH2! GG~ BK300s~ DP1! |  |
| 2340615 | M_PD_DeusCenter_Nazmun_01_Grade3 | 60 | 600055 | 9 | TG3~ AH3! GG~ NC1! |  |
| 2340616 | M_PD_DeusCenter_Gelcos_01_Grade3 | 60 | 600055 | 10 | AH1! GG~ |  |
| 2343601 | M_PD_DeusCenter_Atiel_01_001_Grade6 | 64 | 600058 | 13 | TG1~ LP3 AH2! GG~ BK300s~ DP1! |  |
| 2343615 | M_PD_DeusCenter_Nazmun_01_Grade6 | 64 | 600058 | 9 | TG3~ AH3! GG~ NC1! |  |
| 2343616 | M_PD_DeusCenter_Gelcos_01_Grade6 | 64 | 600058 | 10 | AH1! GG~ |  |
| 2330707 | M_PD_Arkanis_NagaElite_01_V01_001_Grade1 | 56 | 600063 | 11 | TG1~ AH5! GG~ CS1! |  |
| 2330708 | M_PD_Arkanis_NagaElite_02_V01_Grade1 | 56 | 600063 | 9 | AH2! GG~ |  |
| 2330709 | M_PD_Arkanis_Thalythra_01_Grade1 | 56 | 600063 | 14 | LP3 AH19! GG~ BK300s~ DP2! NC1! |  |
| 2350707 | M_PD_Arkanis_NagaElite_01_V01_001_Grade3 | 60 | 600065 | 11 | TG1~ AH5! GG~ CS1! |  |
| 2350708 | M_PD_Arkanis_NagaElite_02_V01_Grade3 | 60 | 600065 | 9 | AH2! GG~ |  |
| 2350709 | M_PD_Arkanis_Thalythra_01_Grade3 | 60 | 600065 | 14 | LP3 AH19! GG~ BK300s~ DP2! NC1! |  |
| 2353707 | M_PD_Arkanis_NagaElite_01_V01_001_Grade6 | 64 | 600068 | 11 | TG1~ AH5! GG~ CS1! |  |
| 2353708 | M_PD_Arkanis_NagaElite_02_V01_Grade6 | 64 | 600068 | 9 | AH2! GG~ |  |
| 2353709 | M_PD_Arkanis_Thalythra_01_Grade6 | 64 | 600068 | 14 | LP3 AH19! GG~ BK300s~ DP2! NC1! |  |
| 2310810 | M_PD_BakronIsland_Tie_01_001_easy | 54 | 600071 | 10 | AH2! GG~ |  |
| 2310811 | M_PD_BakronIsland_Tamon_01_001_easy | 54 | 600071 | 11 | TG1~ AH1! GG~ |  |
| 2310812 | M_PD_EleKing_01_001_easy | 54 | 600071 | 10 | TG2~ GG~ HP2 DP1! |  |
| 2300810 | M_PD_BakronIsland_Tie_01_001 | 54 | 600072 | 10 | AH2! GG~ |  |
| 2300811 | M_PD_BakronIsland_Tamon_01_001 | 54 | 600072 | 11 | TG1~ AH1! GG~ |  |
| 2300812 | M_PD_EleKing_01_001 | 54 | 600072 | 10 | TG2~ GG~ BK300s~ DP1! |  |
| 2320810 | M_PD_BakronIsland_Tie_01_001_hard | 72 | 600073 | 10 | AH2! GG~ |  |
| 2320811 | M_PD_BakronIsland_Tamon_01_001_hard | 72 | 600073 | 11 | TG1~ AH1! GG~ |  |
| 2320812 | M_PD_EleKing_01_001_hard | 72 | 600073 | 10 | TG2~ GG~ BK300s~ DP1! |  |
| 2310907 | M_PD_FierceHornCave_LycanTotem_01_001_Easy | 63 | 600091 | 1 |  |  |
| 2310908 | M_PD_FierceHornCave_KalnifElite_01_V02_001_Easy | 63 | 600091 | 9 | PT1~ AH10! GG~ |  |
| 2310909 | M_PD_FierceHornCave_Lycan_04_V01_001_Easy | 63 | 600091 | 11 | TG1~ LP1 AH21! GG~ NC2! |  |
| 2310910 | M_PD_FierceHornCave_Ferk_01_V01_Easy | 63 | 600091 | 9 | TG1~ LP2 GG~ HP2 DP4! |  |
| 2300907 | M_PD_FierceHornCave_LycanTotem_01_001 | 63 | 600092 | 1 |  |  |
| 2300908 | M_PD_FierceHornCave_KalnifElite_01_V02_001 | 63 | 600092 | 9 | PT1~ AH11! GG~ |  |
| 2300909 | M_PD_FierceHornCave_Lycan_04_V01_001 | 63 | 600092 | 11 | TG1~ LP1 AH28! GG~ NC2! |  |
| 2300910 | M_PD_FierceHornCave_Ferk_01_V01 | 63 | 600092 | 9 | TG1~ LP2 GG~ BK300s~ DP4! |  |
| 2320907 | M_PD_FierceHornCave_LycanTotem_01_001_hard | 74 | 600093 | 1 |  |  |
| 2320908 | M_PD_FierceHornCave_KalnifElite_01_V02_001_hard | 74 | 600093 | 9 | PT1~ AH11! GG~ |  |
| 2320909 | M_PD_FierceHornCave_Lycan_04_V01_001_hard | 74 | 600093 | 11 | TG1~ LP1 AH28! GG~ NC2! |  |
| 2320910 | M_PD_FierceHornCave_Ferk_01_V01_hard | 74 | 600093 | 9 | TG1~ LP2 GG~ BK300s~ DP4! |  |
| 2301301 | M_PD_CradleNihility_Bakarma_01_V01_001_easy | 70 | 600121 | 13 | TG1~ LP1 AH4! GG~ HP2 NA3~ DP2! |  |
| 2301302 | M_PD_CradleNihility_Radvima_01_V03_001_easy | 70 | 600121 | 12 | TG2~ LP3 GG~ |  |
| 2301303 | M_PD_CradleNihility_BeritraC_08_V01_001_easy | 70 | 600121 | 14 | LP1 AH1! GG~ DP1! |  |
| 2311301 | M_PD_CradleNihility_Bakarma_01_V01_001 | 70 | 600122 | 15 | TG2~ LP1 AH4! GG~ BK300s~ NA3~ DP2! |  |
| 2311302 | M_PD_CradleNihility_Radvima_01_V03_001 | 70 | 600122 | 12 | TG2~ LP3 GG~ |  |
| 2311303 | M_PD_CradleNihility_BeritraC_08_V01_001 | 70 | 600122 | 14 | LP1 AH1! GG~ DP1! |  |
| 2321301 | M_PD_CradleNihility_Bakarma_01_V01_001_hard | 76 | 600123 | 15 | TG2~ LP1 AH4! GG~ BK300s~ NA3~ DP2! |  |
| 2321302 | M_PD_CradleNihility_Radvima_01_V03_001_hard | 76 | 600123 | 12 | TG2~ LP3 GG~ |  |
| 2321303 | M_PD_CradleNihility_BeritraC_08_V01_001_hard | 76 | 600123 | 14 | LP1 AH1! GG~ DP1! |  |
| 2301401 | M_PD_PhantomsHall_Protect_01_V01_001_Easy | 74 | 600131 | 16 | TG4~ LP3 PT1~ AH2! GG~ DP1! NC3! |  |
| 2301402 | M_PD_PhantomsHall_Hiladr_01_V01_001_Easy | 74 | 600131 | 8 | TG1~ LP1 AH2! GG~ DP3! |  |
| 2301403 | M_PD_PhantomsHall__DarkLeda_02_V01_001_Easy | 74 | 600131 | 5 | AH6! GG~ HP2 NC1! |  |
| 2301415 | M_PD_PhantomsHall_Invisible_01_Easy | 74 | 600131 | 0 |  |  |
| 2301416 | M_PD_PhantomsHall_Invisible_02_Easy | 74 | 600131 | 0 |  |  |
| 2311401 | M_PD_PhantomsHall_Protect_01_V01_001 | 74 | 600132 | 16 | TG4~ LP3 PT1~ AH2! GG~ DP1! NC3! |  |
| 2311402 | M_PD_PhantomsHall_Hiladr_01_V01_001 | 74 | 600132 | 8 | TG1~ LP1 AH2! GG~ DP3! |  |
| 2311403 | M_PD_PhantomsHall__DarkLeda_02_V01_001 | 74 | 600132 | 5 | AH6! GG~ BK300s~ NC1! |  |
| 2311416 | M_PD_PhantomsHall_Invisible_01 | 74 | 600132 | 0 |  |  |
| 2311417 | M_PD_PhantomsHall_Invisible_02 | 74 | 600132 | 0 |  |  |
| 2321401 | M_PD_PhantomsHall_Protect_01_V01_001_Hard | 80 | 600133 | 16 | TG4~ LP3 PT1~ AH2! GG~ DP1! NC3! |  |
| 2321402 | M_PD_PhantomsHall_Hiladr_01_V01_001_Hard | 80 | 600133 | 8 | TG1~ LP1 AH2! GG~ DP3! |  |
| 2321403 | M_PD_PhantomsHall__DarkLeda_02_V01_001_Hard | 80 | 600133 | 5 | AH6! GG~ BK300s~ NC1! |  |
| 2321416 | M_PD_PhantomsHall_Invisible_01_Hard | 80 | 600133 | 0 |  |  |
| 2321417 | M_PD_PhantomsHall_Invisible_02_Hard | 80 | 600133 | 0 |  |  |
| 2300188 | M_PD_BlueBreathIsle_Alturadon_01_001_easy | 78 | 600141 | 11 | TG1~ PT1~ AH4! GG~ HP2 NC1! |  |
| 2300189 | M_PD_BlueBreathIsle_Agrint_05_V01_001_easy | 78 | 600141 | 9 | TG2~ AH6! GG~ |  |
| 2300190 | M_PD_BlueBreathIsle_ToxFungus_01_V01_001_easy | 78 | 600141 | 8 | PT1~ AH6! GG~ SB1! NC1! |  |
| 2300200 | M_PD_BlueBreathIsle_Alturadon_01_001 | 78 | 600142 | 11 | TG1~ PT1~ AH4! GG~ BK300s~ NC1! |  |
| 2300219 | M_PD_BlueBreathIsle_Agrint_05_V01_001 | 78 | 600142 | 9 | TG2~ AH6! GG~ |  |
| 2300230 | M_PD_BlueBreathIsle_ToxFungus_01_V01_001 | 78 | 600142 | 8 | PT1~ AH6! GG~ SB1! NC1! |  |
| 2300243 | M_PD_BlueBreathIsle_Alturadon_01_001_hard | 82 | 600143 | 11 | TG1~ PT1~ AH4! GG~ BK300s~ NC1! |  |
| 2300244 | M_PD_BlueBreathIsle_Agrint_05_V01_001_hard | 82 | 600143 | 9 | TG2~ AH6! GG~ |  |
| 2300245 | M_PD_BlueBreathIsle_ToxFungus_01_V01_001_hard | 82 | 600143 | 8 | PT1~ AH6! GG~ SB1! NC1! |  |
| 2301601 | M_PD_DaevaCitadel_MOB_DaevaWar_05_V01_001_Easy | 82 | 600144 | 8 | TG1~ PT1~ AH1! GG~ |  |
| 2301602 | M_PD_DaevaCitadel_MOB_Griosa_01_001_Easy | 82 | 600144 | 8 | LP2 PT1~ GG~ NC1! |  |
| 2301603 | M_PD_DaevaCitadel_MOB_Idris_01_V01_001_Easy | 82 | 600144 | 13 | TG1~ PT1~ AH4! GG~ HP2 CS1! |  |
| 2301701 | M_PD_DaevaCitadel_MOB_DaevaWar_05_V01_001 | 82 | 600145 | 8 | TG1~ PT1~ AH1! GG~ |  |
| 2301702 | M_PD_DaevaCitadel_MOB_Griosa_01_001 | 82 | 600145 | 8 | LP2 PT1~ GG~ NC1! |  |
| 2301703 | M_PD_DaevaCitadel_MOB_Idris_01_V01_001 | 82 | 600145 | 13 | TG1~ PT1~ AH4! GG~ BK300s~ CS1! |  |
| 2301721 | M_PD_DaevaCitadel_MOB_DaevaWar_05_V01_001_Hard | 86 | 600146 | 8 | TG1~ PT1~ AH1! GG~ |  |
| 2301722 | M_PD_DaevaCitadel_MOB_Griosa_01_001_Hard | 86 | 600146 | 8 | LP2 PT1~ GG~ NC1! |  |
| 2301723 | M_PD_DaevaCitadel_MOB_Idris_01_V01_001_Hard | 86 | 600146 | 13 | TG1~ PT1~ AH4! GG~ BK300s~ CS1! |  |
| 2301501 | M_PD_SunkenTemple_Gauss_01_V01_001_Grade1 | 68 | 610010 | 14 | TG1~ PT1~ AH29! GG~ BK300s~ |  |
| 2301502 | M_PD_SunkenTemple_Kalgolem_03_CV02_001_Grade1 | 68 | 610010 | 13 | TG1~ PT1~ AH16! GG~ |  |
| 2301503 | M_PD_SunkenTemple_WaterEle_04_V02_001_Grade1 | 68 | 610010 | 10 | TG1~ LP1 AH2! GG~ DP1! |  |
| 2300000 | M_PD_SunkenTemple_Gauss_01_V01_001_Grade2 | 72 | 610011 | 14 | TG1~ PT1~ AH29! GG~ BK300s~ |  |
| 2300001 | M_PD_SunkenTemple_Kalgolem_03_CV02_001_Grade2 | 72 | 610011 | 13 | TG1~ PT1~ AH16! GG~ |  |
| 2300002 | M_PD_SunkenTemple_WaterEle_04_V02_001_Grade2 | 72 | 610011 | 10 | TG1~ LP1 AH2! GG~ DP1! |  |
| 2300015 | M_PD_SunkenTemple_Gauss_01_V01_001_Grade3 | 76 | 610012 | 14 | TG1~ PT1~ AH29! GG~ BK300s~ |  |
| 2300016 | M_PD_SunkenTemple_Kalgolem_03_CV02_001_Grade3 | 76 | 610012 | 13 | TG1~ PT1~ AH16! GG~ |  |
| 2300017 | M_PD_SunkenTemple_WaterEle_04_V02_001_Grade3 | 76 | 610012 | 10 | TG1~ LP1 AH2! GG~ DP1! |  |
| 2300030 | M_PD_SunkenTemple_Gauss_01_V01_001_Grade4 | 80 | 610013 | 14 | TG1~ PT1~ AH29! GG~ BK300s~ |  |
| 2300031 | M_PD_SunkenTemple_Kalgolem_03_CV02_001_Grade4 | 80 | 610013 | 13 | TG1~ PT1~ AH16! GG~ |  |
| 2300032 | M_PD_SunkenTemple_WaterEle_04_V02_001_Grade4 | 80 | 610013 | 10 | TG1~ LP1 AH2! GG~ DP1! |  |
| 2300258 | M_PD_MirrorHeart_G_01_TrialWall_01_001 | 78 | 610020 | 0 |  |  |
| 2300259 | M_PD_MirrorHeart_G_01_WraPrst_01_V01_001 | 78 | 610020 | 10 | TG1~ LP1 AH2! GG~ |  |
| 2300260 | M_PD_MirrorHeart_G_01_EvSpirit_02_V01_001 | 78 | 610020 | 8 | TG1~ LP1 PT1~ AH5! GG~ |  |
| 2300261 | M_PD_MirrorHeart_G_01_Cromede_01_V02_001 | 78 | 610020 | 0 | GG~ BK300s~ |  |
| 2300364 | M_PD_MirrorHeart_G_01_Cromede_01_V02_002 | 78 | 610020 | 0 | GG~ |  |
| 2300397 | M_PD_MirrorHeart_G_01_Invisible_01 | 78 | 610020 | 0 |  |  |
| 2300276 | M_PD_MirrorHeart_G_02_TrialWall_01_001 | 80 | 610021 | 0 |  |  |
| 2300299 | M_PD_MirrorHeart_G_02_WraPrst_01_V01_001 | 80 | 610021 | 10 | TG1~ LP1 AH2! GG~ |  |
| 2300300 | M_PD_MirrorHeart_G_02_EvSpirit_02_V01_001 | 80 | 610021 | 8 | TG1~ LP1 PT1~ AH5! GG~ |  |
| 2300317 | M_PD_MirrorHeart_G_02_Cromede_01_V02_001 | 80 | 610021 | 0 | GG~ BK300s~ |  |
| 2300365 | M_PD_MirrorHeart_G_02_Cromede_01_V02_002 | 80 | 610021 | 0 | GG~ |  |
| 2300398 | M_PD_MirrorHeart_G_02_Invisible_01 | 80 | 610021 | 0 |  |  |
| 2300333 | M_PD_MirrorHeart_G_03_TrialWall_01_001 | 82 | 610022 | 0 |  |  |
| 2300334 | M_PD_MirrorHeart_G_03_WraPrst_01_V01_001 | 82 | 610022 | 10 | TG1~ LP1 AH2! GG~ |  |
| 2300335 | M_PD_MirrorHeart_G_03_EvSpirit_02_V01_001 | 82 | 610022 | 8 | TG1~ LP1 PT1~ AH5! GG~ |  |
| 2300336 | M_PD_MirrorHeart_G_03_Cromede_01_V02_001 | 82 | 610022 | 0 | GG~ BK300s~ |  |
| 2300367 | M_PD_MirrorHeart_G_03_Cromede_01_V02_002 | 82 | 610022 | 0 | GG~ |  |
| 2300399 | M_PD_MirrorHeart_G_03_Invisible_01 | 82 | 610022 | 0 |  |  |
| 2300352 | M_PD_MirrorHeart_G_04_TrialWall_01_001 | 85 | 610023 | 0 |  |  |
| 2300353 | M_PD_MirrorHeart_G_04_WraPrst_01_V01_001 | 85 | 610023 | 10 | TG1~ LP1 AH2! GG~ |  |
| 2300354 | M_PD_MirrorHeart_G_04_EvSpirit_02_V01_001 | 85 | 610023 | 8 | TG1~ LP1 PT1~ AH5! GG~ |  |
| 2300355 | M_PD_MirrorHeart_G_04_Cromede_01_V02_001 | 85 | 610023 | 0 | GG~ BK300s~ |  |
| 2300368 | M_PD_MirrorHeart_G_04_Cromede_01_V02_002 | 85 | 610023 | 0 | GG~ |  |
| 2300400 | M_PD_MirrorHeart_G_04_Invisible_01 | 85 | 610023 | 0 |  |  |
| 2300153 | M_PD_DDN_Hard_BeritraA_01_V01_001 | 74 | 610044 | 11 | PT1~ AH1! GG~ NC1! |  |
| 2300154 | M_PD_DDN_Hard_BeritraD_01_V01_001 | 74 | 610044 | 10 | TG1~ LP1 AH9! GG~ NC1! |  |
| 2300155 | M_PD_DDN_Hard_Durbati_01 | 74 | 610044 | 8 | TG2~ AH4! GG~ BK300s~ |  |
| 2300162 | M_PD_DDN_Hard_Invisible_Dramata_Event_001 | 74 | 610044 | 0 |  |  |
| 2346601 | M_PD_DeusCenter_Atiel_01_001_Grade9 | 68 | 610052 | 13 | TG1~ LP3 AH2! GG~ BK300s~ DP1! |  |
| 2346615 | M_PD_DeusCenter_Nazmun_01_Grade9 | 68 | 610052 | 9 | TG3~ AH3! GG~ NC1! |  |
| 2346616 | M_PD_DeusCenter_Gelcos_01_Grade9 | 68 | 610052 | 10 | AH1! GG~ |  |
| 2356707 | M_PD_Arkanis_NagaElite_01_V01_001_Grade9 | 68 | 610062 | 11 | TG1~ AH5! GG~ CS1! |  |
| 2356708 | M_PD_Arkanis_NagaElite_02_V01_Grade9 | 68 | 610062 | 9 | AH2! GG~ |  |
| 2356709 | M_PD_Arkanis_Thalythra_01_Grade9 | 68 | 610062 | 14 | LP3 AH19! GG~ BK300s~ DP2! NC1! |  |
| 2300473 | M_PD_AbyssalHornCave_G_01_KalnifElite_01_V02_001 | 80 | 610070 | 9 | PT1~ AH11! GG~ |  |
| 2300474 | M_PD_AbyssalHornCave_G_01_Lycan_04_V01_001 | 80 | 610070 | 11 | TG1~ LP1 AH28! GG~ NC2! |  |
| 2300475 | M_PD_AbyssalHornCave_G_01_Ferk_01_V02_001 | 80 | 610070 | 9 | TG1~ LP2 GG~ BK300s~ DP5! |  |
| 2300491 | M_PD_AbyssalHornCave_G_02_KalnifElite_01_V02_001 | 83 | 610071 | 9 | PT1~ AH11! GG~ |  |
| 2300492 | M_PD_AbyssalHornCave_G_02_Lycan_04_V01_001 | 83 | 610071 | 11 | TG1~ LP1 AH28! GG~ NC2! |  |
| 2300493 | M_PD_AbyssalHornCave_G_02_Ferk_01_V02_001 | 83 | 610071 | 9 | TG1~ LP2 GG~ BK300s~ DP5! |  |
| 2300527 | M_PD_AbyssalHornCave_G_03_KalnifElite_01_V02_001 | 86 | 610072 | 9 | PT1~ AH11! GG~ |  |
| 2300528 | M_PD_AbyssalHornCave_G_03_Lycan_04_V01_001 | 86 | 610072 | 11 | TG1~ LP1 AH28! GG~ NC2! |  |
| 2300529 | M_PD_AbyssalHornCave_G_03_Ferk_01_V02_001 | 86 | 610072 | 9 | TG1~ LP2 GG~ BK300s~ DP5! |  |
| 2300544 | M_PD_AbyssalHornCave_G_04_KalnifElite_01_V02_001 | 90 | 610073 | 9 | PT1~ AH11! GG~ |  |
| 2300545 | M_PD_AbyssalHornCave_G_04_Lycan_04_V01_001 | 90 | 610073 | 11 | TG1~ LP1 AH28! GG~ NC2! |  |
| 2300546 | M_PD_AbyssalHornCave_G_04_Ferk_01_V02_001 | 90 | 610073 | 9 | TG1~ LP2 GG~ BK300s~ DP5! |  |

### Dungeon:Quest (51)

| id | name | lvl | maps | skills | mechanics (S ok, ~ partial, ! missing) | rec |
|---|---|---|---|---|---|---|
| 2700003 | M_L1_CoastalCave_Perion_01 | 12 | 210001 | 12 | TG2~ LP1 GG~ HP5 HL1! NC2! |  |
| 2700009 | M_L1_CoastalCave_PriestPhantom_01 | 12 | 210001 | 10 | TG1~ LP1 AH2! GG~ HP3 NC1! |  |
| 2700265 | M_Dungeon_L_0002_Gartua_01 | 14 | 210002 | 13 | TG2~ LP2 AH23! GG~ HP3 |  |
| 2701399 | M_Dungeon_L_0004_Gauss_01 | 19 | 210004 | 12 | AH10! GG~ HP3 |  |
| 2700089 | Mob_L1_RootCellar_PinoKeeper_01 | 27 | 210006 | 8 | AH1! GG~ HP3 SB1! NC1! |  |
| 2700091 | Mob_L1_RootCellar_Owltros_01 | 27 | 210006 | 7 | TG1~ GG~ HP1 |  |
| 2700092 | Mob_L1_RootCellar_Owltros_02 | 27 | 210006 | 7 | TG1~ LP3 AH4! GG~ HP2 |  |
| 2700552 | M_L1_StrigikAltar_Owlac_01 | 28 | 210007 | 8 | GG~ HP3 |  |
| 2700553 | M_L1_StrigikAltar_Owlapri_01 | 28 | 210007 | 10 | TG1~ LP1 AF2 GG~ HP3 |  |
| 2700746 | M_L1_StrigikAltar_Owlapri_01_Die_001 | 28 | 210007 | 0 | GG~ |  |
| 2700440 | M_L1_RefugeOfIllusion_Leda_03 | 41 | 210008 | 11 | TG2~ LP3 AH1! GG~ HP3 |  |
| 2700374 | M_L1_ZumionRelicDepository_BossRelic_01 | 33 | 210009 | 14 | LP1 AH13! GG~ HP3 NC3! |  |
| 2700415 | M_L1_NahidDisposalPlant_Bargot_01 | 41 | 210010 | 8 | AH1! GG~ HP3 |  |
| 2700461 | M_L1_ArdusShrine_Kaisinel_01 | 43 | 210011 | 9 | LP1 AF1 AH6! GG~ HP3 |  |
| 2700306 | M_L1_IllicitTradeSpot_StrigikDuduri_01 | 23 | 210012 | 6 | TG1~ AH2! GG~ HP3 |  |
| 2701034 | M_Dungeon_L_0016_Boss_01 | 5 | 210016 | 7 | LP2 GG~ HP3 | yes |
| 2701378 | M_Dungeon_L_0017_Lepharist_Boss_01 | 46 | 210017 | 10 | TG1~ LP2 AH2! GG~ HP4 DP1! |  |
| 2701361 | M_Dungeon_L_0018_L_0018_LepharBoss | 49 | 210018 | 10 | LP2 AH1! GG~ HP3 NC2! |  |
| 2701373 | M_Dungeon_L_0019_Paton_01 | 50 | 210019 | 8 | GG~ HP4 |  |
| 2701374 | M_Dungeon_L_0019_Ateron_01 | 50 | 210019 | 8 | TG1~ LP1 AH1! GG~ HP5 |  |
| 2700099 | M_D1_UndergroundGrave_GraveCarasZizel_001 | 14 | 220001 | 9 | TG1~ LP2 GG~ HP3 |  |
| 2700100 | M_D1_UndergroundGrave_GraveCarasHillalion_001 | 14 | 220001 | 9 | LP1 GG~ HP3 NC1! |  |
| 2700691 | M_D1_Dungeon_D_0002_TasinA_01 | 18 | 220002 | 6 | LP1 GG~ HP2 |  |
| 2700692 | M_D1_Dungeon_D_0002_TasinB_01 | 18 | 220002 | 10 | TG1~ LP2 GG~ HP3 |  |
| 2700694 | M_D1_Dungeon_D_0002_Nua_01 | 18 | 220002 | 8 | LP2 HP1 |  |
| 2700695 | M_D1_Dungeon_D_0002_Nua_02 | 18 | 220002 | 8 | LP2 GG~ HP4 |  |
| 2700696 | M_D1_Dungeon_D_0002_Nua_03 | 18 | 220002 | 0 |  |  |
| 2700703 | M_D1_Dungeon_D_0006_DrakanBoss_01 | 23 | 220006 | 10 | TG1~ LP2 GG~ HP5 |  |
| 2700731 | M_D1_Dungeon_D_0007_FafnirBlood | 28 | 220007 | 8 | TG2~ LP1 GG~ HP1 |  |
| 2700715 | M_D1_Dungeon_D_0008_DrakanBoss_01 | 30 | 220008 | 12 | LP1 AH1! GG~ HP4 NC1! |  |
| 2700716 | M_D1_Dungeon_D_0008_WolfBoss_01 | 30 | 220008 | 9 | GG~ HP3 SB1! NC1! |  |
| 2700744 | M_D1_Dungeon_D_0009_FortressKeeper_01 | 32 | 220009 | 9 | AH2! GG~ HP3 |  |
| 2700856 | M_Dungeon_D_0010_Gedor_01 | 35 | 220010 | 2 | GG~ HP1 |  |
| 2700857 | M_Dungeon_D_0010_Ulgorn_01 | 35 | 220010 | 9 | LP1 GG~ HP3 |  |
| 2700908 | M_Dungeon_D_0010_Gedor_02 | 35 | 220010 | 12 | LP1 AH7! GG~ HP3 |  |
| 2700847 | M_Dungeon_D_0011_Zikel_01 | 43 | 220011 | 11 | TG3~ LP2 AH1! GG~ HP4 DP1! |  |
| 2701151 | M_D1_Dungeon_D_0011_Histan_01 | 43 | 220011 | 10 | TG1~ LP1 GG~ HP4 |  |
| 2701078 | M_Dungeon_D_0012_Gartua_01 | 20 | 220012 | 13 | TG2~ LP2 AH23! GG~ HP3 |  |
| 2701401 | M_Dungeon_D_0018_Boss_01 | 5 | 220018 | 7 | LP2 GG~ HP3 |  |
| 2701409 | M_Dungeon_D_0019_DestDaevaBoss | 46 | 220019 | 5 | AH2! GG~ HP3 |  |
| 2701449 | M_Dungeon_D_0020_Boss_01 | 47 | 220020 | 9 | LP1 AH5! GG~ HP3 |  |
| 2701440 | M_Dungeon_D_0021_WindBoss | 48 | 220021 | 9 | LP1 AH3! GG~ HP3 NA1~ |  |
| 2701463 | M_Dungeon_D_0022_Cromede_01_001 | 50 | 220022 | 13 | TG1~ LP5 AH3! GG~ HP3 |  |
| 2200031 | M_L1_FQ_Bandit_03 | 16 | 840003 | 5 | GG~ |  |
| 2500072 | M_D1_FQ_Caras_03 | 30 | 840012 | 3 | GG~ HP1 NA1~ |  |
| 2500086 | M_D1_FQ_Minusha_01_001 | 30 | 840017 | 6 | LP1 AH1! GG~ HP3 NA1~ |  |
| 2500138 | M_D1_FQ_FighterM_01_001 | 45 | 840022 | 5 | GG~ HP1 |  |
| 2200103 | M_L1_FQ_Jumionllusion_01 | 37 | 840023 | 14 | LP1 AH13! GG~ NC3! |  |
| 2200104 | M_L1_FQ_Jumionllusion_02 | 37 | 840023 | 14 | LP1 AH13! GG~ NC3! |  |
| 2500141 | M_D1_FQ_DstrArchonE_01_001 | 45 | 840024 | 6 | LP1 GG~ |  |
| 2200108 | M_L1_FQ_PhantomBoss_01 | 45 | 840025 | 10 | TG1~ LP1 AH2! GG~ HP2 NC1! |  |

### Dungeon:Raid (17)

| id | name | lvl | maps | skills | mechanics (S ok, ~ partial, ! missing) | rec |
|---|---|---|---|---|---|---|
| 2301006 | M_PD_ReforgedAbyss_Lagta_01_001 | 75 | 600082 | 13 | TG2~ PT1~ AH2! GG~ |  |
| 2301007 | M_PD_ReforgedAbyss_Kashapa_01_001 | 75 | 600082 | 7 | LP1 GG~ CS1! |  |
| 2301008 | M_PD_ReforgedAbyss_OrcusGem_01_001 | 75 | 600082 | 9 | TG1~ HP9 NA6~ |  |
| 2301014 | M_PD_ReforgedAbyss_Durbati_01_V01_001 | 75 | 600082 | 1 | GG~ BK600s~ NC1! |  |
| 2301207 | M_PD_ErosionPurifier_FafBlood_02_V02_001_001 | 80 | 620011 | 14 | TG2~ PT1~ GG~ DP2! NC2! |  |
| 2301208 | M_PD_ErosionPurifier_Bargot_01_V02_001_001 | 80 | 620011 | 16 | LP1 AH4! GG~ BK480s~ DP1! |  |
| 2301209 | M_PD_ErosionPurifier_Neuth_03_V01_001_001 | 80 | 620011 | 15 | TG3~ PT1~ AH2! GG~ DP3! NC1! |  |
| 2301216 | M_PD_ErosionPurifier_DracoTurret_L_01_V01_001 | 80 | 620011 | 2 |  |  |
| 2920908 | N_Invisible_Summon_Bargot_01_V02_Press | 10 | 620011 | 1 |  |  |
| 2301059 | M_PD_MusphelHolyGrail_AltKnife_07_V01_01 | 85 | 620021 | 15 | TG4~ LP1 AH15! GG~ NA1~ DP1! |  |
| 2301060 | M_PD_MusphelHolyGrail_Ramu_01_V01_01 | 85 | 620021 | 16 | PT1~ AH47! GG~ BK480s~ NC1! |  |
| 2301061 | M_PD_MusphelHolyGrail_Ramu_01_V01_02 | 85 | 620021 | 16 | PT1~ AH47! NA1~ NC1! |  |
| 2921274 | N_Invisible_Summon_StatueA_02_FloorCheck_001 | 80 | 620021 | 0 |  |  |
| 2301089 | M_PD_MusphelHolyGrail_AltKnife_07_V01_02 | 85 | 620022 | 15 | TG4~ LP1 AH15! GG~ NA1~ DP1! |  |
| 2301090 | M_PD_MusphelHolyGrail_Ramu_01_V01_03 | 85 | 620022 | 16 | PT1~ AH47! GG~ BK480s~ NC1! |  |
| 2301091 | M_PD_MusphelHolyGrail_Ramu_01_V01_04 | 85 | 620022 | 16 | PT1~ AH47! NA1~ NC1! |  |
| 2921315 | N_Invisible_Summon_StatueA_02_FloorCheck_002 | 80 | 620022 | 0 |  |  |

### Dungeon:Seal (60)

| id | name | lvl | maps | skills | mechanics (S ok, ~ partial, ! missing) | rec |
|---|---|---|---|---|---|---|
| 2700916 | M_Seal_Verteron_001_Boss_01 | 10 | 310001 | 5 | GG~ HP2 |  |
| 2700949 | M_Seal_Verteron_007_Agrint_03_01 | 40 | 310007 | 5 | GG~ HP3 |  |
| 2701130 | M_Seal_Verteron_008_SuraWar_01 | 40 | 310008 | 5 | GG~ HP1 NA1~ |  |
| 2701118 | M_Seal_Verteron_009_WraKnt_01 | 30 | 310009 | 6 | GG~ HP3 |  |
| 2700835 | M_Seal_Verteron_013_Boss_01 | 15 | 310013 | 7 | TG1~ LP1 AH3! GG~ HP1 DP1! |  |
| 2701020 | M_Seal_Verteron_014_Neuth_03 | 20 | 310014 | 5 | GG~ HP1 |  |
| 2701072 | M_Seal_Verteron_015_Boss_01 | 30 | 310015 | 6 | LP1 GG~ HP3 |  |
| 2700942 | M_Seal_Verteron_017_Boss_01 | 15 | 310017 | 5 | LP1 AH1! GG~ HP1 |  |
| 2700963 | M_Seal_Verteron_019_Boss_01 | 25 | 310019 | 5 | GG~ HP1 |  |
| 2701142 | M_Seal_Verteron_020_NagaElite_01 | 30 | 310020 | 6 | GG~ HP2 SB1! |  |
| 2700914 | M_Seal_Verteron_024_SuraWar_04_V03_01 | 10 | 310024 | 5 | GG~ HP3 |  |
| 2701148 | M_Seal_Verteron_026_Undead_01_01 | 40 | 310026 | 6 | GG~ HP3 |  |
| 2701145 | M_Seal_Verteron_028_Bug_01 | 25 | 310028 | 5 | TG1~ LP1 GG~ HP1 |  |
| 2701138 | M_Seal_Verteron_029_Keneir_01 | 25 | 310029 | 4 | GG~ HP1 |  |
| 2701101 | M_Seal_Verteron_031_BigMan_01 | 30 | 310031 | 6 | TG1~ GG~ HP1 |  |
| 2700988 | M_Seal_Verteron_042_Boss | 30 | 310042 | 5 | LP1 GG~ HP3 |  |
| 2700975 | M_Seal_Verteron_045_Spirit_03_01 | 15 | 310045 | 6 | GG~ HP1 |  |
| 2701202 | M_Seal_Verteron_046_OwlElite_01 | 25 | 310046 | 5 | GG~ HP1 |  |
| 2701043 | M_Seal_Verteron_048_EliteKrall_01 | 30 | 310048 | 7 | GG~ HP1 AT1! |  |
| 2701184 | M_Seal_Verteron_049_Dratona_04 | 35 | 310049 | 5 | LP1 AH1! GG~ HP1 |  |
| 2701501 | M_Seal_Verteron_050_DracoElite_01_V01_01 | 35 | 310050 | 7 | AH1! GG~ HP3 |  |
| 2701096 | M_Seal_Verteron_051_Boss_01 | 30 | 310051 | 6 | AH1! GG~ HP4 NC2! |  |
| 2701055 | M_Seal_Verteron_054_Boss_01 | 20 | 310054 | 7 | LP1 GG~ HP3 |  |
| 2701521 | M_Seal_Verteron_055_DracoWar_01 | 35 | 310055 | 5 | GG~ HP2 |  |
| 2701544 | M_Seal_Verteron_057_BeritraD_01 | 40 | 310057 | 6 | LP1 GG~ HP1 |  |
| 2702232 | M_Seal_Light_066_FighterM_01_V03_001 | 45 | 310066 | 5 | GG~ HP1 |  |
| 2702326 | M_Seal_Light_067_Boss_01 | 45 | 310067 | 6 | GG~ HP1 |  |
| 2702564 | M_Seal_Light_068_Boss_01 | 45 | 310068 | 5 | GG~ HP1 |  |
| 2702241 | M_Seal_Light_069_GraveUn_02_001 | 45 | 310069 | 5 | GG~ HP1 |  |
| 2702329 | M_Seal_Light_070_GraveGh_02_001 | 45 | 310070 | 6 | AH1! GG~ HP3 NC2! |  |
| 2700952 | M_Seal_Altgard_001_NornirBoss_01 | 20 | 320001 | 5 | TG1~ LP1 GG~ HP3 |  |
| 2700921 | M_Seal_Altgard_002_FightBoss_01 | 10 | 320002 | 5 | GG~ HP3 |  |
| 2701035 | M_Seal_Altgard_003_Boss_01 | 10 | 320003 | 5 | GG~ HP3 |  |
| 2700876 | M_Seal_Altgard_004_DratonaBoss_01 | 10 | 320004 | 5 | LP1 AH1! GG~ HP3 |  |
| 2701227 | M_Seal_Altgard_006_Boss_01 | 10 | 320006 | 5 | GG~ HP1 |  |
| 2700981 | M_Seal_Altgard_009_Boss_01 | 10 | 320009 | 6 | GG~ HP1 |  |
| 2701250 | M_Seal_Altgard_015_DaevaWar_01 | 15 | 320015 | 8 | LP1 GG~ HP1 |  |
| 2700886 | M_Seal_Altgard_018_DrakanBoss_01 | 15 | 320018 | 7 | GG~ HP1 |  |
| 2701013 | M_Seal_Altgard_023_Boss_01 | 10 | 320023 | 6 | GG~ HP3 |  |
| 2701568 | M_Seal_Altgard_024_DrakanElite_01 | 15 | 320024 | 5 | TG1~ GG~ HP1 SB1! HL1! |  |
| 2701280 | M_Seal_Altgard_025_Boss_01 | 40 | 320025 | 7 | TG2~ LP1 GG~ HP4 |  |
| 2701298 | M_Seal_Altgard_026_EliteWraKnt_01 | 15 | 320026 | 6 | GG~ HP3 |  |
| 2701551 | M_Seal_Altgard_027_DrakanElite_03_V01_01 | 25 | 320027 | 7 | LP1 GG~ HP3 |  |
| 2701287 | M_Seal_Altgard_030_Boss_01 | 30 | 320030 | 5 | TG1~ GG~ HP1 |  |
| 2701310 | M_Seal_Altgard_035_EliteWaterEle_01 | 20 | 320035 | 5 | LP1 GG~ HP3 |  |
| 2701275 | M_Seal_Altgard_036_BeritraD_03_01 | 20 | 320036 | 6 | LP1 GG~ HP1 |  |
| 2701454 | M_Seal_Altgard_039_NagaElite_04 | 35 | 320039 | 6 | GG~ HP3 SB1! |  |
| 2701480 | M_Seal_Altgard_049_GraveGh_02_001 | 35 | 320049 | 6 | AH1! GG~ HP3 NC2! |  |
| 2701485 | M_Seal_Altgard_052_AltKnife_07_01 | 35 | 320052 | 7 | LP2 GG~ HP3 |  |
| 2701578 | M_Seal_Altgard_053_AddShulack_05_01 | 35 | 320053 | 6 | GG~ HP3 |  |
| 2701525 | M_Seal_Altgard_055_AltKnife_04_V02_01 | 40 | 320055 | 6 | GG~ HP3 SB1! |  |
| 2701569 | M_Seal_Altgard_056_BeritraD_01 | 40 | 320056 | 6 | LP1 GG~ HP1 |  |
| 2702133 | M_Seal_Dark_066_FireEle_04_001 | 45 | 320066 | 8 | LP1 GG~ HP1 |  |
| 2702076 | M_Seal_Dark_067_WaterEle_03_001 | 45 | 320067 | 5 | LP1 GG~ HP3 |  |
| 2702572 | M_Seal_Dark_068_KalnifElite_01 | 45 | 320068 | 6 | GG~ HP1 |  |
| 2702574 | M_Seal_Dark_069_SpiritKnt_02_001 | 45 | 320069 | 5 | LP1 GG~ |  |
| 2702177 | M_Seal_Dark_070_Boss_01 | 45 | 320070 | 5 | TG1~ GG~ HP1 |  |
| 2700837 | M_SealAdvanced_0001_EliteA_01 | 45 | 400001,400002 | 7 | TG1~ LP1 AH3! GG~ HP2 DP1! |  |
| 2700840 | M_SealAdvanced_0001_EliteD_01 | 45 | 400001,400002 | 6 | GG~ HP2 |  |
| 2700845 | M_SealAdvanced_0001_Boss_01 | 45 | 400001,400002 | 9 | AH2! GG~ HP6 DP1! |  |

### FieldBoss(WorldMap) (85)

| id | name | lvl | maps | skills | mechanics (S ok, ~ partial, ! missing) | rec |
|---|---|---|---|---|---|---|
| 2600068 | M_AR1_WSA_EleKing_01_V02_001 | 65 | 20 | 9 | TG3~ AH3! |  |
| 2600084 | M_AR1_RR_AbBoss_01_001 | 65 | 20 | 12 | TG1~ AH5! |  |
| 2600089 | M_AR1_BeritraD_01_001 | 65 | 20 | 8 | TG2~ LP1 |  |
| 2600093 | M_AR1_RR_AbBoss_01_002 | 65 | 20 | 12 | TG1~ AH5! |  |
| 2600094 | M_AR1_RR_AbBoss_01_003 | 65 | 20 | 12 | TG1~ AH5! |  |
| 2600096 | M_AR1_Radvima_01_CV02_001 | 65 | 20 | 8 | AH9! |  |
| 2600097 | M_AR1_WSA_EleKing_01_V02_002 | 65 | 20 | 9 | TG3~ AH3! |  |
| 2600098 | M_AR1_BeritraD_01_002 | 65 | 20 | 8 | TG2~ LP1 |  |
| 2600479 | M_AR3_AbBoss_01_002 | 80 | 22 | 12 | TG1~ AH5! |  |
| 2600480 | M_AR3_AbBoss_01_003 | 80 | 22 | 12 | TG1~ AH5! |  |
| 2600520 | M_AR3_Kashapa_01_CV02_001 | 80 | 22 | 11 | LP1 CS1! NC1! |  |
| 2600521 | M_AR3_Evspirit_02_CV02_001 | 80 | 22 | 8 | LP1 |  |
| 2600522 | M_AR3_Nahid_01_CV02_001 | 80 | 22 | 12 | TG1~ LP1 PT1~ |  |
| 2100003 | M_L1_CV_Keneir_Named_02 | 45 | 1010 | 4 |  |  |
| 2100040 | M_L1_ER_SwampMan02_Named_01 | 45 | 1010 | 6 | TG1~ |  |
| 2100050 | M_L1_CV_Keneir_Named_01 | 45 | 1010 | 4 |  |  |
| 2100076 | M_L1_VF_KrallElite_Named_01 | 45 | 1010 | 7 | AT1! |  |
| 2100077 | M_L1_VF_KrallElite_Named_02 | 45 | 1010 | 5 |  |  |
| 2100079 | M_L1_VF_Neuth_01_Named_01 | 45 | 1010 | 5 |  |  |
| 2100141 | M_L1_BF_Coradon_Named_01 | 45 | 1010 | 5 | TG1~ |  |
| 2100177 | M_L1_TF_Ursuss_Named_01 | 45 | 1010 | 7 | TG1~ SB1! |  |
| 2100178 | M_L1_TF_Kalnif_Named_01 | 45 | 1010 | 6 |  |  |
| 2100582 | M_L1_AH_A_SuraMag_01 | 45 | 1010 | 5 | SB1! |  |
| 2100617 | M_L1_AC_WaterEle_04_Named_01 | 45 | 1010 | 6 | LP1 AH1! |  |
| 2100661 | M_L1_RI_Kasia_01_Named_01 | 45 | 1010 | 8 | LP2 NC1! |  |
| 2100708 | M_L1_AH_B_SpiritKnt_Named | 45 | 1010 | 6 |  |  |
| 2100718 | M_L1_AH_C_Guardit_Named | 45 | 1010 | 8 | TG1~ SB1! NC1! |  |
| 2100876 | M_L1_DP_DrakanElite_03_Named | 45 | 1010 | 7 | LP1 |  |
| 2100877 | M_L1_DP_BeritraD_04_Named | 45 | 1010 | 7 |  |  |
| 2100988 | M_L1_OV_A_OwlMagic_01_Named | 45 | 1010 | 5 | AH3! |  |
| 2100989 | M_L1_OV_A_OwlElite_01_Named | 45 | 1010 | 5 |  |  |
| 2100991 | M_L1_OV_C_OwlElite_01_CV01_Named | 45 | 1010 | 5 |  |  |
| 2101016 | M_L1_Nahid_C_BeritraB_01_002 | 45 | 1010 | 6 | LP1 |  |
| 2101074 | M_FE_IoE_OdiumGartua_01_001 | 51 | 1010 | 9 | TG1~ LP2 AH2! DP1! |  |
| 2101120 | M_L1_FieldBoss_Dragon_01_1 | 48 | 1010 | 7 | TG2~ LP1 |  |
| 2101122 | M_L1_FieldBoss_Kashapa_01_1 | 48 | 1010 | 11 | LP1 CS1! NC1! |  |
| 2101131 | M_L1_FieldBoss_Ragta_01_1 | 48 | 1010 | 14 | TG2~ LP1 |  |
| 2101217 | M_L2_Named_RH_Bug_02_V01 | 80 | 1011 | 5 | TG1~ LP1 |  |
| 2101218 | M_L2_Named_RH_FighterM_04_V01 | 80 | 1011 | 6 |  |  |
| 2101257 | M_L2_Named_EH_DstrArchonE_01_CV01 | 80 | 1011 | 6 | LP1 |  |
| 2101278 | M_L2_Named_EOR_PretorBoss_01 | 80 | 1011 | 5 |  |  |
| 2101279 | M_L2_Named_EOR_KalnifElite_01_V03 | 80 | 1011 | 6 |  |  |
| 2101306 | M_L2_Named_AMS_Agrint_04 | 80 | 1011 | 5 | CS1! |  |
| 2101349 | M_L2_CL_Named_Nihogg_01 | 80 | 1011 | 5 |  |  |
| 2101350 | M_L2_CL_Named_Ktima_01 | 80 | 1011 | 5 |  |  |
| 2101415 | M_L2_Named_DP_Basilisk_02_V01 | 80 | 1011 | 5 |  |  |
| 2101416 | M_L2_Named_DP_Ramfu_04_001 | 80 | 1011 | 5 | LP1 |  |
| 2101600 | M_L2_FieldBoss_BeritraD_01_1 | 80 | 1011 | 7 | TG2~ LP1 |  |
| 2101601 | M_L2_FieldBoss_Kashapa_01_1 | 80 | 1011 | 11 | LP1 CS1! NC1! |  |
| 2400017 | M_D1_DC_DranaVar_EL_01 | 45 | 1110 | 5 |  | yes |
| 2400074 | M_D1_UC_SuraWar_05_01 | 45 | 1110 | 5 |  |  |
| 2400140 | M_D1_SW_DracoWar_01_V02_01 | 45 | 1110 | 5 |  | yes |
| 2400141 | M_D1_SW_Varg_03_01 | 45 | 1110 | 5 | SB1! |  |
| 2400212 | M_D1_MF_UrsussElite_01_001 | 45 | 1110 | 7 | TG1~ SB1! |  |
| 2400223 | M_D1_MF_DranaVar_02_001 | 45 | 1110 | 6 | SB1! |  |
| 2400274 | M_D1_SH_NornirVar_02_01 | 45 | 1110 | 5 | TG1~ LP1 |  |
| 2400335 | M_D1_PF_Keneir_Elite_01_01 | 45 | 1110 | 4 |  |  |
| 2400353 | M_D1_DR_SkurvWar_04_001 | 45 | 1110 | 5 |  |  |
| 2400358 | M_D1_DR_DrakanElite_03_001 | 45 | 1110 | 7 | LP1 |  |
| 2400419 | M_D1_BR_FighterM_01_001 | 45 | 1110 | 5 |  |  |
| 2400424 | M_D1_BR_AltKnife_08_001 | 45 | 1110 | 7 | LP1 SB1! |  |
| 2400425 | M_D1_BR_DstrArchonE_01_001 | 45 | 1110 | 6 | LP1 |  |
| 2400474 | M_D1_FD_Shulack_05_01 | 45 | 1110 | 6 |  |  |
| 2400504 | M_D1_WGC_AltKnife_04_01 | 45 | 1110 | 7 | LP2 |  |
| 2400593 | M_D1_EGC_BeritraD_04_001 | 45 | 1110 | 7 |  |  |
| 2400607 | M_D1_BCV_KrallElite_01_01 | 45 | 1110 | 6 |  |  |
| 2400608 | M_D1_BCV_KrallElite_02_01 | 45 | 1110 | 7 | TG2~ LP1 |  |
| 2400659 | M_D1_LF_Sarca_Elite_01_01 | 45 | 1110 | 6 | LP1 |  |
| 2400709 | M_D1_IS_DracoElite_01_001 | 45 | 1110 | 7 | AH1! |  |
| 2400800 | M_D1_IoI_OdiumGartua_01_001 | 51 | 1110 | 9 | TG1~ LP2 AH2! DP1! |  |
| 2400853 | M_D1_FieldBoss_Lagta_02 | 48 | 1110 | 14 | TG2~ LP1 |  |
| 2400854 | M_D1_FieldBoss_Kashapa_02 | 48 | 1110 | 11 | LP1 CS1! NC1! |  |
| 2400855 | M_D1_FieldBoss_BeritraD_01_001 | 48 | 1110 | 8 | TG2~ LP1 |  |
| 2406034 | M_D2_PF_Pargon_01_001_01 | 80 | 1111 | 6 |  |  |
| 2406035 | M_D2_PF_Dratona_03_CV01_001_01 | 80 | 1111 | 6 | LP1 AH1! |  |
| 2406071 | M_D2_NS_Kalgolem_04_001 | 80 | 1111 | 6 |  |  |
| 2406093 | M_D2_NS_DstrArchonE_01_V02_01 | 80 | 1111 | 6 | LP1 |  |
| 2406094 | M_D2_NS_WaterEle_04_V01_01 | 80 | 1111 | 6 | LP1 SB1! |  |
| 2406129 | M_D2_AH_Fieoce_01_001_01 | 80 | 1111 | 7 | TG1~ |  |
| 2406131 | M_D2_AH_KrallElite_01_CV01_01 | 80 | 1111 | 7 | AT1! |  |
| 2406132 | M_D2_AH_DestDaeva_06_001_01 | 80 | 1111 | 6 | LP1 |  |
| 2406181 | M_D2_MV_FireEle_04_V01_01 | 80 | 1111 | 8 | LP1 |  |
| 2406182 | M_D2_MV_Kalgolem_03_CV01_01 | 80 | 1111 | 6 | TG1~ |  |
| 2406990 | M_D2_FieldBoss_BeritraD_01_001 | 80 | 1111 | 8 | TG2~ LP1 |  |
| 2406991 | M_D2_FieldBoss_Kashapa_02_001 | 80 | 1111 | 11 | LP1 CS1! NC1! |  |

### FieldNamed (6)

| id | name | lvl | maps | skills | mechanics (S ok, ~ partial, ! missing) | rec |
|---|---|---|---|---|---|---|
| 2600078 | M_AR1_RR_OrcusGem_01_001 | 65 | 20 | 9 | TG1~ |  |
| 2600080 | M_AR1_STI_OrcusGem_01_001 | 65 | 20 | 9 | TG1~ |  |
| 2600082 | M_AR1_WSA_OrcusGem_01_001 | 65 | 20 | 9 | TG1~ |  |
| 2600516 | M_AR3_RR_OrcusGem_01_001 | 80 | 22 | 9 | TG1~ |  |
| 2600517 | M_AR3_STI_OrcusGem_01_001 | 80 | 22 | 9 | TG1~ |  |
| 2600518 | M_AR3_WSA_OrcusGem_01_001 | 80 | 22 | 9 | TG1~ |  |

### InstanceLayer (108)

| id | name | lvl | maps | skills | mechanics (S ok, ~ partial, ! missing) | rec |
|---|---|---|---|---|---|---|
| 2701090 | M_L_Starter_InstanceLayer_1_Bargot_01 | 1 | 100001 | 5 |  | yes |
| 2701128 | M_L_Starter_InstanceLayer_2_DrakanElite_01 | 9 | 100002 | 7 | PT1~ GG~ HP1 | yes |
| 2290156 | M_L1_TF_UrsussElite_01 | 25 | 101012 | 7 | TG1~ GG~ SB1! |  |
| 2290158 | M_L1_OV_Ins_Owlamus_01 | 30 | 101016 | 6 | TG1~ LP1 AF1 GG~ HP4 NC1! |  |
| 2200146 | M_L1_FQ_BigBug_01 | 45 | 101021 | 5 | TG1~ LP1 GG~ HP3 |  |
| 2701388 | M_D_Starter_Layer_1_Rakut_01 | 1 | 110001 | 5 | HP2 |  |
| 2590120 | M_D_Starter_HQ001_Elvida_V01_01 | 3 | 110002 | 4 | HP2 NA1~ |  |
| 2701492 | M_D_Starter_Layer_3_Pelleir_01 | 9 | 110006 | 5 | AH4! GG~ HP1 |  |
| 2345107 | M_D1_FE_SA_Boss_001 | 20 | 111002,111003,510015 | 13 | TG2~ LP2 AH23! HP8 |  |
| 2590118 | M_D1_SWIns_DracoWar_01_V02_01 | 19 | 111008 | 7 | TG1~ LP1 AH1! GG~ |  |
| 2590134 | M_D1_DR_Ins_SkurvWar_04_V01 | 32 | 111009 | 5 | GG~ |  |
| 2590089 | M_D1_HQ_Lycan_05_01 | 42 | 111038 | 6 | GG~ HP3 NA1~ |  |
| 2404001 | M_D1_DH_BeritraD_03_01 | 45 | 141000 | 6 | LP1 GG~ HP1 |  |
| 2404002 | M_D1_DH_BeritraC_09_02 | 45 | 141001 | 6 | LP1 AH1! GG~ HP1 |  |
| 2404003 | M_D1_DH_BeritraC_08_03 | 45 | 141002 | 6 | GG~ HP1 |  |
| 2404004 | M_D1_DH_BeritraD_03_04 | 45 | 141003 | 6 | LP1 GG~ HP1 |  |
| 2404005 | M_D1_DH_BeritraC_09_05 | 45 | 141004 | 6 | LP1 AH1! GG~ HP1 |  |
| 2404006 | M_D1_DH_BeritraC_08_06 | 46 | 141005 | 6 | GG~ HP1 |  |
| 2404007 | M_D1_DH_BeritraD_03_07 | 46 | 141006 | 6 | LP1 GG~ HP1 |  |
| 2404008 | M_D1_DH_BeritraC_09_08 | 46 | 141007 | 6 | LP1 AH1! GG~ HP1 |  |
| 2404009 | M_D1_DH_BeritraC_08_09 | 46 | 141008 | 6 | GG~ HP1 |  |
| 2404010 | M_D1_DH_BeritraD_03_10 | 46 | 141009 | 6 | LP1 GG~ HP1 |  |
| 2404011 | M_D1_DH_BeritraC_09_11 | 47 | 141010 | 6 | LP1 AH1! GG~ HP1 |  |
| 2404012 | M_D1_DH_BeritraC_08_12 | 47 | 141011 | 6 | GG~ HP1 |  |
| 2404013 | M_D1_DH_BeritraD_03_13 | 47 | 141012 | 6 | LP1 GG~ HP1 |  |
| 2404014 | M_D1_DH_BeritraC_09_14 | 47 | 141013 | 6 | LP1 AH1! GG~ HP1 |  |
| 2404015 | M_D1_DH_BeritraC_08_15 | 47 | 141014 | 6 | GG~ HP1 |  |
| 2407059 | M_D2_DH_BeritraD_03_01 | 50 | 141015 | 6 | LP1 GG~ HP1 |  |
| 2407060 | M_D2_DH_BeritraC_09_02 | 50 | 141016 | 6 | LP1 AH1! GG~ HP1 |  |
| 2407061 | M_D2_DH_BeritraC_08_03 | 50 | 141017 | 6 | GG~ HP1 |  |
| 2407062 | M_D2_DH_BeritraD_03_04 | 50 | 141018 | 6 | LP1 GG~ HP1 |  |
| 2407063 | M_D2_DH_BeritraC_09_05 | 50 | 141019 | 6 | LP1 AH1! GG~ HP1 |  |
| 2407064 | M_D2_DH_BeritraC_08_06 | 50 | 141020 | 6 | GG~ HP1 |  |
| 2407065 | M_D2_DH_BeritraD_03_07 | 50 | 141021 | 6 | LP1 GG~ HP1 |  |
| 2405023 | M_D1_Daeva01_DaevaWar_01_01 | 13 | 142000 | 8 | LP1 GG~ HP1 |  |
| 2405045 | M_D1_Dragon01_BeritraD_03_01 | 18 | 142001 | 9 | AH1! GG~ HP1 SB1! |  |
| 2405006 | M_D1_human01_FighterM_01_01 | 12 | 142002 | 6 | GG~ HP1 |  |
| 2405072 | M_D1_MF_DG_DaevaWar_01_005 | 21 | 142003 | 8 | LP1 GG~ HP1 |  |
| 2405106 | M_D1_MF_DG_DaevaWar_01_004 | 21 | 142004 | 6 | GG~ HP1 SB1! |  |
| 2405085 | M_D1_MF_HG_FighterM_01_01 | 21 | 142005 | 5 | GG~ HP1 |  |
| 2405120 | M_D1_Human03_FighterM_01_01 | 26 | 142006 | 5 | GG~ HP1 |  |
| 2405144 | M_D1_Daeva04_EvSpirit_02_01 | 10 | 142014 | 6 | AH1! GG~ HP1 NC2! |  |
| 2405150 | M_D1_Daeva05_DaevaWar_01_01 | 30 | 142015 | 8 | LP1 GG~ HP1 |  |
| 2405151 | M_D1_Daeva05_DaevaWar_01_02 | 30 | 142015 | 8 | LP1 GG~ HP1 |  |
| 2405152 | M_D1_Daeva05_DaevaWar_01_03 | 30 | 142015 | 8 | LP1 GG~ HP1 |  |
| 2405215 | M_D1_Human04_FighterM_01_01 | 37 | 142016 | 6 | GG~ HP1 |  |
| 2405194 | M_D1_Dragon02_BeritraC_09_01 | 28 | 142017 | 6 | LP1 AH1! GG~ HP1 |  |
| 2405175 | M_D1_Human05_FighterM_01_01 | 40 | 142018 | 5 | GG~ HP1 |  |
| 2405236 | M_D1_Dragon03_BeritraD_03_01 | 40 | 142019 | 9 | AH1! GG~ HP1 SB1! |  |
| 2405276 | M_D1_Dragon04_BeritraD_03_01 | 44 | 142021 | 9 | AH1! GG~ HP1 SB1! |  |
| 2407000 | M_D2_H1_Garrison_SpiritKnt_02_01 | 46 | 142100 | 5 | LP1 GG~ HP1 |  |
| 2407023 | M_D2_Daeva01_SuraWar_05_V01_001_01 | 47 | 142104 | 5 | GG~ HP1 |  |
| 2407075 | M_D2_R2_Garrison_SuraPriest_01 | 47 | 142105 | 5 | GG~ HP1 SB1! |  |
| 2407046 | M_D2_D1_Garrison_BeritraC_09_01 | 47 | 142108 | 6 | LP1 AH1! GG~ HP1 |  |
| 2407094 | M_D2_D4_Garrison_DrakanBoss_01 | 47 | 142111 | 9 | AH1! GG~ HP1 SB1! |  |
| 2101104 | M_L1_DH_1_MOB_BeritraD_03 | 45 | 151001 | 6 | LP1 GG~ HP1 |  |
| 2101105 | M_L1_DH_2_MOB_BeritraC_09 | 46 | 151002 | 6 | LP1 AH1! GG~ HP1 |  |
| 2101106 | M_L1_DH_3_MOB_BeritraC_08 | 46 | 151003 | 6 | GG~ HP1 |  |
| 2101107 | M_L1_DH_4_MOB_BeritraD_03 | 45 | 151004 | 6 | LP1 GG~ HP1 |  |
| 2101108 | M_L1_DH_5_MOB_BeritraC_09 | 46 | 151005 | 6 | LP1 AH1! GG~ HP1 |  |
| 2101109 | M_L1_DH_6_MOB_BeritraC_08 | 47 | 151006 | 6 | GG~ HP1 |  |
| 2101110 | M_L1_DH_7_MOB_BeritraD_03 | 45 | 151007 | 6 | LP1 GG~ HP1 |  |
| 2101111 | M_L1_DH_8_MOB_BeritraC_09 | 47 | 151008 | 6 | LP1 AH1! GG~ HP1 |  |
| 2101112 | M_L1_DH_9_MOB_BeritraC_08 | 47 | 151009 | 6 | GG~ HP1 |  |
| 2101113 | M_L1_DH_10_MOB_BeritraD_03 | 46 | 151010 | 6 | LP1 GG~ HP1 |  |
| 2101114 | M_L1_DH_11_MOB_BeritraC_09 | 46 | 151011 | 6 | LP1 AH1! GG~ HP1 |  |
| 2101115 | M_L1_DH_12_MOB_BeritraC_08 | 45 | 151012 | 6 | GG~ HP1 |  |
| 2101116 | M_L1_DH_13_MOB_BeritraD_03 | 45 | 151013 | 6 | LP1 GG~ HP1 |  |
| 2101117 | M_L1_DH_14_MOB_BeritraC_09 | 47 | 151014 | 6 | LP1 AH1! GG~ HP1 |  |
| 2101118 | M_L1_DH_15_MOB_BeritraC_08 | 47 | 151015 | 6 | GG~ HP1 |  |
| 2110515 | M_L2_DH_1_MOB_BeritraD_03 | 50 | 151016 | 6 | LP1 GG~ HP1 |  |
| 2110516 | M_L2_DH_2_MOB_BeritraC_09 | 50 | 151017 | 6 | LP1 AH1! GG~ HP1 |  |
| 2110517 | M_L2_DH_3_MOB_BeritraC_08 | 50 | 151018 | 6 | GG~ HP1 |  |
| 2110518 | M_L2_DH_4_MOB_BeritraD_03 | 50 | 151019 | 6 | LP1 GG~ HP1 |  |
| 2110519 | M_L2_DH_5_MOB_BeritraC_09 | 50 | 151020 | 6 | LP1 AH1! GG~ HP1 |  |
| 2110520 | M_L2_DH_6_MOB_BeritraC_08 | 50 | 151021 | 6 | GG~ HP1 |  |
| 2110521 | M_L2_DH_7_MOB_BeritraD_03 | 50 | 151022 | 6 | LP1 GG~ HP1 |  |
| 2110000 | M_L1_GT_R1_DaevaElite_01 | 27 | 152000 | 8 | LP1 GG~ HP1 |  |
| 2110016 | M_L1_GT_R2_DaevaElite_01 | 39 | 152001 | 8 | LP1 GG~ |  |
| 2110300 | M_L1_GT_R3_DaevaElite_01 | 33 | 152002 | 8 | LP1 GG~ HP1 |  |
| 2110311 | M_L1_GT_R4_DaevaElite_01 | 33 | 152003 | 8 | LP1 GG~ HP1 |  |
| 2110350 | M_L1_CT_R5_DaevaElite_01 | 34 | 152005 | 6 | LP1 GG~ HP1 |  |
| 2110600 | M_L2_GT_R1_Lepharist_M_03 | 47 | 152011 | 5 | GG~ HP1 |  |
| 2110360 | M_L1_CT_H1_PlundererElite_01 | 12 | 153001 | 6 | GG~ HP1 |  |
| 2110370 | M_L1_CT_H2_PlundererElite_01 | 18 | 153002 | 5 | GG~ HP1 |  |
| 2110377 | M_L1_CT_H2_PlundererElite_02 | 18 | 153002 | 5 | GG~ |  |
| 2110380 | M_L1_CT_H3_PlundererElite_01 | 25 | 153003 | 6 | GG~ HP1 |  |
| 2110390 | M_L1_CT_H4_PlundererElite_01 | 27 | 153004 | 5 | GG~ HP1 |  |
| 2110399 | M_L1_CT_H5_PlundererElite_01 | 33 | 153005 | 6 | GG~ |  |
| 2110470 | M_L2_H1_PlundererElite_01 | 46 | 153011 | 5 | GG~ HP1 |  |
| 2110465 | M_L2_CT_H2_INS_PlundererElite_01 | 49 | 153012 | 6 | GG~ HP1 |  |
| 2110437 | M_L1_DT_D1_INS_DraKanElite_01 | 18 | 154001 | 9 | AH1! GG~ HP1 SB1! |  |
| 2110410 | M_L1_DT_D2_INS_DraKanElite_01 | 39 | 154002 | 9 | AH1! GG~ HP1 SB1! |  |
| 2110443 | M_L1_DT_D3_INS_DraKanElite_01 | 41 | 154003 | 9 | AH1! GG~ HP1 SB1! |  |
| 2110453 | M_L1_DT_D5_INS_DraKanElite_01 | 41 | 154005 | 9 | AH1! GG~ HP1 SB1! |  |
| 2110619 | M_L2_DT_D1_INS_DraKanElite_01 | 49 | 154011 | 6 | LP1 AH1! GG~ |  |
| 2110620 | M_L2_DT_D1_INS_DraKanElite_01_2 | 49 | 154011 | 6 | LP1 AH1! GG~ HP1 |  |
| 2340158 | M_FE_Beritra_BeritraD_01_001 | 16 | 500001,500017,500018 | 7 | TG2~ LP1 HP24 |  |
| 2340255 | M_FE_BS_01_EvSpirit_002_01 | 20 | 500002,500019,500020 | 8 | TG1~ LP1 HP24 |  |
| 2340057 | M_L1_FE_SA_Boss_001 | 20 | 500005,500006,500021 | 13 | TG2~ LP2 AH23! HP8 |  |
| 2200060 | M_L1_FQ_OwlTrampBoss_01 | 30 | 800012 | 8 | GG~ |  |
| 2200101 | M_L1_Q_Guardit_02 | 37 | 800014 | 8 | TG1~ GG~ HP1 SB1! NC1! |  |
| 2500016 | M_D1_FQ_CorpeSpider_01 | 16 | 850001 | 5 | GG~ | yes |
| 2500045 | M_D1_FQ_AltKnife_04_002 | 22 | 850006 | 6 | GG~ SB1! |  |
| 2500119 | M_D1_FQ_FighterM_01_V03_001 | 45 | 850009 | 5 | GG~ |  |
| 2500120 | M_D1_FQ_KalnifElite_01_001 | 45 | 850009 | 6 | GG~ |  |
| 2500131 | M_D1_FQ_Lycan_04_001 | 45 | 850010 | 7 | TG2~ LP1 GG~ |  |
| 2500107 | M_D1_FQ_AltKnife_04_003 | 37 | 850011 | 6 | GG~ SB1! |  |
