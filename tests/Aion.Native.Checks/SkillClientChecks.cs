using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Player skills from the client tables (tools/skill_data.py -> tools/data/skills-client.json, SkillClient): per-level damage,
/// the damage split, area shapes and target caps, DoT ticks, charge levels, traps and anchor pulses. The expected numbers are
/// the table rows (cited) and, where named, the official recordings.
/// </summary>
static class SkillClientChecks
{
    public static void Run(Action<bool, string> check, GameData data)
    {
        var table = SkillClient.Embedded;
        check(table.Count > 7000, $"skills-client.json holds every player skill ({table.Count})");

        // Damage per level (SkillEffectLv Sorcerer_Skill009_LvUp): 15090000 flat 79 at level 1, 2038 at level 40, ratio 9250.
        var frost = table.Of(15090000)!;
        var row = frost.Effects.First(e => e.Type == "Damage");
        check(table.DamageAt(row, 1) is { FlatMin: 79, RatioMin: 9250, Split: 1, PvpFlatMin: 98 } && table.DamageAt(row, 40) is { FlatMin: 2038, RatioMin: 9250, PvpFlatMin: 3632 },
            "15090000 Damage row: flat 79 -> 2038 from level 1 to 40, attack ratio 9250, PvP flat 98 -> 3632 (SkillEffectLv)");
        var weapon = new WeaponStats(100, 100, 0, 0);
        var at1 = Combat.SkillHits(data, 15090000, "Sorcerer", 1, null, 0, new Random(7), weaponStats: weapon, skillLevel: 1);
        var at40 = Combat.SkillHits(data, 15090000, "Sorcerer", 1, null, 0, new Random(7), weaponStats: weapon, skillLevel: 40);
        check(at1.Count == 1 && at40.Count == 1 && Math.Abs(at40[0].Amount - at1[0].Amount - (2038 - 79)) <= 1 && at1[0].EffectId == 1509000011,
            $"a level-40 hit is the level-1 hit + (2038 - 79) with the same attack ({at1[0].Amount} -> {at40[0].Amount})");
        // [4] split (recording 20261107-012837: the level-1 Sorcerer's 15040000 hit 23-25 a row: flat 114 / 5).
        var split = Combat.SkillHits(data, 15040000, "Sorcerer", 1, null, 0, new Random(7), weaponStats: new WeaponStats(0, 0, 0, 0), skillLevel: 1);
        var fixing = data.BaseStats("Sorcerer", 1).GetValueOrDefault("FixingDamage");
        check(split.Count > 1 && split.All(h => h.Amount <= (114 + fixing * 2) / 5 + 1) && table.DamageAt(table.Of(15040000)!.Effects.First(e => e.Type == "Damage"), 1).Split == 5,
            $"Damage [4] divides the row (15040000 split 5: {string.Join(",", split.Select(h => h.Amount))}; official 23-25)");
        check(table.DamageAt(table.Of(15280002)!.Effects.First(e => e.Type == "Damage"), 1) is { FlatMin: 238, Split: 7 },
            "Bittercold Wind's pulse 15280002: flat 238 split 7 (official pulses 35-44 at level 1)");

        // Areas (SkillEffectFilter): 15090000 Circle 400 around the target, 4 targets; 11010000 Rectangle 590 x 500 from 100 behind.
        var circle = table.Of(15090000)!.Area!;
        check(circle is { Shape: "Circle", Radius: 400, Max: 4, NeedTarget: true }, "15090000 area: Circle radius 400 on the target, at most 4 (official max 4)");
        var origin = new Vector3d(0, 0, 0);
        Target At(double x, double y) => new() { Key = 1, Name = "t", Hp = 1, HpMax = 1, Position = new Vector3d(x, y, 0) };
        check(GameSession.InClientArea(circle, origin, new Vector3d(1000, 0, 0), 0, At(1380, 0)) && !GameSession.InClientArea(circle, origin, new Vector3d(1000, 0, 0), 0, At(1450, 0)),
            "a target-centred circle: 380 cm from the aimed monster is inside, 450 outside");
        var box = table.Of(11010000)!.Area!;
        check(box is { Shape: "Rectangle", Length: 590, Width: 500, Forward: -100, Max: 4 }
              && GameSession.InClientArea(box, origin, null, 0, At(400, 0)) && GameSession.InClientArea(box, origin, null, 0, At(-90, 0))
              && !GameSession.InClientArea(box, origin, null, 0, At(-150, 0)) && !GameSession.InClientArea(box, origin, null, 0, At(200, 300))
              && !GameSession.InClientArea(box, origin, null, 90, At(400, 0)) && GameSession.InClientArea(box, origin, null, 90, At(0, 400)),
            "11010000 Rending Blow: a 590 x 500 box from 100 cm behind along the facing (turns with the yaw)");
        var arc = table.All.SelectMany(s => s.Steps).Select(s => s.Filter).FirstOrDefault(f => f is { Shape: "Arc" });
        check(arc is { Radius: 400, Angle: 90 } && GameSession.InClientArea(arc, origin, null, 0, At(300, 100)) && !GameSession.InClientArea(arc, origin, null, 0, At(100, 300)),
            "an Arc 400 / 90 degrees: 18 degrees off the facing is inside, 72 degrees outside");

        // DoT per tick (SkillAbnormalEffectLv): Cold Storm's 152000021 ticks every 1000 ms, flat 472 at level 1, 963 at level 10.
        var storm = table.AbnormalOf(152000021)!.Effects.First(e => e.Type == "Dot_NormalCalc");
        check(table.TickAt(storm, 1) is { IntervalMs: 1000, FlatMin: 472, RatioMin: 8000 } && table.TickAt(storm, 10) is { FlatMin: 963 },
            "DoT tick from the client: 152000021 every 1000 ms, 472 + 8000/10000 of attack at level 1, 963 at level 10");
        var stormApply = table.Of(15200002)!.Effects.First(e => e.Type == "Abnormal_True");
        check(table.AbnormalLevelAt(stormApply, 1) == 1 && table.AbnormalLevelAt(stormApply, 12) == 12, "the applied abnormal level follows the skill level (SkillEffectLv [3])");

        // Charge levels (SkillCharge): 12090000 500/1000/1500 ms -> 12090001/2/3; 11360030 one level.
        check(GameSession.ChargeSteps(12090000).SequenceEqual([(500, 12090001), (1000, 12090002), (1500, 12090003)])
              && GameSession.ChargeSteps(11360030).SequenceEqual([(500, 11360031)]) && table.Of(14010000)?.Charge is { MinMs: > 0, Steps.Count: > 0 },
            "charge levels from SkillCharge (12090000: 500/1000/1500 ms; 11360030: 500 ms -> 11360031)");

        // Traps from the client tables: Ensnaring Trap 14180000 -> NPC 2920015, explosion 14180001 (Circle 500, 4), arm 14180007, 600 s.
        var traps = PlayerTrapTable.Embedded;
        check(traps.Of(14180000) is { Npc: 2920015, ExplodeSkill: 14180001, ArmSkill: 14180007 } t1 && traps.LifeOf(t1) == 600_000
              && traps.Of(14170010) is null or { ExplodeSkill: 14170011 } && traps.Of(14170030) is { FollowSkill: 14170037 } && traps.Of(14170050) is { ExplodeSkill: 14170001 }
              && table.Of(14180001)!.Area is { Shape: "Circle", Radius: 500, Max: 4 },
            "traps from the client: 14180000 Ensnaring Trap (2920015, explodes 14180001 on a 500 circle, 4 targets, stealth 600 s), A3 follow-up 14170037");

        // Anchor pulses: OBSERVED rows, else the pulse skill's cooldown (15200002 285 ms).
        check(SummonRules.Embedded.PulsesOf(2920011, 15280002) == (9, 200) && SummonRules.Embedded.PulsesOf(2920713, 15200002) == (0, 285)
              && SummonRules.Embedded.LifeOf(2920011, false) == 7200,
            "anchor pulses: Bittercold Wind 9 x 200 ms (OBSERVED), Cold Storm every 285 ms (its pulse skill's client cooldown), life 7.2 s");

        // Row conditions: 12100000's two rows (IsMainTarget true / false) give one hit on the aimed monster, one on another;
        // 13130000's rows by the stacks of 130000011 (none: 663, present: 742); 11010040 by GatherCount (1 target: 76, 4: 72).
        var w0 = new WeaponStats(0, 0, 0, 0);
        var main = Combat.SkillHits(data, 12100000, "Templar", 1, null, 0, new Random(3), weaponStats: w0, skillLevel: 1, rows: new SkillClient.RowContext(MainTarget: true));
        var other = Combat.SkillHits(data, 12100000, "Templar", 1, null, 0, new Random(3), weaponStats: w0, skillLevel: 1, rows: new SkillClient.RowContext(MainTarget: false));
        check(main.Count == 1 && other.Count == 1 && main[0].EffectId != other[0].EffectId, "IsMainTarget rows: one hit on the aimed monster, the other row on the rest (12100000)");
        int Flat(int skill, SkillClient.RowContext ctx) => table.Of(skill)!.Steps.SelectMany(st => st.Effects.Where(e => e.Type == "Damage" && SkillClient.RowApplies(st, e, ctx)))
            .Select(e => table.DamageAt(e, 1).FlatMin).DefaultIfEmpty(0).Sum();
        check(Flat(13130000, SkillClient.RowContext.Default) == 663 && Flat(13130000, new SkillClient.RowContext(TargetHas: a => a == 130000011)) == 742
              && Flat(11010040, new SkillClient.RowContext(Gathered: 1)) < 3 * 76 && Flat(11010040, new SkillClient.RowContext(Gathered: 1)) > 0,
            $"stack and gather rows: 13130000 663 without its mark, 742 with it; 11010040 the 1-target rows ({Flat(11010040, new SkillClient.RowContext(Gathered: 1))})");

        // Damage line and element from the client (SkillDamageType / AttributeType).
        check(table.Of(15090000) is { DamageType: "Magic", Element: "Water" } && table.Of(11010000) is { DamageType: "Physic" }, "damage line and element from the client");
    }
}
