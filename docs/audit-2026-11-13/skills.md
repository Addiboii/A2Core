# Skills and abnormals audit, 2026-11-13 (read-only)

Repo: `G:\A2Core-src\editing` @ `601e9e4` (branch upgrade/stage1-step10). Client tables: `tables-20261109-023531`.
Official recordings: 20 `timeline-13328` folders on the Windows PC (34,543 player casts 0x3802 counted).
Scripts and raw output (Windows PC scratchpad): `schema.py, enum.py, types.py, grepT.py, cover.py, mixed.py, items.py, casts.py`, `cover.json` (per-skill verdicts).

## How this was measured

* **Player skill closure**: SkillAcquireData skills of the 8 server classes (Fighter excluded: no server kit), plus their
  specialisation children (SpecializedSkill), charge levels (SkillCharge), chain follow-ups (ChainSkillPrevSkillId),
  SkillTransform, SkillLink and skills named in effect values. **4,786 skills, 573 abnormals** (Fighter adds 1,129).
* **Skill book**: the SkillAcquireData base skills per class (38-44, of which 13 are Stigma).
* **Server status**: every effect/abnormal type name was looked up in `server/Aion.World/*.cs` (string literal), then
  the cast path was read: `GameSession.cs CastReplies (2862) -> SelfBuffs / CombatReplies (3045) -> SkillHits / TargetDebuffs /
  GuideDebuff / GuideDot / GuideCrowdControl / GearProcs`, `GameSession.Buffs.cs AddBuff`, `MapEvents.cs AddNpcBuff/AbnormalModel`,
  `Pvp.cs PvpHits`, `Rides.cs UseSkillItemAsync`.
* Status words: **Supported** (modelled from client data), **Partial**, **Missing** (no code), **Ignored-OK** (client handles it,
  server need not), **Candidate** (works, numbers are OURS).

### Where the server takes skill data from (important for every gap below)

| Source | Used for | Coverage of the player closure |
|---|---|---|
| `skills-client.json` (SkillClient) | damage rows per level, areas, row conditions, DoT ticks, delayed casts | 4,786 / 4,786 skills, 548 / 573 abnormals |
| `class-skills.json` (data.Skill) | learned kit (auto-learn unlocks), cost, PvP damage rows | 25-31 skills per class only |
| `skill-behaviour.json` (Behaviour) | disposition, **which abnormals a cast applies** (RollAbnormals), chains, damage fallback | 3,701 / 4,786 (1,085 missing, nearly all specialisation variants) |
| `abnormal-table.json` | self-buff StatChange, CC types, knockback, immunities on the player | 548 / 573 |
| `skill-guide.json` (shugo.gg text) | monster stat debuffs, guide DoT, guide stuns, lifesteal, "hits N times" | text-based, CANDIDATE |
| `abnormal-observed.json` | recorded stat changes of self buffs | few |

## 1. Skill effect types (SkillEffect.EffectType), player closure

| Effect type | Skills | Status | Code / evidence |
|---|---:|---|---|
| Damage | 3,696 | Supported (Candidate formula) | `Combat.ClientHits`, `SkillClient.DamageAt` (levels, split [4], PvP [21-24]); areas `ClientSkills.ClientArea` |
| GroggyGuardDecreaseEnergy | 2,495 | Supported | `BossClient.GroggyHit` via `AfterNpcDamage` |
| Abnormal_True | 1,589 | Partial | via `skill-behaviour.json` rows only (`SkillAbnormals.RollAbnormals`); Positive -> caster, Negative -> aimed monster, no party/ally; see section 2 |
| Abnormal_Cc | 1,191 | Partial | monster "held" (no move, no attack) for every CC type; knockback/pull/knockdown moves (`AbnormalMove`); never on players in PvP |
| Dispel_True | 629 | **Missing** | no code. Values `[bool, Debuff/Buff/None, ..., count]` (440 Debuff, 113 Buff, 31 TRUE-DeBuff) |
| Teleport_Caster | 530 (718 rows) | Ignored-OK / P3 | dash/blink; client moves itself; server position stays old until next move packet (range checks) |
| NoEffect | 443 | n/a | |
| CasterSkill | 385 | **Missing** | e.g. 11200050 -> 11200057, Assassin 13050000/13070000/13220000/13360000 follow-ups never cast |
| Spawn | 218 | Supported (Candidate numbers) | `GameSession.Summons.cs`, `PlayerTraps.cs` (summons.xml, player-traps.xml) |
| HpHealRatio | 169 | **Missing** | no HP restore for any player skill (only items, lifesteal text, Restoration) |
| HpHeal | 159 | **Missing** | e.g. Templar 12350000 `[180,198,10000,...]` |
| SpHeal | 103 | **Missing** | |
| Abnormal_Passive | 100 | Partial | `Buffs.PassiveAbnormals`: icon + weapon-equip permission only; StatChange / procs of the passive not applied |
| MPBurn | 38 | **Missing** | |
| DecreaseCooltime / IncreaseCooltime | 29 / 8 | Supported | `GameSession.Cooltimes.cs` (0x3847, OBSERVED) |
| Toggle (+ ToggleOnAbnormalId, SubType Toggle) | 18 | **Missing** | `CastRequest.ChangeToggle` parsed (GameSession.cs 2797) but never used |
| MpHealRatio / MpHeal | 11 / 9 | **Missing** | (MP on hit only from mp-gain.xml / guide text) |
| Despawn | 6 | Supported | Summons (16009999, 16290000-4) |
| Multiply_Dot | 2 | Missing | Cleric 17190040/50 |
| Rebirth | 1 | Missing | Cleric 17390003 resurrect (UseRebirth from items works) |

