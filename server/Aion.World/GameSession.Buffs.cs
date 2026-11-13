using Aion.Protocol.Native;

namespace Aion.World;

// Item buffs: the Abnormal_True effects of item skills (potions over time, food, drinks, scrolls), with
// the SkillAbnormal / SkillAbnormalEffect rows from tools/data/boxes.json. Wire as the official Life
// Potion and speed scroll uses (capture 20261013-144836): 0x382a AbnormalAdd (expire time = server
// clock at expiry), 0x3805 per heal-over-time row at once (remaining total) and on every tick (remaining,
// healed) with the HP as 0x8d00, 0x382c AbnormalRemove reason 1 at expiry, 0x364b MoveSpeedUpdate
// {205, summed MoveSpeed} for speed buffs. StatChange stats join the stat sheet (0x364a, CANDIDATE: no
// food sample). One buff per SkillAbnormal group: a new one takes the old one's uid (0x382b AbnormalChange_NT, OFFICIAL).
public sealed partial class GameSession
{
    sealed class Buff
    {
        public required int Uid, AbnormalId, SkillId, Group;
        public required long DurationMs, ExpireAt;
        /// <summary>A UseRebirth buff (Resurrection Spiritstone): kept through death, used to revive in place.</summary>
        public bool Rebirth { get; set; }
        /// <summary>A Polymorph buff (transformation candy): the form the client draws while it lasts.</summary>
        public bool Polymorph { get; set; }
        /// <summary>Added by a map event script (AddAbnormal): its RemoveAbnormal or leaving the map ends it.</summary>
        public bool Scripted { get; set; }
        /// <summary>A ChangeRelationshipEntity effect: the relationship the player shows while it lasts (PC_FriendlyToAll: monsters ignore them).</summary>
        public int? Relationship { get; set; }
        public Dictionary<string, int> Stats { get; } = [];
        public List<HealOverTime> Heals { get; } = [];
        /// <summary>Who put it on the player: a monster's key for its debuffs (the official 0x382a names the NPC, rec 115817 #139529), another
        /// player's key for a party heal / buff (official 0x3805 of a Hot names its healer, rec 115817 #125196), else 0 (the player).</summary>
        public int CasterKey { get; set; }
        /// <summary>Damage over time from a monster's abnormal (Dot_NormalCalc: the grass's poison 12000101 every 2000 ms).</summary>
        public List<DamageOverTime> Dots { get; } = [];
        /// <summary>The abnormal's level on the wire (Common_Abnormal level; the rank reward buffs' 3-21, OBSERVED), else 1.</summary>
        public int Level { get; set; } = 1;
        /// <summary>A season ranking reward buff (GameSession.RankRewards.cs): it follows the grades, not death.</summary>
        public bool Rank { get; set; }
    }

    /// <summary>A damage-over-time row on the player: the SkillAbnormalEffect id (what 0x3805 carries), interval, damage per tick.</summary>
    sealed class DamageOverTime
    {
        public required int EffectId, IntervalMs, PerTick;
        public required long NextAt;
    }

    /// <summary>A heal-over-time row: interval, amount per tick, what is left of the total, the point it restores (HP; the recovery
    /// orbs' Mot/Sot/Oot_Ratio restore MP, stamina, the diving gauge; Fot the flight gauge).</summary>
    sealed class HealOverTime
    {
        public required int EffectId, IntervalMs, PerTick;
        public required int Remaining;
        public required long NextAt;
        public CharPoint Point { get; init; } = CharPoint.Hp;
    }

    readonly List<Buff> buffs = [];
    /// <summary>When each abnormal last landed on the player (map-event GetAbnormal: a short one may be gone by the next tick).</summary>
    readonly Dictionary<int, long> abnormalsGainedAt = [];
    int buffUid;
    public const int MoveSpeedStat = 205;
    const byte ExpireTimeout = 1, ExpireReplaced = 4, ExpireEtc = 7, ExpireEventFunction = 9;
    /// <summary>The expire reason of every buff removed at a death (OBSERVED 0x13, players and NPCs, 2026-11-06 090856 #96385).</summary>
    const byte ExpireDeath = 0x13;

