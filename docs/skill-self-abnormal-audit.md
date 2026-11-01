# Skill self-abnormal audit (attack skills)

Server rule (`SkillSelfAbnormalPolicy`): **no `0x382a` on the caster** from hit-time `ApplyCasterSkillAbnormals` or chain-accept priming for any skill with Damage rows, traps, or zones. Self toggles use `skill_cond_grant` on cast accept (`SelfAbnormalPath.SkillCondGrant`). Chain accept may still send 0x382a only for abnormal-only parents with export `caster_debuffs` (e.g. Ranger Defiance); attack parents such as Savage Roar open `0x3818` without self abnormals.

Every player `AddBuff` logs playtest action `self_abnormal` with `abnormalId`, `skillId`, `path`, and `allowed`.

Removed `caster_debuffs` from `skill-areas.json` for every attack skill below (debuffs stay on `target_debuffs` only).

| Class | Skill | Name | Abnormal id(s) (was self) | Now |
| --- | ---: | --- | --- | --- |
| Gladiator | 11290000 | Mocking Blade | 112900001 | target-only / cast-grant |
| Templar | 12090000 | Punishment | 120900001 | target-only / cast-grant |
| Templar | 12130000 | Poach | 121300001 | target-only / cast-grant |
| Templar | 12270000 | Debilitating Smash | 122700001 | target-only / cast-grant |
| Templar | 12350000 | Warding Strike | 123500001 | target-only / cast-grant |
| Assassin | 13100000 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100010 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100020 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100030 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100040 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100050 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100120 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100130 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100140 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100150 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100230 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100240 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100250 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100340 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100350 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13100450 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13101230 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13101240 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13101250 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13101340 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13101350 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13101450 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13102340 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13102350 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13102450 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13103450 | Savage Roar | 130000011 | target-only / cast-grant |
| Assassin | 13220000 | Shadow Fall | 132200002 | target-only / cast-grant |
| Elementalist | 16140000 | Jointstrike: Curse | 161400001 | target-only / cast-grant |
| Elementalist | 16330000 | Dimensional Control | 163300001 | target-only / cast-grant |
| Cleric | 17070000 | Chain of Torment | 170700001 | target-only / cast-grant |
| Cleric | 17080000 | Debilitating Mark | 170800001 | target-only / cast-grant |
| Chanter | 18080000 | Wave Blow | 180800001 | target-only / cast-grant |
| NPC/Common | 4900202 | Capture Lugi! | 49002021 | target-only / cast-grant |
| NPC/Common | 12330010 | STR_SKILL_PC_TEMPLAR_12330000 | 123300001 | target-only / cast-grant |
| NPC/Common | 12330120 | STR_SKILL_PC_TEMPLAR_12330000 | 123300001 | target-only / cast-grant |
| NPC/Common | 12330130 | STR_SKILL_PC_TEMPLAR_12330000 | 123300001 | target-only / cast-grant |
| NPC/Common | 12330140 | STR_SKILL_PC_TEMPLAR_12330000 | 123300001 | target-only / cast-grant |
| NPC/Common | 12330150 | STR_SKILL_PC_TEMPLAR_12330000 | 123300001 | target-only / cast-grant |
| NPC/Common | 12331230 | STR_SKILL_PC_TEMPLAR_12330000 | 123300001 | target-only / cast-grant |
| NPC/Common | 12331240 | STR_SKILL_PC_TEMPLAR_12330000 | 123300001 | target-only / cast-grant |
| NPC/Common | 12331250 | STR_SKILL_PC_TEMPLAR_12330000 | 123300001 | target-only / cast-grant |
| NPC/Common | 12331340 | STR_SKILL_PC_TEMPLAR_12330000 | 123300001 | target-only / cast-grant |
| NPC/Common | 12331350 | STR_SKILL_PC_TEMPLAR_12330000 | 123300001 | target-only / cast-grant |
| NPC/Common | 12331450 | STR_SKILL_PC_TEMPLAR_12330000 | 123300001 | target-only / cast-grant |
| NPC/Common | 13110000 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110010 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110020 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110030 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110040 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110050 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110120 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110130 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110140 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110150 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110230 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110240 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110250 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110340 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110350 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13110450 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13111230 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13111240 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13111250 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13111340 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13111350 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13111450 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13112340 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13112350 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13112450 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13113450 | Savage Back Kick | 130000011 | target-only / cast-grant |
| NPC/Common | 13120000 | Savage Smash | 130000011 | target-only / cast-grant |
| NPC/Common | 13120010 | Savage Smash | 130000011 | target-only / cast-grant |
| NPC/Common | … | +46 more variants | … | target-only |

Total attack skills scrubbed: **123**.