Item skills (319): HpHeal_Item, HpHealRatio_Item, MpHeal_Item, FpHeal_Item, Abnormal_True Supported (`Rides.UseSkillItemAsync`,
`Buffs.ApplyItemAbnormals`). Missing: CasterSkill 9, SpHeal_Item 2, Dispel_True 2, Damage 1, ExpCharge_Item 1.
**296 / 319 item skills fully modelled**; the 23 others mostly carry ImmuneAbnormal (10), ImmuneBind (8), HpBarrier (3).

### Targeting / area shapes (filters of the closure's hit steps)

| Shape | Skills | Status |
|---|---:|---|
| Single enemy | 1,708 | Supported |
| Circle enemy | 1,299 | Supported (radius, offset, max targets, navmesh visibility) |
| Rectangle enemy | 998 | Supported (anchor = caster: CANDIDATE) |
| Arc enemy | (in Circle/Rect families) | Supported |
| Self | 1,142 | Supported for buffs on the caster |
| Self enemy+friend / Circle friend / Self friend / MyParty filter | 555 / 207 / 19 / 190 | **Partial: allies never receive anything**, only the caster |
| MyChild / MyParent filter (summon targets) | 18 / 5 | Missing (buffs on own summon) |
| Projectile steps | 1,070 | Supported (timings OBSERVED for 22 skills, medians otherwise) |

### Cast types

| Cast type | Skills | Status | Code |
|---|---:|---|---|
| Instant / cast time | all | Supported | `CastReplies`, `SkillTimers` |
| Charge (SkillCharge) | 159 | Supported | `GameSession.Charging.cs` (OBSERVED wire) |
| Chain / combo (ChainSkillPrevSkillId) | 884 | Supported | `GameSession.Chains.cs` 0x3818/0x3819 |
| Auto-load charges (AutoLoadType Time) | 101 | Supported (refill without cooldown stat: PROVISIONAL) | Chains.cs |
| Ground / location | via Circle on caster | Supported (self-location clamp) | `SkillChecks.cs` |
| Delayed (EndCastSkill / EndCastSkillByCaster on a monster) | 26+12 abnormals | Supported on monsters only | `ClientSkills.DelayedCastTick` |
| Toggle | 18 | **Missing** | |
| Channeling | 0 (table empty) | n/a | |
| Specialisation variants (SpecializedSkill children) | ~3,100 | **Missing** (see gap G4) | `Specialization.cs` stores parts only |
| Stigma skills | 13 per class | **Missing** (not learnable) | no handler; `GameData.LearnedSkills` = auto-learn unlocks only |

### Skill conditions