    public IReadOnlyList<int> ActiveBuffs => buffs.Select(b => b.AbnormalId).ToList();

    /// <summary>A stat summed over the active buffs (StatChange / MoveSpeed rows).</summary>
    int BuffStat(string stat) => buffs.Sum(b => b.Stats.GetValueOrDefault(stat));

    static IEnumerable<int> AbnormalIds(IReadOnlyList<SkillEffectRow> effects) =>
        effects.Where(e => e.Type == "Abnormal_True" && e.Value(2) > 0).Select(e => e.Value(2));

    /// <summary>Whether an item skill's buffs are known (their effects the server does not model only show the icon).</summary>
    static bool ItemAbnormalsSupported(IReadOnlyList<SkillEffectRow> effects) =>
        AbnormalIds(effects).Any() && AbnormalIds(effects).All(id => Boxes.Abnormal(id) is not null);

    AbnormalWire Wire(Buff b) => new(b.Uid, b.AbnormalId, b.DurationMs, b.ExpireAt, CasterKey: b.CasterKey != 0 ? b.CasterKey : playerKey, Level: (byte)Math.Clamp(b.Level, 1, 255),
        SkillId: b.SkillId == 0 ? null : b.SkillId, RangeBase: position ?? Vector3d.Zero);

    /// <summary>Adds an item skill's buffs: 0x382a, the first 0x3805 per heal row, stat and speed updates.</summary>
    List<byte[]> ApplyItemAbnormals(int skillId, IReadOnlyList<SkillEffectRow> effects, long now)
    {
        var replies = new List<byte[]>();
        var speedBefore = MoveSpeedBonus();
        foreach (var e in effects.Where(e => e.Type == "Abnormal_True"))
        {
            var duration = Math.Max(1, rng.Next(Math.Min(e.Value(0), e.Value(1)), Math.Max(e.Value(0), e.Value(1)) + 1));
            AddBuff(e.Value(2), duration, skillId, now, replies);
        }
        replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    /// <summary>
    /// One buff on the player: its SkillAbnormal effects the server models, 0x382a to the player and those nearby, the first
    /// 0x3805 per heal row, the polymorph and relationship updates. A duration below 0 lasts (the official passive's expire time);
    /// an abnormal the tables do not know only shows its icon. The caller refreshes the stats.
    /// </summary>
    Buff? AddBuff(int abnormalId, int durationMs, int skillId, long now, List<byte[]> replies, bool scripted = false, Target? caster = null, Action<Buff>? setup = null,
        int level = 1, int? remainingMs = null)
    {
        if (resources is null) return null;
        abnormalsGainedAt[abnormalId] = now;   // map-event GetAbnormal objectives (GameSession.MapEvents.cs GainedAbnormal)
        // The item abnormals (boxes.json), else the client's SkillAbnormal rows (abnormal-table.json: the monsters' debuffs, whose
        // group and StatChange rows were missing before 2026-11-12, e.g. the stuns' shared group 100 and the slows' MoveSpeed).
        var info = Boxes.Abnormal(abnormalId)
            ?? (AbnormalTable.Embedded.Of(abnormalId) is { } client ? new AbnormalInfo(client.Group, client.Effects.Select(e => (e.Id, e.Type, e.Values)).ToList()) : null)
            ?? (ClientSkills.AbnormalOf(abnormalId) is { } skillRow ? new AbnormalInfo(skillRow.Group, skillRow.Effects.Select(e => (e.Id, e.Type, e.Values)).ToList()) : null);
        // A skill's abnormal at its level (the Abnormal row's [3]): SkillAbnormalEffectLv replaces the level-1 values (OFFICIAL client
        // data; the official 0x382a carries the level, e.g. Recuperation's 181200101 at 14, rec 145442 #9941).
        var levelled = level > 1 ? ClientSkills.AbnormalOf(abnormalId) : null;
        var group = info?.Group ?? abnormalId;
        // Applied again (same SkillAbnormal group): the official server keeps the uid and sends 0x382b AbnormalChange_NT with the
        // new id / duration / expire time / skill (32,294 official samples; never a remove + add, no reason 4 in any recording).
        var previous = buffs.FirstOrDefault(b => b.Group == group);
        replies.AddRange(RemoveBuffs(buffs.Where(b => b.Group == group && b != previous).ToList(), ExpireReplaced));
        if (previous is not null) buffs.Remove(previous);
        var permanent = durationMs < 0;
        // A buff restored after a logout keeps its duration and ends after the time it had left (GameSession.LogoutState.cs).
        var duration = permanent ? 0 : remainingMs ?? durationMs;
        var buff = new Buff
        {
            Uid = previous?.Uid ?? ++buffUid, AbnormalId = abnormalId, SkillId = skillId, Group = group, DurationMs = permanent ? -1 : durationMs,
            ExpireAt = permanent ? PassiveExpireTime : now + duration, Scripted = scripted, CasterKey = caster?.Key ?? 0,
        };
        foreach (var (id, type, baseValues) in info?.Effects ?? [])
        {
            var values = levelled?.Effects.FirstOrDefault(e => e.Id == id) is { } atLevel ? ClientSkills.ValuesAt(atLevel, level) : baseValues;
            int V(int i) => i < values.Count && int.TryParse(values[i], out var v) ? v : 0;
            switch (type)
            {
                case "UseRebirth":
                    buff.Rebirth = true;
                    break;
                case "Polymorph" or "PetPolymorph":
                    buff.Polymorph = true;
                    break;
                case "StatChange" when values.Count >= 2:
                    buff.Stats[values[0]] = buff.Stats.GetValueOrDefault(values[0]) + V(1);
                    break;
                // Hot_Item [interval, heal min, max, ...]: the heal per tick (CANDIDATE: the official totals differ by player).
                case "Hot_Item" when V(0) > 0:
                    buff.Heals.Add(Heal(id, V(0), rng.Next(Math.Min(V(1), V(2)), Math.Max(V(1), V(2)) + 1), duration, now, perTick: true));
                    break;
                // HotRatio_Item [interval, ratio min, max (1/10000 of max HP in total), cap, ...] (OBSERVED: 5% in total).
                case "HotRatio_Item" when V(0) > 0:
                    buff.Heals.Add(Heal(id, V(0), (int)Math.Min(V(3) > 0 ? V(3) : int.MaxValue, (long)resources.HpMax * V(1) / 10_000), duration, now, perTick: false));
                    break;
                // The recovery orbs' Hot/Mot/Sot/Oot_Ratio [interval, ratio min, max (1/10000 of the maximum in total), cap, ...]: HP, MP,
                // stamina, diving gauge. OBSERVED (rec 105448, 800 ms abnormals, one tick): HP 4937 and MP 1712 (= the bars' maxima),
                // stamina 99999 (the cap).
                case "Hot_Ratio" or "Mot_Ratio" or "Sot_Ratio" or "Oot_Ratio" when V(0) > 0:
                {
                    var (point, max) = type switch
                    {
                        "Mot_Ratio" => (CharPoint.Mp, resources.MpMax),
                        "Sot_Ratio" => (CharPoint.Sp, resources.SpMax),
                        "Oot_Ratio" => (CharPoint.Op, OpMax),
                        _ => (CharPoint.Hp, resources.HpMax),
                    };
                    var total = (int)Math.Min(V(3) > 0 ? V(3) : int.MaxValue, (long)max * V(1) / 10_000);
                    var row = Heal(id, V(0), total, duration, now, perTick: false);
                    buff.Heals.Add(new HealOverTime { EffectId = row.EffectId, IntervalMs = row.IntervalMs, PerTick = row.PerTick, Remaining = row.Remaining, NextAt = row.NextAt, Point = point });
                    break;
                }
                // Fot [interval, amount min, max, cap, flag, ...]: flight points (the flight rings' 19000065 / 19000063, the Od bubbles'
                // 19000019-21, the FP orb's 19000028). OBSERVED (rec 20261107-012837, 19000065 for 100 ms): one tick 100 ms after the add,
                // 0x3805 mask 2 with the table amount (100000) although the gauge took less (185500 -> 250000, its maximum), then the
                // removal and 0x8d00 FP. The amount is FP itself (CANDIDATE for rows lasting several ticks: the total, as the ratio rows).
                case "Fot" when V(0) > 0:
                {
                    var row = Heal(id, V(0), rng.Next(Math.Min(V(1), V(2)), Math.Max(V(1), V(2)) + 1), duration, now, perTick: false);
                    buff.Heals.Add(new HealOverTime { EffectId = row.EffectId, IntervalMs = row.IntervalMs, PerTick = row.PerTick, Remaining = row.Remaining, NextAt = row.NextAt, Point = CharPoint.Fp });
                    break;
                }
                // A monster's damage over time (Dot_NormalCalc [interval, ..., flat min, max, ratio min, max]): ticks on the player as
                // 0x3805 mask 0x0a (OFFICIAL: 2500016's 12000151 every 2000 ms for 81, rec 115817 #97270; the same row on level 16 /
                // 27 monsters ticked 40-81 / 190). The tick's size: NpcDotTick (CANDIDATE fit of those samples).
                case "Dot_NormalCalc" when caster is not null && V(0) > 0:
                    buff.Dots.Add(new DamageOverTime { EffectId = id, IntervalMs = V(0), PerTick = NpcDotTick(caster, V(3), V(4), V(5), V(6)), NextAt = now + V(0) });
                    break;
                // ChangeRelationshipEntity [flag, relationship, flag]: the player's relationship while the buff lasts (the transformations' PC_FriendlyToAll).
                case "ChangeRelationshipEntity" when values.Count >= 2 && MapEventTables.Relationship(values[1]) is { } relation:
                    buff.Relationship = relation;
                    break;
            }
        }
        if (level > 1) buff.Level = level;
        setup?.Invoke(buff);   // the caller's own level, stats and times (the rank reward buffs)
        buffs.Add(buff);
        var packets = new List<byte[]> { previous is null ? PlayerPackets.AbnormalAdd(playerKey, [Wire(buff)]) : PlayerPackets.AbnormalChange(playerKey, Wire(buff), AbnormalTable.Embedded.ShowChange(abnormalId)) };
        if (buff.Polymorph) packets.Add(RidePackets.PolymorphUpdate(playerKey, buff.Uid));
        else if (previous?.Polymorph == true && buffs.All(b => !b.Polymorph)) packets.Add(RidePackets.PolymorphUpdate(playerKey, 0));
        if (buff.Relationship is { } shown) packets.Add(GamePackets.RelationshipUpdate(playerKey, shown));
        else if (previous?.Relationship is not null && buffs.All(b => b.Relationship is null)) packets.Add(GamePackets.RelationshipUpdate(playerKey, OwnRelationship));
        worldPlayer?.Map?.Broadcast(worldPlayer, packets);
        replies.Add(packets[0]);
        // A skill-less buff (a recovery orb's AddAbnormal) announces only its HP row, without the skill (OBSERVED rec 105448 #54190: the
        // HP orb's 0x3805 mask 1; the MP and stamina orbs' none).
        foreach (var h in buff.Heals)
            if (skillId != 0) replies.Add(RidePackets.AbnormalEffect(playerKey, buff.CasterKey != 0 ? buff.CasterKey : playerKey, buff.Uid, h.EffectId, h.Remaining, null, skillId));
            else if (h.Point == CharPoint.Hp) replies.Add(EnvBoxPackets.AbnormalHeal(playerKey, playerKey, buff.Uid, h.EffectId, h.Remaining, null));
        replies.AddRange(packets.Skip(1));
        return buff;
    }

    /// <summary>AddAbnormal on the player from a map event script (duration in ms, -1 for the run).</summary>
    List<byte[]> AddScriptedBuff(int abnormalId, int durationMs, long now)
    {
        var replies = new List<byte[]>();
        var speedBefore = MoveSpeedBonus();
        if (AddBuff(abnormalId, durationMs, 0, now, replies, scripted: true) is null) return [];
        replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    /// <summary>RemoveAbnormal on the player (by abnormal id), or every scripted buff when the player leaves the map (null); reason 9 EventFunction.</summary>
    List<byte[]> RemoveScriptedBuffs(int? abnormalId)
    {
        var gone = buffs.Where(b => abnormalId is { } id ? b.AbnormalId == id : b.Scripted).ToList();
        if (gone.Count == 0) return [];
        var speedBefore = MoveSpeedBonus();
        return [.. RemoveBuffs(gone, ExpireEventFunction), .. BuffStatsChanged(speedBefore)];
    }

    /// <summary>A PC_FriendlyToAll relationship from a buff (the story's transformations): monsters neither notice nor keep chasing the player.</summary>
    public bool FriendlyToAll => buffs.Any(b => b.Relationship == MapEventTables.FriendlyToAll);

    /// <summary>The player's own ERelationshipEntity (PC_Light_Common 1 / PC_Darkness_Common 2).</summary>
    int OwnRelationship => profileRace == 2 ? 2 : 1;

    static HealOverTime Heal(int effectId, int interval, int amount, int duration, long now, bool perTick)
    {
        var ticks = Math.Max(1, duration / interval);
        var per = perTick ? amount : amount / ticks;
        return new HealOverTime { EffectId = effectId, IntervalMs = interval, PerTick = per, Remaining = per * ticks, NextAt = now + interval };
    }

    /// <summary>Heal ticks and expiries due now.</summary>
    List<byte[]> BuffTick(long now)
    {
        if (buffs.Count == 0 || resources is not { } r) return [];
        var replies = new List<byte[]>();
        var fpBefore = r.Fp;
        foreach (var b in buffs)
        {
            var healed = false;
            var mpBefore = r.Mp;
            foreach (var h in b.Heals)
                while (now >= h.NextAt && h.NextAt <= b.ExpireAt && h.Remaining > 0)
                {
                    var amount = Math.Min(h.PerTick, h.Remaining);
                    h.Remaining -= amount;
                    h.NextAt += h.IntervalMs;
                    // A recovery orb's MP / stamina / diving gauge row (skill-less): the amount as the table gives it (OBSERVED 1712 and
                    // 99999), the bar capped at its maximum; MP goes out as 0x8d00 when it changed (OBSERVED #54376), stamina and the
                    // gauge do not (none OBSERVED after the stamina orb's tick #53933).
                    if (h.Point != CharPoint.Hp)
                    {
                        switch (h.Point)
                        {
                            case CharPoint.Mp: r.Mp = Math.Min(r.MpMax, r.Mp + amount); break;
                            case CharPoint.Sp: r.Sp = Math.Min(r.SpMax, r.Sp + amount); break;
                            case CharPoint.Op: r.Op = Math.Min(OpMax, r.Op + amount); break;
                            case CharPoint.Fp: r.Fp = Math.Min(r.FpMax, r.Fp + amount); break;
                        }
                        replies.Add(EnvBoxPackets.AbnormalHeal(playerKey, playerKey, b.Uid, h.EffectId, h.Remaining, amount));
                        continue;
                    }
                    amount = HealReceived(amount);   // incoming heal bonus (the tick's remaining pool is unchanged)
                    var hpBefore = r.Hp;
                    r.Hp = Math.Min(r.HpMax, r.Hp + amount);
                    if (b.SkillId == 0) replies.Add(EnvBoxPackets.AbnormalHeal(playerKey, playerKey, b.Uid, h.EffectId, h.Remaining, amount));
                    else
                    {
                        // A skill's heal over time names its healer and is seen nearby (OBSERVED: other players' Hot ticks, rec 115817 #125196).
                        var tick = RidePackets.AbnormalEffect(playerKey, b.CasterKey != 0 ? b.CasterKey : playerKey, b.Uid, h.EffectId, h.Remaining, amount, b.SkillId);
                        if (b.SkillId is >= 11_000_000 and < 20_000_000) worldPlayer?.Map?.Broadcast(worldPlayer, [tick]);   // class skills only
                        replies.Add(tick);
                    }
                    // A skill-less heal on a full bar sends no HP (OBSERVED: the HP orb's tick #54374 at full HP, no 0x8d00 HP).
                    healed |= b.SkillId != 0 || r.Hp != hpBefore;
                }
            if (healed) replies.Add(GamePackets.NpcHp(playerKey, r.Hp));
            if (r.Mp != mpBefore) replies.Add(GamePackets.CharPoints(playerKey, [(CharPoint.Mp, r.Mp)]));
            var hurt = false;
            foreach (var d in b.Dots)
                while (!dead && now >= d.NextAt && d.NextAt <= b.ExpireAt)
                {
                    d.NextAt += d.IntervalMs;
                    if (!invincible.Hp) r.Hp = Math.Max(0, r.Hp - d.PerTick);
                    var tick = RidePackets.AbnormalDamage(playerKey, b.CasterKey, b.Uid, d.EffectId, d.PerTick, b.SkillId);
                    worldPlayer?.Map?.Broadcast(worldPlayer, [tick]);
                    replies.Add(tick);
                    hurt = true;
                    if (r.Hp == 0) { dotKiller = b.CasterKey; break; }
                }
            if (hurt)
            {
                replies.Add(GamePackets.NpcHp(playerKey, r.Hp));
                if (!inCombat) { inCombat = true; replies.Add(GamePackets.CombatState(playerKey, true)); }
                combatUntil = Math.Max(combatUntil, now + CombatTimeoutMs);
            }
        }
        if (dotKiller is { } killer && !dead && r.Hp == 0)
        {
            dotKiller = null;
            replies.AddRange(Die(killer, now));
            return replies;
        }
        var speedBefore = MoveSpeedBonus();
        var expired = buffs.Where(b => now >= b.ExpireAt).ToList();
        if (expired.Count > 0)
        {
            replies.AddRange(RemoveBuffs(expired, ExpireTimeout));
            // Only buffs that changed stats refresh the sheet (OBSERVED: the flight ring's Fot 19000065 ended with 0x382c alone, its
            // FlySpeed/FlyCost 19000064 with 0x382c and 0x364a {214 = 0, 247 = 0}).
            if (expired.Any(b => b.Stats.Count > 0)) replies.AddRange(BuffStatsChanged(speedBefore, expired));
            // Only buffs that ran out (not replaced, not cleared at death) re-arm a quickbar auto-use item.
            foreach (var b in expired) if (b.SkillId != 0 && !b.Scripted) expiredItemSkills.Add(b.SkillId);
        }
        // The flight points a Fot tick restored, after the removal (OBSERVED order: 0x3805, 0x382c, 0x8d00 FP).
        if (r.Fp != fpBefore) replies.Add(r.FpUpdate());
        return replies;
    }

    int? dotKiller;

    /// <summary>
    /// A monster DoT's damage per tick: flat + the monster's hit base (its level's HP x NpcAi.DamagePerHp x its rank's scale, as its
    /// skill hits) x the ratio x DotRatioScale. CANDIDATE: DotRatioScale fitted to the official ticks of the 30000-ratio rows
    /// (12000141 / 12000151: level 16 monsters 40-81 per tick, level 27 190; base 229 / 637 -> about 0.11 x 3.0 of the base).
    /// </summary>
    internal static int NpcDotTick(Target caster, int flatMin, int flatMax, int ratioMin, int ratioMax)
    {
        var flat = (Math.Min(flatMin, flatMax) + Math.Max(flatMin, flatMax)) / 2.0;
        var ratio = (Math.Min(ratioMin, ratioMax) + Math.Max(ratioMin, ratioMax)) / 2.0 / 10_000;
        var hitBase = Combat.HpForLevel(caster.Level) * NpcAi.DamagePerHp * caster.DamageScale;
        return Math.Max(1, (int)Math.Round(flat + hitBase * ratio * DotRatioScale));
    }

    public const double DotRatioScale = 0.11;

    /// <summary>Removes buffs: 0x382c to the player and those nearby (stat updates are the caller's).</summary>
    List<byte[]> RemoveBuffs(IReadOnlyList<Buff> removed, byte reason)
    {
        if (removed.Count == 0) return [];
        foreach (var b in removed) buffs.Remove(b);
        var remove = PlayerPackets.AbnormalRemove(playerKey, removed.Select(b => (b.Uid, reason)).ToList());
        var packets = new List<byte[]> { remove };
        // A transformation ending: back to the own form (polymorph uid 0) and the own relationship.
        if (removed.Any(b => b.Polymorph) && buffs.All(b => !b.Polymorph)) packets.Add(RidePackets.PolymorphUpdate(playerKey, 0));
        if (removed.Any(b => b.Relationship is not null) && buffs.All(b => b.Relationship is null)) packets.Add(GamePackets.RelationshipUpdate(playerKey, OwnRelationship));
        worldPlayer?.Map?.Broadcast(worldPlayer, packets);
        return packets;
    }

    /// <summary>Every buff ends at death except a rebirth buff (it revives in place) and the rank reward buffs (they last the season,
    /// CANDIDATE): removal, then the stats without them.</summary>
    List<byte[]> ClearBuffs()
    {
        var speedBefore = MoveSpeedBonus();
        return [.. RemoveBuffs(buffs.Where(b => !b.Rebirth && !b.Rank).ToList(), ExpireEtc), .. BuffStatsChanged(speedBefore)];
    }

    public bool CanRebirth => buffs.Any(b => b.Rebirth);

    /// <summary>The rebirth buff is used up (revive in place).</summary>
    List<byte[]> UseRebirthBuff() => RemoveBuffs(buffs.Where(b => b.Rebirth).ToList(), ExpireEtc);

    /// <summary>An item whose skill gives a UseRebirth buff (Resurrection Spiritstone).</summary>
    static bool IsRebirthItem(int itemId) =>
        ItemDB.Embedded.Of(itemId) is { UsableCategory: "Rebirth" }
        || Boxes.Use(itemId) is { Effect: "Skill", Values: [var skill, ..] } && int.TryParse(skill, out var skillId)
            && AbnormalIds(Boxes.SkillEffects(skillId)).Any(id => Boxes.Abnormal(id)?.Effects.Any(e => e.Type == "UseRebirth") == true);

    /// <summary>
    /// Stats that only the stat sheet carries: a buff changing nothing else (a flight ring's 19000064: FlySpeed, FlyCost) refreshes
    /// with 0x364a alone - OBSERVED (rec 20261107-012837): no 0x3656 combat power and no maxima after the ring's 0x364a, and an
    /// empty 0x364a when the ring was flown through again (nothing changed).
    /// </summary>
    static readonly HashSet<string> SheetOnlyStats = new(StringComparer.OrdinalIgnoreCase) { "FlySpeed", "FlyCost" };

    /// <summary>The changed buffs' stats: 0x364a alone when they change only sheet-only stats, else the whole refresh.</summary>
    List<byte[]> BuffStatsChanged(int speedBefore, IReadOnlyCollection<Buff> changed)
    {
        if (resources is not null && changed.Any(b => b.Stats.Count > 0) && changed.All(b => b.Stats.Keys.All(SheetOnlyStats.Contains)))
        {
            var (stats, hpMax) = StatSheet();
            return [StatsUpdate(stats, hpMax)];
        }
        return BuffStatsChanged(speedBefore);
    }

    /// <summary>
    /// A world buff passed through (WorldBuffs: a flight ring, an Od bubble, a recovery or buff orb): its EventFunc abnormals on the
    /// player (0x382a, or 0x382b for one already on), then the stats they change.
    /// </summary>
    List<byte[]> ApplyWorldBuffs(IReadOnlyList<WorldBuffs.Effect> effects, long now)
    {
        if (effects.Count == 0 || resources is null) return [];
        var replies = new List<byte[]>();
        var speedBefore = MoveSpeedBonus();
        var added = new List<Buff>();
        foreach (var e in effects)
            if (AddBuff(e.Abnormal, e.DurationMs, 0, now, replies) is { } buff) added.Add(buff);
        replies.AddRange(BuffStatsChanged(speedBefore, added));
        return replies;
    }

    /// <summary>Checks only: the character ascends (the default wing, a full flight gauge) and its gauge is set.</summary>
    internal List<byte[]> AscendForChecks(int fp)
    {
        var replies = Ascend(2);
        replies.AddRange(RefreshStats());
        if (resources is not null) resources.Fp = Math.Clamp(fp, 0, resources.FpMax);
        return replies;
    }

    /// <summary>The stat sheet with the buffs (when they change stats) and 0x364b when the move speed changed.</summary>
    List<byte[]> BuffStatsChanged(int speedBefore)
    {
        var replies = RefreshStats();
        var speed = MoveSpeedBonus();
        if (speed != speedBefore) replies.Add(PlayerPackets.MoveSpeedUpdate(playerKey, [(MoveSpeedStat, speed)]));
        return replies;
    }

    /// <summary>Passive abnormals already announced (by abnormal id), with their uids.</summary>
    readonly Dictionary<int, int> passiveUids = [];
    const int FirstPassiveUid = 1_000_000;
    /// <summary>The official passive's expire time (2100-01-01 00:00 KST, ms): it never runs out.</summary>
    const long PassiveExpireTime = 4_102_412_400_000;

    /// <summary>
    /// 0x382a for the learned skills' passive abnormals not announced yet (OBSERVED at the official world entry:
    /// abnormal 110000001, duration -1, expire 2100, caster self, level 1, range base). The client equips a weapon
    /// category only while a WeaponEquipable passive allows it (live 2026-10-16: "Cannot be equipped by this class"
    /// for a Gladiator's greatsword without it).
    /// </summary>
    List<byte[]> PassiveAbnormals(bool resend = false)
    {
        if (resend) passiveUids.Clear();
        var fresh = new List<AbnormalWire>();
        foreach (var skill in learned.Order())
            foreach (var abnormal in Boxes.Passives(skill))
            {
                if (passiveUids.ContainsKey(abnormal)) continue;
                var uid = passiveUids[abnormal] = FirstPassiveUid + passiveUids.Count;
                fresh.Add(new AbnormalWire(uid, abnormal, -1, PassiveExpireTime, CasterKey: playerKey, Level: 1, RangeBase: position ?? Vector3d.Zero));
            }
        return fresh.Count == 0 ? [] : [PlayerPackets.AbnormalAdd(playerKey, fresh)];
    }

    /// <summary>The active buffs as other players' 0x3645 carries them.</summary>
    public IReadOnlyList<AbnormalWire> BuffWires() => buffs.Select(Wire).Concat(GearPassiveWires()).ToList();

    /// <summary>After a map load the client draws the player anew: the active buffs and the speed again.</summary>
    List<byte[]> BuffsAfterLoad() =>
        [.. buffs.Count == 0 ? Array.Empty<byte[]>() : [PlayerPackets.AbnormalAdd(playerKey, buffs.Select(Wire).ToList())],
            .. MoveSpeedBonus() is var speed and not 0 ? [PlayerPackets.MoveSpeedUpdate(playerKey, [(MoveSpeedStat, speed)])] : Array.Empty<byte[]>()];

    /// <summary>
    /// The move speed bonus from every source (buffs, titles, gear, soul binds, collections, the creature board; 0.01% units), the
    /// value of 0x364b stat 205 (user 2026-11-08: title effects such as Move Speed did nothing, only buffs counted).
    /// </summary>
    int MoveSpeedBonus() => BuffStat("MoveSpeed") + TitleStat("MoveSpeed") + ExtraStat("MoveSpeed") + BoardStat("MoveSpeed") + RideStat("MoveSpeed")
        + EquippedItems().Sum(i => i.Stat("MoveSpeed"));

    /// <summary>The move speed last told to the client (RefreshStats resends it when a title, gear or collection changed it).</summary>
    int sentMoveSpeed;
}
