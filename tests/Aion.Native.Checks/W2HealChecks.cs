using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Wave 2 heals and buffs (fix/w2-heal-buffs, GameSession.SkillSupport.cs): skill heals on the caster and party members, heal over
/// time ticks naming the healer, party buffs, self-buffs at the skill level's values, attack skills' self-buffs on the caster (not
/// the monster), dispel by category, immunity, shields absorbing then breaking, the HP floor.
/// </summary>
static class W2HealChecks
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);

    static (int Value, int Next) Varint(byte[] b, int i)
    {
        int value = 0, shift = 0;
        while (true)
        {
            var x = b[i++];
            value |= (x & 0x7f) << shift;
            if (x < 0x80) return (value, i);
            shift += 7;
        }
    }

    static int KeyOf(byte[] b) => Varint(b, 2).Value;

    /// <summary>A 0x3804 support row: target, mask, caster, skill, hit type, effect, amount.</summary>
    static (int Target, int Mask, int Caster, int Skill, int HitType, int Effect, int Seq, int Amount) Row(byte[] b)
    {
        var (target, i) = Varint(b, 2);
        var mask = b[i];
        i += 2;
        var (caster, j) = Varint(b, i);
        var skill = BitConverter.ToInt32(b, j);
        var (hitType, k) = Varint(b, j + 5);
        var effect = BitConverter.ToInt32(b, k);
        var seq = BitConverter.ToInt32(b, k + 4);
        var (_, l) = Varint(b, k + 8);
        var amount = (mask & 4) != 0 ? Varint(b, l).Value : 0;
        return (target, mask, caster, skill, hitType, effect, seq, amount);
    }

    public static async Task Run(GameData data, Func<SharedWorld, string, Task<GameSession>> newSession, long now, Action<bool, string> check)
    {
        // ---------- Official bytes (rec 20261106-145442 #9935 / #9938 Recuperation solo; rec 105448 #7705 a dispel) ----------
        check(GameSession.SupportHit(2872, 2872, 18120140, 0xe3, 2, 1812014011, 1, 10910, 1265).SequenceEqual(Convert.FromHexString("0438b8160400b816cc7d1401e302bb23016c010000009e55f1090100"))
              && GameSession.SupportHit(2872, 2872, 18120140, 0xe3, 0, 1812014014, 1, 10910, 0).SequenceEqual(Convert.FromHexString("0438b8160000b816cc7d1401e300be23016c010000009e550100")),
              "support rows rebuild the official Recuperation heal row (mask 4, amount 1265) and its empty dispel row (hit type 0)");
        check(GameSession.DispelRemove(26073, [0x2b], 12577, 11290000, 1129000042).SequenceEqual(Convert.FromHexString("2c38d9cb0101072b0ba1629045ac006a2c4b43")),
              "the dispel removal rebuilds the official 0x382c (mask 7, reason 11, dispeller, skill, effect)");
        check(RidePackets.AbnormalEffect(14415, 14415, 9114, 1873000211, 445, null, 18730002).SequenceEqual(Convert.FromHexString("0538cf7009cf709a4713b7a36fbd0312cc1d01"))
              && RidePackets.AbnormalDamage(14415, 20797, 9114, 1873000211, 445, 18730002).SequenceEqual(Convert.FromHexString("0538cf700abda2019a4713b7a36fbd0312cc1d01")),
              "a shield's add (0x3805 mask 9, its pool) and absorb (mask 0x0a, attacker, absorbed amount) rebuild the official bytes (rec 105448 #36371, #36669)");

        var world = new SharedWorld(data);
        var healer = await newSession(world, "Healer");
        var friend = await newSession(world, "Friend");
        var stranger = await newSession(world, "Stranger");
        foreach (var s in new[] { healer, friend, stranger }) await s.TickAsync(now);
        var party = new Party(world.Social.NextId(), healer.WorldPlayer!);
        party.Members.Add(friend.WorldPlayer!);
        world.Social.Parties[party.Key] = party;
        void Hurt(params GameSession[] who) { foreach (var s in who) s.SetPointsForW2Checks(s.PointsForW2Checks.HpMax / 2, s.PointsForW2Checks.MpMax); }

        // ---------- Radiant Recovery 17120000: MyParty circle counting the caster (caster + 3) ----------
        Hurt(healer, friend, stranger);
        var half = (Healer: healer.PointsForW2Checks.Hp, Friend: friend.PointsForW2Checks.Hp, Stranger: stranger.PointsForW2Checks.Hp);
        var radiant = healer.SupportForChecks(17120000, healer.PlayerKey, now);
        var heals = radiant.Where(b => Op(b) == 0x3804).Select(Row).Where(r => r.Effect == 1712000011).ToList();
        var toFriend = heals.FirstOrDefault(r => r.Target == friend.PlayerKey);
        var toSelf = heals.FirstOrDefault(r => r.Target == healer.PlayerKey);
        check(toSelf.Amount is >= 379 and <= 417 && toFriend.Amount is >= 379 and <= 417 && toFriend is { Mask: 4, HitType: 2, Seq: 1, Skill: 17120000 } && toFriend.Caster == healer.PlayerKey
              && healer.PointsForW2Checks.Hp == half.Healer + toSelf.Amount && friend.PointsForW2Checks.Hp == half.Friend + toFriend.Amount
              && stranger.PointsForW2Checks.Hp == half.Stranger && heals.All(r => r.Target != stranger.PlayerKey),
              $"Cleric Radiant Recovery heals the caster ({toSelf.Amount}) and the party member ({toFriend.Amount}) with level-1 HpHeal 379-417 (0x3804 mask 4, seq 1); not a player outside the party");
        var friendSaw = await friend.TickAsync(now + 1);
        check(friendSaw.Any(b => b.SequenceEqual(GamePackets.NpcHp(friend.PlayerKey, friend.PointsForW2Checks.Hp))) && friendSaw.Any(b => Op(b) == 0x3804 && KeyOf(b) == friend.PlayerKey),
              "the healed party member gets its HP (0x8d00) and sees the heal row");
        check(radiant.Any(b => Op(b) == 0x3804 && Row(b).Effect == 1712000012 && Row(b).HitType == 0),
              "its Dispel_True row with nothing to remove is shown with hit type 0 (official Recuperation row)");

        // ---------- Healing Light 17100000: the aimed party member, else the caster ----------
        Hurt(healer, friend);
        var before = (Healer: healer.PointsForW2Checks.Hp, Friend: friend.PointsForW2Checks.Hp);
        var light = healer.SupportForChecks(17100000, friend.PlayerKey, now + 10).Where(b => Op(b) == 0x3804).Select(Row).ToList();
        check(light.Count == 1 && light[0].Target == friend.PlayerKey && light[0].Seq == 1 && friend.PointsForW2Checks.Hp > before.Friend && healer.PointsForW2Checks.Hp == before.Healer,
              "Healing Light aimed at a party member heals that member only (its party step; the Self step is the fallback)");
        var selfLight = healer.SupportForChecks(17100000, healer.PlayerKey, now + 20).Where(b => Op(b) == 0x3804).Select(Row).ToList();
        check(selfLight.Count == 1 && selfLight[0].Target == healer.PlayerKey && selfLight[0].Seq == 2 && healer.PointsForW2Checks.Hp > before.Healer,
              "Healing Light without an ally heals the caster through its Self step (seq 2, as the official solo cast)");
        var outside = healer.SupportForChecks(17100000, stranger.PlayerKey, now + 30).Where(b => Op(b) == 0x3804).Select(Row).ToList();
        check(outside.Count == 1 && outside[0].Target == healer.PlayerKey, "aimed at a player outside the party: the caster is healed, not the stranger");

        // ---------- Heal over time: Cleric 17090000 on the party ----------
        Hurt(friend);
        healer.SupportForChecks(17090000, healer.PlayerKey, now + 100);
        var hot = friend.BuffForW2Checks(170900001);
        var hpBeforeTick = friend.PointsForW2Checks.Hp;
        var tick = friend.BuffTickForW2Checks(now + 100 + 2000);
        var tickRow = tick.FirstOrDefault(b => Op(b) == 0x3805);
        check(hot is { Heals: 1 } h && h.CasterKey == healer.PlayerKey && healer.BuffForW2Checks(170900001) is { Heals: 1 }
              && tickRow is not null && tickRow[Varint(tickRow, 2).Next] == 0x0b && Varint(tickRow, Varint(tickRow, 2).Next + 1).Value == healer.PlayerKey
              && friend.PointsForW2Checks.Hp > hpBeforeTick && tick.Any(b => b.SequenceEqual(GamePackets.NpcHp(friend.PlayerKey, friend.PointsForW2Checks.Hp))),
              $"the party heal over time lands on both, and ticks 2000 ms later: 0x3805 mask 0x0b naming the healer, HP {hpBeforeTick} -> {friend.PointsForW2Checks.Hp}");

        // ---------- Chanter party buff 18250000: a Self step for the caster, a MyParty step (not counting the caster) for the others ----------
        healer.SupportForChecks(18250000, healer.PlayerKey, now + 200);
        check(healer.BuffForW2Checks(182500001) is { CasterKey: 0 } && friend.BuffForW2Checks(182500011) is { } partyBuff && partyBuff.CasterKey == healer.PlayerKey
              && friend.BuffForW2Checks(182500001) is null && healer.BuffForW2Checks(182500011) is null && stranger.BuffForW2Checks(182500011) is null
              && friend.BuffStatForW2Checks("CombatSpeed") == 2000 && friend.BuffStatForW2Checks("CoolTimeIncrease") == -1000,
              "Chanter's party buff: the caster gets its own abnormal, the party member the party one (CombatSpeed +2000, its 0x382a names the Chanter); the stranger nothing");

        // ---------- Self-buff at the skill level: 18420000 MaxHpRatio 540 at level 1, 580 at level 2 ----------
        friend.SupportForChecks(18420000, friend.PlayerKey, now + 300);
        healer.SetSkillLevelForW2Checks(18420000, 2);
        healer.SupportForChecks(18420000, healer.PlayerKey, now + 300);
        check(friend.BuffStatForW2Checks("MaxHpRatio") == 540 && friend.BuffForW2Checks(184200001) is { Level: 1 }
              && healer.BuffStatForW2Checks("MaxHpRatio") == 580 && healer.BuffForW2Checks(184200001) is { Level: 2 },
              "a self-buff takes the cast skill level's values (SkillAbnormalEffectLv): level 1 +540, level 2 +580 MaxHpRatio; 0x382a carries the level");

        // ---------- An attack skill's self-buff (12090000 Self step) lands on the caster, not the monster ----------
        healer.SupportForChecks(12090000, Combat.TrainingDummyKey, now + 400);
        healer.SkillAbnormalsForChecks(12090000, Combat.TrainingDummyKey, now + 400);
        check(GameSession.SupportOnlyAbnormals(12090000).Contains(120900001) && healer.BuffForW2Checks(120900001) is { Stats: > 0 }
              && !healer.NpcAbnormalsForW2Checks(Combat.TrainingDummyKey).Contains(120900001),
              "Templar 12090000 (an attack skill) puts its Self-step buff 120900001 (Accuracy, damage) on the caster and no longer on the monster");

        // ---------- Dispel by category: 11260000 removes the debuffs, then its immunity holds ----------
        friend.AddBuffForChecks(12000001, 5000, 0, now + 500);   // a monster's stun (DeBuff)
        friend.AddBuffForChecks(12000101, 5000, 0, now + 500);   // a monster's poison (DeBuff)
        var cleanse = friend.SupportForChecks(11260000, friend.PlayerKey, now + 510);
        var removal = cleanse.FirstOrDefault(b => Op(b) == 0x382c);
        check(!friend.ActiveBuffs.Contains(12000001) && !friend.ActiveBuffs.Contains(12000101) && friend.ActiveBuffs.Contains(100200001) && friend.ActiveBuffs.Contains(182500011)
              && removal is not null && removal[Varint(removal, 2).Next] == 2 && removal[Varint(removal, 2).Next + 1] == 7
              && cleanse.Any(b => Op(b) == 0x3804 && Row(b).Effect == 1126000011 && Row(b).HitType == 2),
              "Gladiator 11260000 removes every debuff (stun and poison) but not the buffs: 0x382c mask 7 reason 11 naming the dispeller, the row shown with hit type 2");
        check(friend.ImmuneForW2Checks(12000001) && !friend.ActiveBuffs.Contains(12000001) && !healer.ImmuneForW2Checks(12000001),
              "its 100200001 (ImmuneAbnormal: Stun, Hold, ...) then blocks a monster's stun (0x3805 + 0x382d); without it the stun lands");

        // ---------- Shield 12320000: absorbs, then breaks ----------
        Hurt(healer, friend);
        healer.SupportForChecks(12320000, healer.PlayerKey, now + 600);
        var pool = healer.ShieldLeftForW2Checks(123200001);
        var hpMax = healer.PointsForW2Checks.HpMax;
        var hp0 = healer.PointsForW2Checks.Hp;
        check(pool == hpMax * 4100 / 10_000 && friend.ShieldLeftForW2Checks(123200001) == friend.PointsForW2Checks.HpMax * 4100 / 10_000,
              $"Nezekan's Shield gives the caster and the party member a shield of 41% of max HP ({pool})");
        var small = healer.NpcHitsPlayer(pool / 2, now + 700);
        check(healer.PointsForW2Checks.Hp == hp0 && healer.ShieldLeftForW2Checks(123200001) == pool - pool / 2
              && small.Any(b => Op(b) == 0x3805 && b[Varint(b, 2).Next] == 0x0a),
              "a hit smaller than the shield takes no HP: 0x3805 mask 0x0a with the absorbed amount");
        var big = healer.NpcHitsPlayer(pool, now + 800);
        var left = pool - pool / 2;
        check(healer.PointsForW2Checks.Hp == hp0 - (pool - left) && healer.BuffForW2Checks(123200001) is null
              && big.Any(b => Op(b) == 0x382c && b[^1] == 7),
              $"the next hit breaks it: the rest ({pool - left}) reaches HP, the shield ends with 0x382c reason 7");

        // ---------- HP floor: Barrier Spell 18440000 keeps HP at 10% ----------
        friend.ClearBuffsForChecks();
        friend.SupportForChecks(18440000, friend.PlayerKey, now + 900);
        var fMax = friend.PointsForW2Checks.HpMax;
        friend.NpcHitsPlayer(fMax * 2, now + 950);
        check(friend.PointsForW2Checks.Hp == fMax * 1000 / 10_000 && !friend.Dead,
              $"Barrier Spell (ImmuneDecreaseHP 1000): a killing blow leaves HP at 10% ({friend.PointsForW2Checks.Hp})");
    }
}