| Condition | Rows | Status |
|---|---:|---|
| GatherCount, IsMainTarget, ObjectType, CalcBackAttack/Front, Target/Caster_HasAbnormalId/GroupId(_ByCaster), Target_HasAbnormalEffectType, Target_MaxHPRatio_Lower/Upper, Probability | 2,990 / 2,952 / 2,045 / 64 / ~1,750 / 386 / 56 / 7 | Supported (`SkillClient.RowApplies`); stack counts > 1 never met (no stacking) |
| Distance_Lower, Caster_Combat, Caster_MoveType, Caster_MaxHPRatio_Lower, Caster_HasAbnormalEffectType | 34 / 32 / 12 / 10 / 6 | **Missing** (evaluated as "not met") |
| Table_SkillCond (use conditions: target Groggy/Stun/Knockdown, self Frozen/Fear, self CondDefenceWeaponBlock/ShieldBlock/Block/Miss, own abnormal group) | 110 | Missing server-side; the CondDefence* counters can never light up because the server never grants the block/parry/miss abnormals |

## 2. Abnormal effect types (SkillAbnormalEffect), 573 player abnormals

Counts = distinct abnormals carrying the type. "Self" = on the caster, "Target" = on a monster.

| Abnormal effect | Abn. | Self | Target (monster) | Code / note |
|---|---:|---|---|---|
| StatChange | 379 | Partial: applied, **level-1 values only** (SkillAbnormalEffectLv ignored) | Partial: only what `skill-guide.json` text says (Defense/Attack/MoveSpeed/Tolerance) | `Buffs.AddBuff`, `Debuffs.GuideDebuff` |
| AbnormalClear (end on map change / death...) | 120 | Ignored (buffs end at death anyway) | - | P3 |
| Dot_NormalCalc | 95 | - | Supported (client ticks per level) | `ClientSkills.ClientDotTick`, `Debuffs.DotTick` |
| Hot | 42 | **Missing** (only Hot_Item/Hot_Ratio/Mot/Sot/Oot/Fot) | - | `Buffs.AddBuff` switch |
| ImmuneAbnormal | 28 | **Missing** on player | Supported for NPC passives (10000001) | `AbnormalTable.Blocking` |
| EndCastSkill / EndCastSkillByCaster | 26 / 12 | Missing | Supported | `ClientSkills.NoteDelayedCasts` |
| TriggerSkillByAttackAll | 24 | **Missing** (gear godstones only) | - | `GearEffects` handles gear passives only |
| HpBarrier (shield) | 23 | **Missing** | - | |
| ImmuneSkillEffectGroup / ImmuneDecreaseHP | 21 / 7 | **Missing** on player | Supported for scripted NPCs | `MapEvents.AbnormalModel` |
| OnExpireAbnormal | 20 | Missing | Missing | |
| TriggerSkillByGetDamage / ByGetAbnormal / BySelfBlock / BySelfWeaponBlock / ByKill | 20 / 8 / 6 / 5 / 1 | **Missing** | Missing | counter-attack / reactive skills dead |
| Stun, Hold, Bind, KnockdownInstant, Aerial, Frozen, Fear, Sleep, BlockActive, BlockSkill, BlockMoveControl, BlockMovePosition, Move_CannotUseSkill | small (shared abnormals) | Supported on player (cast block `CrowdControl.CcBlocksCast`, knockdown move) | Supported as "held" + moves | |
| Snare / Slow | 4 / 2 | Supported (StatChange MoveSpeed) | Partial: held completely when sent as Abnormal_Cc, else guide text | |
| CastSkill / CastSkillToCaster | 14 / 6 | Missing | Missing | |
| StatConvert | 9 | Missing | - | Templar 12450000, passives 17740000/18710000 |
| DamageTransfer | 8 | Missing | - | Templar 12250000 |
| Stealth / StealthSeek | 8 / 8 | Missing (monsters still see the player) | - | Assassin 13180000/13140000, Ranger 14190000 |
| GetDamageMultiplyByCaster / DamageMultiply | 7 / 4 | - | Missing | Sorcerer 15320000, Elementalist 16140000 |
| ForceCriticalDamage | 6 | Missing | - | Assassin stealth follow-ups |
| SkillTransform (+ Table_SkillTransform 278 rows, 13 groups) | 6 | Missing | - | |
| CloneSkillEffect | 6 | Missing | - | Assassin 13310000 |
| ConvertDamageToMP / ConvertDamageToHpHeal | 6 / 6 | Missing | - | Sorcerer 15230000, Ranger 14350000 |
| Polymorph (+ Polymorph_CannotMove) | 6 (+1) | Supported for items (visual) | Partial (icon / hold only) | Sorcerer 15140000 |
| DamageReflect | 3 | Missing | - | |
| Taunt | 1 | - | Missing (aggro not forced) | Templar 12120000 |
| Blind | 1 | - | Partial (icon only, no miss) | |
| UseRebirth | 2 | Supported | - | `Buffs.UseRebirthBuff` |
| IgnoreBackAttack, Loophole, Dot_TargetMaxHP, IgnoreCollision, EndMotion, BlockAniHit, Rotate, Block* (interact/loot/equip) | 1 each | Missing / Ignored-OK (client-enforced) | | |

Abnormal meta:

| Rule | Abnormals | Status |
|---|---:|---|
| One per group, replaced (0x382b keeps uid) | all | Supported (`AddBuff`, `AddNpcBuff` - NPC side keys by id, not group) |
| AbnormalReplaceType Priority / PriorityReplace / Replace / Time | 376 / 77 / 98 / 1 | Partial: always replaced, priority never compared |
| AbnormalOverlapCount > 1 (Separate 9, Renew 1) | 10 | **Missing** (no stacks; stack conditions treat any presence as 1) |
| Abnormal level from skill level ([3] of the Abnormal row + SkillEffectLv) | all | Used for DoT ticks only; StatChange/Hot use level-1 values |
| Dispel categories (Buff / Debuff / DeBuff, SkillAbnormalProperty Shock / Mental / Body) | 629 skills | Missing |
| Status-effect chance / resist (AbnormalAccuracy vs AbnormalResistance) | - | Supported (monster->player resist, player chance bonus); NPC resistance none |

## 3. Per class: skill book (SkillAcquireData base skills)

OK = every effect and abnormal type of the skill (with its charge levels and chain follow-ups) is modelled and the skill can be
learned. "Learnable" = in the server's auto-learn kit. Specialisation variants are not counted (all of them are broken, G4).

| Class | Book | OK | Partial | Broken | OK % of book | Learnable | OK % of learnable |
|---|---:|---:|---:|---:|---:|---:|---:|
| Gladiator | 40 | 11 | 0 | 29 | 28% | 27 | 41% |
| Templar | 38 | 11 | 1 | 26 | 29% | 25 | 44% |
| Assassin | 39 | 7 | 1 | 31 | 18% | 26 | 27% |
| Ranger | 38 | 11 | 2 | 25 | 29% | 25 | 44% |
| Sorcerer | 39 | 10 | 2 | 27 | 26% | 26 | 38% |
| Elementalist | 44 | 14 | 1 | 29 | 32% | 31 | 45% |
| Cleric | 38 | 8 | 2 | 28 | 21% | 25 | 32% |
| Chanter | 38 | 11 | 0 | 27 | 29% | 25 | 44% |

Nearly all "OK" skills are the plain damage actives. The broken ones per class (cause in brackets; full list in `cover.json`):

* **Gladiator**: 11290000 (dispel), 11260000 (dispel, ImmuneAbnormal), 11300000 (OnExpireAbnormal, monster StatChange text-only);
  passives 11710000-11800000, 11170037, 11300037, 11380007 (stats not applied; procs TriggerSkillByAttackAll/GetDamage/GetAbnormal/
  SelfWeaponBlock); all 13 stigmas not learnable (11080000, 11110000 block counter, 11130000, 11240000, 11250000, 11340000,
  11380000 immunity, 11390000, 11400000, 11410000 CastSkill, 11430000, 11450000, 11700000).
* **Templar**: 12350000 (**heal**), 12260000 (dispel/immunity), 12270000 (partial: debuff text-only); passives 12710000-12800000,
  12300037; stigmas incl. 12230000 heal, 12320000 HpBarrier on ally, 12250000 DamageTransfer on ally, 12120000 Taunt + CastSkill,
  12110000 block counter, 12450000 StatConvert.
* **Assassin**: 13050000, 13070000, 13220000, 13360000 (CasterSkill follow-ups, dispel), 13130000 (dispel, stack conditions),
  13260000, 13100000 (partial: stacking); passives 13710000-13800000, 13220037, 13350007; stigmas incl. 13180000/13140000 Stealth,
  13310000 CloneSkillEffect, 13080000 GetDamage trigger.
* **Ranger**: 14260000 (dispel/immunity), 14130000/14050000 (partial: snare/debuff); passives 14710000-14800000, 14050007; stigmas
  incl. 14190000 Stealth, 14350000 ConvertDamageToHpHeal, 14380000 attack trigger.
* **Sorcerer**: 15150000 (target TriggerSkillByGetAbnormal), 15240000 (dispel/immunity), 15090000/15110000 (partial: snare);
  passives 15710000-15800000, 15220037, 15410057; stigmas incl. 15160000 HpBarrier, 15230000 ConvertDamageToMP, 15140000
  Polymorph + CasterSkill, 15320000 GetDamageMultiply, 15410000.
* **Elementalist**: 16120000 (**heal**), 16140000 (DamageMultiply), 16300000/16330000 (dispel, CasterSkill), 16200000; passives
  16710000-16800000, 16300001; stigmas incl. 16190000 Hot on ally, 16060000 MPBurn, 16170000 CastSkillToCaster.
* **Cleric**: 17100000, 17120000 (**main heals**), 17090000 (**HoT on ally**), 17240000 (dispel/immunity), 17070000/17080000
  (partial); passives 17710000-17800000, 17270147; stigmas incl. 17290000 heal+dispel, 17390000 party resurrection buff,
  17410000 toggle, 17420000 party, 17190000 Multiply_Dot.
* **Chanter**: 18120000 (**heal + HoT on party + dispel**), 18080000, 18200000 (dispel); passives 18710000-18800000, 18080037;
  stigmas incl. 18160000/18190000 toggles (mantras), 18170000/18420000 heals, 18240000 HpBarrier on party, 18250000 party,
  18440000 ImmuneDecreaseHP on party.

## 4. Damage / stat formula gaps (clearly missing only)

Current model (`Combat.cs`, `GameSession.cs 3420-3600`, `Pvp.cs`): `(flat + attack x ratio/10000)/split`, crit chance
`Critical/1000` capped 0.5, crit x1.5, mitigation `1000/(1000+defense)`, then OURS amplify/add sums.

1. **Monster Defense is always 0**: `Target.Defense` is never set in `Combat.MapTargets` (no `Defense =` anywhere). Defense debuffs
   (`EffectiveDefense`), `PvePierce`, DefensePierce/IgnoreDefense stats do nothing in PvE. NpcData/NpcStat defense must be loaded.
2. **No hit/miss for player -> monster**: no Accuracy vs NPC evasion, no NPC block/parry, no NPC CriticalResist; Blind/ForceMiss
   cannot work.
3. **Critical**: fixed x1.5 and a 50% cap (`Combat.CriticalMultiplier/CriticalCap`); client caps Critical at 20000
   (StatCorrectionNumber MaxValue) and CriticalRatio/AGI (PcStatSecond) are not used in PvE (only in PvP). No CriticalResist on NPCs.
4. **Physic vs Magic**: same mitigation for both; MagicDefense/MagicArmorDefense lines and NPC element resistances (Tol:<elem>
   exists only as debuff mods) absent.
5. **Level difference**: no attacker/defender level scaling (NPC level only drives NPC HP/damage tables).
6. **PvP**: damage only for skills with rows in `class-skills.json`/`skill-behaviour.json` (`PvpHits` line 38), no row
   conditions (IsMainTarget/GatherCount), no areas (aimed player only), no debuff/CC/DoT on the victim
   (`NpcHitsPlayer(total, now, playerKey)` with skill 0), all divisors (`EvaPerPoint`, `CritPerPoint`, `BlockPerPoint`) OURS.
7. Heal power: `HealReceived` (AmplifyHpHealGet) exists, but no caster-side heal stats (AmplifyHpHeal / HealBoost) because skill
   heals are missing.

## 5. Prioritised GAP LIST

P1 breaks play, P2 wrong, P3 cosmetic. Size S < 1 day, M 1-3 days, L > 3 days.

| # | Gap | Skills / classes | How to implement (client data / recordings) | Size | Prio |
|---|---|---|---|---|---|
| G1 | **Skill heals missing** (HpHeal 159, HpHealRatio 169 skills; Hot 42 abnormals) | Cleric 17100000/17120000/17090000/17290000, Chanter 18120000/18170000/18420000, Templar 12350000/12230000, Elementalist 16120000/16190000, many variants | HpHeal `[flat min, max, ratio(1/10000 of attack?), ...]`, HpHealRatio `[min, max (1/10000 MaxHP), -1, ...]`; Hot `[interval, min, max, ratio, ...]` -> reuse `HealOverTime` + `RidePackets.AbnormalEffect`. Wire: find official Cleric/Chanter heal casts (3,541 Cleric + 16,880 Chanter stigma-family casts recorded) for the 0x3804 heal form. Apply `HealReceived`. | M | P1 |
| G2 | **Ally / party targeting** (friend filters 555+207+19, MyParty 190) | every party buff/heal/shield: Chanter mantras, Cleric, Templar 12320000/12250000, Elementalist 16190000/16360000 | For friend steps pick party members (Social.PartyOf) in the filter area; apply via their session (`InvokeAsync`) AddBuff/heal; broadcast 0x382a with caster key. | M-L | P1 |
| G3 | **Stigma skills not learnable** (13 per class, 104 book skills, 20 levels each) | all classes (NeedCharacterLevel 22+, AscensionGrade 3) | SkillAcquireData Stigma rows: CostStigmaPoint/CostStigmaSuperiorPoint, NeedPrevSkillDataList; learn + level-up through the stigma point pool (points already stored: `stigmaPoints`); add to `learned`, 0x5100/0x3813. Need a recording of a stigma learn for the RQ opcode. | M | P1 |
| G4 | **Specialisation parts have no effect; variant ids refused** | ~3,100 closure skills; official casts use them (4,219 casts of xx0010-xx3450 ids in recordings, e.g. 14340030, 18090340, 11012340) | Table_SpecializedSkill maps parent + equipped parts -> child id. Accept a child cast when its parent is learned and the equipped parts match (`learned.Contains` in CastReplies 2900), or map base -> child server-side; use the child's client rows. Regenerate `skill-behaviour.json` from Table_Skill (1,085 closure skills missing). Verify live which id our client sends. | M | P1 |
| G5 | **Passive skills: stats and procs not applied** | 53 book passives with StatChange; 39 TriggerSkillByAttackAll, 14 ByGetDamage, 10 ByGetAbnormal, SelfBlock/WeaponBlock passives (every class 71x-80x) | Sum the passive abnormals' StatChange (at learned level) into `CombatStat` like `GearEffectStat`; procs: extend `GearProcs` (TriggerSkillByAttackAll: skill, chance, cooldown, hit types) to class passives; ByGetDamage/ByGetAbnormal hooks in `NpcHitsPlayer`/`AddBuff`. | M | P1 |
| G6 | **Dispel missing** (Dispel_True 629 skills) | 24 book skills: every class's "remove debuff" (11260000, 12260000, 13260000, 14260000, 15240000, 16200000, 17240000, 18200000), Cleric/Chanter cleanses | Values [self?, Buff/Debuff, ..., count, ..., chance]; remove matching abnormals (SkillAbnormalProperty Shock/Mental/Body) from self/ally/target; 0x382c reason (find in recordings). | S-M | P2 |
| G7 | **Defensive abnormals on the player**: HpBarrier 23, ImmuneAbnormal 28, ImmuneSkillEffectGroup 21, ImmuneDecreaseHP 7, DamageReflect 3, DamageTransfer 8, ConvertDamageTo* 12 | escape/immunity skills of every class, Templar/Chanter shields, Sorcerer 15160000/15230000 | Hook in `NpcHitsPlayer` / `PvpHits` before HP loss: shield pool `[.., amount ..]`, immunity lists (effect names in values[5]), reflect %; RollAbnormals already puts the abnormals on the player. | M | P2 |
| G8 | **PvP skills** | all classes | Use `ClientHits` path (client rows, conditions, areas) for player targets; apply Negative abnormals/CC/DoT to the victim (`NpcHitsPlayer` with the skill); party exclusion. | M | P2 |
| G9 | **Self-buff values at level 1; abnormals on the wrong side** | all self buffs; 250 Negative skills carrying Self-filter abnormals (e.g. 12090000, 12350000, 13140000, 16250000) | Read StatChange/Hot at the applied abnormal level (`SkillClient.ValuesAt(AbnormalEffect, level)`); route each Abnormal row by its step filter (self vs enemy), not by Disposition. | S | P2 |
| G10 | **Monster debuff effects** (StatChange on monsters only via guide text; Snare/Slow as full hold; GetDamageMultiplyByCaster 7, DamageMultiply 4, Blind, Taunt) | 29 book skills (11300000, 12270000, 13100000, 14050000, 15090000, 17070000...), Sorcerer 15320000, Elementalist 16140000, Templar 12120000 | Map client StatChange names (Defense, MoveSpeed, AttackSpeed, ...) onto `Target.AddMod`; Snare/Slow -> MoveSpeed mod not hold; DamageMultiply -> `TakenAfterMods`; Taunt -> NpcAi forced aggro. | M | P2 |
| G11 | **Monster Defense = 0, no player hit/miss, crit model** | all damage | Load NPC defense/evasion/crit-resist from NpcData stat columns; accuracy vs evasion roll; crit from Critical incl. CriticalRatio, multiplier from stats. Calibrate with recorded 0x3804 amounts (`tools/combat.py`). | M | P2 |
| G12 | **CasterSkill / CastSkill / CastSkillToCaster / OnExpireAbnormal follow-ups** | 385 skills (Assassin combos 13050000/13070000/13220000/13360000, Elementalist 16300000/16330000, Gladiator 11410000), 14+6+20 abnormals | Cast the named skill on the caster/target after the parent (reuse `DelayedCast` / `CombatReplies` with the child id; OnExpire values [abnormal, TimeOut, ms]). | S-M | P2 |
| G13 | **Reactive counters & use conditions** (TriggerSkillBySelfBlock/WeaponBlock, Table_SkillCond CondDefence*) | Gladiator 11110000/11720000, Templar 12110000/12720000/12780000, Chanter 18140000/18720000 | On a player block/parry/evade (`PveAvoidance`), grant the CondDefence abnormal so the client enables the counter; refuse casts whose SkillCond is not met. | M | P2 |
| G14 | **Toggle skills** (18) | Cleric 17410000, Chanter 18160000/18190000 (mantras) | `ChangeToggle` flag: first cast adds ToggleOnAbnormalId (permanent), second removes it; MP drain rows if any. | S | P2 |
| G15 | **MP/SP effects** (SpHeal 103, MpHeal 20, MPBurn 38) | Gladiator 11110008/9, many variants, Elementalist 16060000 | Restore SP/MP with 0x8d00 CharPoints; MPBurn on players only (PvP). | S | P2 |
| G16 | Stealth / StealthSeek / ForceCriticalDamage / CloneSkillEffect / SkillTransform / StatConvert | Assassin 13180000/13140000/13310000, Ranger 14190000, Templar 12450000, 13 SkillTransform groups | Stealth: NPC aggro skips stealthed player until it attacks; ForceCritical next hit; StatConvert into CombatStat; SkillTransform: Table_SkillTransform swap while abnormal active. | M | P2 |
| G17 | Stacking and replace priority (OverlapCount>1: 10 abnormals; Priority rules) | Assassin 13100000/13270000/13130000 stacks, Sorcerer 15320000 | Keep a stack count per abnormal (0x382b with count), use it in `RowApplies` stack conditions; compare priority before replacing. | S | P2 |
| G18 | Missing row conditions (Distance_Lower, Caster_Combat, Caster_MoveType, Caster_MaxHPRatio_Lower, Caster_HasAbnormalEffectType) | 94 rows | Add to `SkillClient.RowApplies` (inCombat, distance, caster HP ratio). | S | P2 |
| G19 | Resurrection (Rebirth) and Multiply_Dot | Cleric 17390000/17390003, 17190040/50 | Rebirth on a dead party member (revive in place, HP%); Multiply_Dot multiplies running DoT ticks. | S-M | P3 |
| G20 | Teleport_Caster server position | 530 skills (dashes/blinks) | Move the server position to the effect's target location (values: location type, distance) so range checks after a dash do not refuse. | S | P3 |
| G21 | Item skills remaining (23 / 319) | ImmuneAbnormal/ImmuneBind potions, HpBarrier scrolls, CasterSkill items, SpHeal_Item | follows G7/G12/G15 | S | P3 |
| G22 | AbnormalClear end conditions | 120 abnormals | End buffs on MapChange etc. as the values say. | S | P3 |

## Notes / verification needed

* G4: the official 0x3802 echoes child ids (e.g. 18090340). Whether our client sends the child id or the base id with parts
  equipped is not traced yet; both ways the server ignores the parts.
* "OK" damage skills still use the CANDIDATE damage model (section 4).
* No official recording contains a party heal/buff on another player yet (SkillAbnormals.cs header) - record a party session
  with a Cleric or Chanter before G1/G2.
