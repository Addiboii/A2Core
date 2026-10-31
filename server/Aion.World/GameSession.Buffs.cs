using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

// Item buffs: the Abnormal_True effects of item skills (potions over time, food, drinks, scrolls), with
// the SkillAbnormal / SkillAbnormalEffect rows from tools/data/boxes.json. Wire as the official Life
// Potion and speed scroll uses (capture 20261013-144836): 0x382a AbnormalAdd (expire time = server
// clock at expiry), 0x3805 per heal-over-time row at once (remaining total) and on every tick (remaining,
// healed) with the HP as 0x8d00, 0x382c AbnormalRemove reason 1 at expiry, 0x364b MoveSpeedUpdate
// {205, summed MoveSpeed} for speed buffs. StatChange stats join the stat sheet (0x364a, CANDIDATE: no
// food sample). One buff per SkillAbnormal group: a new one replaces the old (remove reason 4, CANDIDATE).
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
        /// <summary>A carry box's item (the BombBox's bomb): an EnvObj_Destroy object of its map takes it.</summary>
        public bool Carried { get; set; }
        /// <summary>Casts of the AbnormalClear SkillStart skill so far.</summary>
        public int ChargesUsed { get; set; }
        /// <summary>A ChangeRelationshipEntity effect: the relationship the player shows while it lasts (PC_FriendlyToAll: monsters ignore them).</summary>
        public int? Relationship { get; set; }
        /// <summary>Barrier, reflect, immunity, CC and DoT rows the server applies in combat.</summary>
        public AbnormalCombat.BuffCombat? Combat { get; set; }
        public Dictionary<string, int> Stats { get; } = [];
        public List<HealOverTime> Heals { get; } = [];
    }

    /// <summary>A heal-over-time row: interval, amount per tick, what is left of the total.</summary>
    sealed class HealOverTime
    {
        public required int EffectId, IntervalMs, PerTick;
        public required int Remaining;
        public required long NextAt;
    }

    readonly List<Buff> buffs = [];
    int buffUid;
    public const int MoveSpeedStat = 205;
    const byte ExpireTimeout = 1, ExpireReplaced = 4, ExpireDispel = 3, ExpireEtc = 7, ExpireEventFunction = 9;
    const string ActiveBuffsSetting = "active_buffs", ItemCooldownsSetting = "item_cooldowns";
    bool buffSettingsDirty;
    long buffSettingsFlushAt;

    public IReadOnlyList<int> ActiveBuffs => buffs.Select(b => b.AbnormalId).ToList();

    /// <summary>A stat summed over the active buffs (StatChange / MoveSpeed rows).</summary>
    int BuffStat(string stat) => buffs.Sum(b => b.Stats.GetValueOrDefault(stat));

    static IEnumerable<int> AbnormalIds(IReadOnlyList<SkillEffectRow> effects) =>
        effects.Where(e => e.Type == "Abnormal_True" && e.Value(2) > 0).Select(e => e.Value(2));

    /// <summary>Whether an item skill's buffs are known (their effects the server does not model only show the icon).</summary>
    static bool ItemAbnormalsSupported(IReadOnlyList<SkillEffectRow> effects) =>
        AbnormalIds(effects).Any() && AbnormalIds(effects).All(id => Boxes.Abnormal(id) is not null && !HasUnsupportedAbnormalEffect(id));

    static bool HasUnsupportedAbnormalEffect(int abnormalId)
    {
        foreach (var (_, type, _) in Boxes.Abnormal(abnormalId)?.Effects ?? [])
            if (type is not ("UseRebirth" or "Polymorph" or "PetPolymorph" or "StatChange" or "Hot_Item" or "HotRatio_Item" or "ChangeRelationshipEntity"
                or "HpBarrier" or "ImmuneAbnormal" or "ImmuneBind" or "DamageReflect" or "Stun" or "Sleep" or "Fear" or "Silence" or "Bind" or "Stone"
                or "Paralysis" or "Blind" or "Hold" or "Root" or "BlockMovePosition" or "Dot_TargetHP" or "Dot_TargetMaxHP" or "ImmuneDecreaseHP" or "Invincible"
                or "Blockade" or "Aerial" or "Knockdown" or "ImmuneSkillEffectGroup"))
                return true;
        return false;
    }

    /// <summary>Item skills the server can apply: heals, known abnormals, dispel scrolls, or NoEffect placeholders (exp boosters).</summary>
    static bool ItemSkillSupported(IReadOnlyList<SkillEffectRow> effects)
    {
        if (effects.Count == 0) return false;
        foreach (var e in effects)
        {
            switch (e.Type)
            {
                case "HpHeal_Item" or "HpHealRatio_Item" or "MpHeal_Item" or "FpHeal_Item" or "Dispel_True" or "NoEffect":
                    return true;
                case "Abnormal_True" when e.Value(2) > 0:
                    if (Boxes.Abnormal(e.Value(2)) is null) return false;
                    break;
                case "CasterSkill":
                    break;
                default:
                    return false;
            }
        }
        return AbnormalIds(effects).Any();
    }

    AbnormalWire Wire(Buff b) => new(b.Uid, b.AbnormalId, b.DurationMs, b.ExpireAt, CasterKey: playerKey, Level: 1, SkillId: b.SkillId == 0 ? null : b.SkillId, RangeBase: position ?? Vector3d.Zero);

    /// <summary>Adds an item skill's buffs: 0x382a, the first 0x3805 per heal row, stat and speed updates.</summary>
    List<byte[]> ApplyItemAbnormals(int skillId, IReadOnlyList<SkillEffectRow> effects, long now)
    {
        var replies = new List<byte[]>();
        var speedBefore = BuffStat("MoveSpeed");
        foreach (var e in effects.Where(e => e.Type == "Abnormal_True"))
        {
            var duration = Math.Max(1, rng.Next(Math.Min(e.Value(0), e.Value(1)), Math.Max(e.Value(0), e.Value(1)) + 1));
            AddBuff(e.Value(2), duration, skillId, now, replies, path: SelfAbnormalPath.Item);
        }
        replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    /// <summary>
    /// One buff on the player: its SkillAbnormal effects the server models, 0x382a to the player and those nearby, the first
    /// 0x3805 per heal row, the polymorph and relationship updates. A duration below 0 lasts (the official passive's expire time);
    /// an abnormal the tables do not know only shows its icon. The caller refreshes the stats.
    /// </summary>
    Buff? AddBuff(int abnormalId, int durationMs, int skillId, long now, List<byte[]> replies, bool scripted = false,
        SelfAbnormalPath path = SelfAbnormalPath.Unrestricted)
    {
        if (resources is null) return null;
        if (!SkillSelfAbnormalPolicy.TryAllowPlayerBuff(data, abnormalId, skillId, path, out var denyReason))
        {
            PlaytestDebug.Action("self_abnormal", ("allowed", false), ("abnormalId", abnormalId), ("skillId", skillId),
                ("path", path.ToString()), ("reason", denyReason ?? ""));
            return null;
        }
        PlaytestDebug.Action("self_abnormal", ("allowed", true), ("abnormalId", abnormalId), ("skillId", skillId),
            ("path", path.ToString()));
        var rule = AbnormalStackRules.For(abnormalId);
        var group = rule.Group;
        var info = AbnormalSpecs.Info(abnormalId);
        var permanent = durationMs < 0;
        var duration = permanent ? 0 : durationMs;
        var existingSame = buffs.Where(b => b.AbnormalId == abnormalId).ToList();
        if (existingSame.Count > 0 && rule.OverlapTime is not "Separate")
        {
            var existing = existingSame[0];
            existing.ExpireAt = rule.OverlapTime == "Longer" ? Math.Max(existing.ExpireAt, now + durationMs) : now + durationMs;
            existing.DurationMs = permanent ? -1 : (int)Math.Max(0, existing.ExpireAt - now);
            MarkBuffSettingsDirty();
            replies.Add(PlayerPackets.AbnormalChange(playerKey, Wire(existing)));
            return existing;
        }
        if (existingSame.Count > 0 && rule.OverlapTime == "Separate" && existingSame.Count >= Math.Max(1, rule.OverlapCount))
        {
            var existing = existingSame[0];
            existing.ExpireAt = now + durationMs;
            existing.DurationMs = duration;
            MarkBuffSettingsDirty();
            replies.Add(PlayerPackets.AbnormalChange(playerKey, Wire(existing)));
            return existing;
        }
        var sameGroup = buffs.Where(b => b.Group == group && b.AbnormalId != abnormalId).ToList();
        if (sameGroup.Count > 0)
            replies.AddRange(RemoveBuffs(sameGroup, ExpireReplaced));
        var buff = new Buff
        {
            Uid = ++buffUid, AbnormalId = abnormalId, SkillId = skillId, Group = group, DurationMs = permanent ? -1 : durationMs,
            ExpireAt = permanent ? PassiveExpireTime : now + durationMs, Scripted = scripted,
        };
        AbnormalCombat.BuffCombat? combat = info is not null ? new AbnormalCombat.BuffCombat() : null;
        foreach (var (id, type, values) in info?.Effects ?? [])
        {
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
                // Slow [1/10000 of the move speed]: the MoveSpeed stat falls (0x364b tells the client).
                case "Slow":
                    buff.Stats["MoveSpeed"] = buff.Stats.GetValueOrDefault("MoveSpeed") - V(0);
                    break;
                // Hot_Item [interval, heal min, max, ...]: the heal per tick (CANDIDATE: the official totals differ by player).
                case "Hot_Item" or "Hot" when V(0) > 0:
                    buff.Heals.Add(Heal(id, V(0), rng.Next(Math.Min(V(1), V(2)), Math.Max(V(1), V(2)) + 1), duration, now, perTick: true));
                    break;
                // HotRatio_Item [interval, ratio min, max (1/10000 of max HP in total), cap, ...] (OBSERVED: 5% in total).
                case "HotRatio_Item" or "Hot_Ratio" when V(0) > 0:
                    buff.Heals.Add(Heal(id, V(0), (int)Math.Min(V(3) > 0 ? V(3) : int.MaxValue, (long)resources.HpMax * V(1) / 10_000), duration, now, perTick: false));
                    break;
                // ChangeRelationshipEntity [flag, relationship, flag]: the player's relationship while the buff lasts (the transformations' PC_FriendlyToAll).
                case "ChangeRelationshipEntity" when values.Count >= 2 && MapEventTables.Relationship(values[1]) is { } relation:
                    buff.Relationship = relation;
                    break;
                default:
                    if (combat is not null) AbnormalCombat.ApplyRow(id, type, values, combat, duration, now, rng, resources.HpMax);
                    break;
            }
        }
        buff.Combat = combat;
        if (buff.Combat is { BarrierAmount: > 0 })
            replies.Add(GamePackets.CharBarrierInfo(playerKey, [(buff.Uid, buff.Combat.BarrierAmount)]));
        buffs.Add(buff);
        MarkBuffSettingsDirty();
        var packets = new List<byte[]> { PlayerPackets.AbnormalAdd(playerKey, [Wire(buff)]) };
        if (buff.Polymorph) packets.Add(RidePackets.PolymorphUpdate(playerKey, buff.Uid));
        if (buff.Relationship is { } shown) packets.Add(GamePackets.RelationshipUpdate(playerKey, shown));
        worldPlayer?.Map?.Broadcast(worldPlayer, packets);
        replies.Add(packets[0]);
        foreach (var h in buff.Heals) replies.Add(RidePackets.AbnormalEffect(playerKey, playerKey, buff.Uid, h.EffectId, h.Remaining, null, skillId));
        replies.AddRange(packets.Skip(1));
        return buff;
    }

    /// <summary>AddAbnormal on the player from a map event script (duration in ms, -1 for the run).</summary>
    List<byte[]> AddScriptedBuff(int abnormalId, int durationMs, long now)
    {
        var replies = new List<byte[]>();
        var speedBefore = BuffStat("MoveSpeed");
        if (AddBuff(abnormalId, durationMs, 0, now, replies, scripted: true, path: SelfAbnormalPath.Scripted) is null) return [];
        replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    /// <summary>RemoveAbnormal on the player (by abnormal id), or every scripted buff when the player leaves the map (null); reason 9 EventFunction.</summary>
    List<byte[]> RemoveScriptedBuffs(int? abnormalId)
    {
        var gone = buffs.Where(b => abnormalId is { } id ? b.AbnormalId == id : b.Scripted).ToList();
        if (gone.Count == 0) return [];
        var speedBefore = BuffStat("MoveSpeed");
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
        foreach (var b in buffs)
        {
            var healed = false;
            var damaged = false;
            if (b.Combat?.Dots is { Count: > 0 } dots)
                foreach (var d in dots)
                    while (now >= d.NextAt && d.NextAt <= b.ExpireAt && d.Remaining > 0)
                    {
                        var amount = Math.Min(d.PerTick, d.Remaining);
                        d.Remaining -= amount;
                        d.NextAt += d.IntervalMs;
                        if (!invincible.Hp) r.Hp = Math.Max(0, r.Hp - amount);
                        replies.Add(RidePackets.AbnormalEffect(playerKey, playerKey, b.Uid, d.EffectId, d.Remaining, amount, b.SkillId));
                        damaged = true;
                    }
            foreach (var h in b.Heals)
                while (now >= h.NextAt && h.NextAt <= b.ExpireAt && h.Remaining > 0)
                {
                    var amount = Math.Min(h.PerTick, h.Remaining);
                    h.Remaining -= amount;
                    h.NextAt += h.IntervalMs;
                    r.Hp = Math.Min(r.HpMax, r.Hp + amount);
                    replies.Add(RidePackets.AbnormalEffect(playerKey, playerKey, b.Uid, h.EffectId, h.Remaining, amount, b.SkillId));
                    healed = true;
                }
            if (healed || damaged) replies.Add(GamePackets.NpcHp(playerKey, r.Hp));
            if (damaged && r.Hp == 0) replies.AddRange(Die(0, now));
        }
        var speedBefore = BuffStat("MoveSpeed");
        var expired = buffs.Where(b => now >= b.ExpireAt).ToList();
        if (expired.Count > 0)
        {
            replies.AddRange(RemoveBuffs(expired, ExpireTimeout));
            replies.AddRange(BuffStatsChanged(speedBefore));
        }
        return replies;
    }

    /// <summary>Dispel scroll (Dispel_True DeBuff): removes debuff abnormals on the player (CANDIDATE level filter).</summary>
    List<byte[]> DispelDebuffs(long now)
    {
        var speedBefore = BuffStat("MoveSpeed");
        var gone = buffs.Where(b => !b.Rebirth && !b.Scripted && Boxes.Debuff(b.AbnormalId)).ToList();
        if (gone.Count == 0) return [];
        return [.. RemoveBuffs(gone, ExpireDispel), .. BuffStatsChanged(speedBefore)];
    }

    /// <summary>Removes buffs: 0x382c to the player and those nearby (stat updates are the caller's).</summary>
    List<byte[]> RemoveBuffs(IReadOnlyList<Buff> removed, byte reason)
    {
        if (removed.Count == 0) return [];
        foreach (var b in removed) buffs.Remove(b);
        MarkBuffSettingsDirty();
        var remove = PlayerPackets.AbnormalRemove(playerKey, removed.Select(b => (b.Uid, reason)).ToList());
        var packets = new List<byte[]> { remove };
        // A transformation ending: back to the own form (polymorph uid 0) and the own relationship.
        if (removed.Any(b => b.Polymorph) && buffs.All(b => !b.Polymorph)) packets.Add(RidePackets.PolymorphUpdate(playerKey, 0));
        if (removed.Any(b => b.Relationship is not null) && buffs.All(b => b.Relationship is null)) packets.Add(GamePackets.RelationshipUpdate(playerKey, OwnRelationship));
        worldPlayer?.Map?.Broadcast(worldPlayer, packets);
        return packets;
    }

    /// <summary>Every buff ends at death except a rebirth buff (it revives in place): removal, then the stats without them.</summary>
    List<byte[]> ClearBuffs()
    {
        var speedBefore = BuffStat("MoveSpeed");
        return [.. RemoveBuffs(buffs.Where(b => !b.Rebirth).ToList(), ExpireEtc), .. BuffStatsChanged(speedBefore)];
    }

    public bool CanRebirth => buffs.Any(b => b.Rebirth);

    /// <summary>The rebirth buff is used up (revive in place).</summary>
    List<byte[]> UseRebirthBuff() => RemoveBuffs(buffs.Where(b => b.Rebirth).ToList(), ExpireEtc);

    /// <summary>An item whose skill gives a UseRebirth buff (Resurrection Spiritstone).</summary>
    static bool IsRebirthItem(int itemId) =>
        Boxes.Use(itemId) is { Effect: "Skill", Values: [var skill, ..] } && int.TryParse(skill, out var skillId)
        && AbnormalIds(Boxes.SkillEffects(skillId)).Any(id => Boxes.Abnormal(id)?.Effects.Any(e => e.Type == "UseRebirth") == true);

    /// <summary>The stat sheet with the buffs (when they change stats) and 0x364b when the move speed changed.</summary>
    List<byte[]> BuffStatsChanged(int speedBefore)
    {
        var replies = RefreshStats();
        var speed = BuffStat("MoveSpeed");
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
    public IReadOnlyList<AbnormalWire> BuffWires() => buffs.Select(Wire).ToList();

    /// <summary>After a map load the client draws the player anew: the active buffs and the speed again.</summary>
    List<byte[]> BuffsAfterLoad() =>
        buffs.Count == 0 ? [] : [PlayerPackets.AbnormalAdd(playerKey, buffs.Select(Wire).ToList()),
            .. BuffStat("MoveSpeed") is var speed and not 0 ? [PlayerPackets.MoveSpeedUpdate(playerKey, [(MoveSpeedStat, speed)])] : Array.Empty<byte[]>()];

    void MarkBuffSettingsDirty() => buffSettingsDirty = true;

    /// <summary>Re-applies combat rows after relog when older saves lack combat snapshot fields.</summary>
    void RestoreCombatFromTables(Buff buff, long now)
    {
        if (resources is null || AbnormalSpecs.Info(buff.AbnormalId) is not { } info) return;
        var duration = buff.ExpireAt == PassiveExpireTime ? int.MaxValue : (int)Math.Max(0, buff.ExpireAt - now);
        var combat = new AbnormalCombat.BuffCombat();
        foreach (var (id, type, values) in info.Effects)
        {
            if (type is "UseRebirth" or "Polymorph" or "PetPolymorph" or "StatChange" or "Hot_Item" or "HotRatio_Item" or "Hot" or "Hot_Ratio" or "Slow" or "ChangeRelationshipEntity") continue;
            AbnormalCombat.ApplyRow(id, type, values, combat, duration, now, rng, resources.HpMax);
        }
        if (combat.BarrierAmount > 0 || combat.ReflectPermille > 0 || combat.Held || combat.Silence || combat.Rooted || combat.Taunt || combat.Dots.Count > 0 || combat.ImmuneEffectTypes.Count > 0 || combat.ImmuneDebuffCategory)
            buff.Combat = combat;
    }

    sealed record SavedHeal(int EffectId, int IntervalMs, int PerTick, int Remaining, long NextAt);
    sealed record SavedDot(int EffectId, int IntervalMs, int PerTick, int Remaining, long NextAt);
    sealed record SavedCombat(int BarrierAmount, int ReflectPermille, bool Held, bool Silence, bool Rooted = false);
    sealed record SavedBuff(int Uid, int AbnormalId, int SkillId, long ExpireAt, bool Rebirth, bool Polymorph, bool Scripted, int? Relationship,
        Dictionary<string, int> Stats, List<SavedHeal> Heals, SavedCombat? Combat, List<SavedDot> Dots);

    /// <summary>Restores item buffs and cooldown groups after character select (official food/speed persist across relog).</summary>
    void LoadBuffSettings(IReadOnlyDictionary<string, JsonElement> settings, long now)
    {
        buffs.Clear();
        buffUid = 0;
        itemCooldowns.Clear();
        try
        {
            if (settings.TryGetValue(ActiveBuffsSetting, out var raw) && raw.Deserialize<SavedBuff[]>() is { } rows)
            {
                foreach (var row in rows)
                {
                    if (row.ExpireAt <= now && row.ExpireAt != PassiveExpireTime) continue;
                    var info = AbnormalSpecs.Info(row.AbnormalId);
                    // A carried bomb stays with the map copy it was taken on: a relog drops it.
                    if (WorldObjects.IsCarryGroup(info?.Group ?? row.AbnormalId)) { MarkBuffSettingsDirty(); continue; }
                    var buff = new Buff
                    {
                        Uid = row.Uid, AbnormalId = row.AbnormalId, SkillId = row.SkillId, Group = AbnormalStackRules.GroupOf(row.AbnormalId),
                        DurationMs = row.ExpireAt == PassiveExpireTime ? -1 : Math.Max(0, row.ExpireAt - now),
                        ExpireAt = row.ExpireAt, Rebirth = row.Rebirth, Polymorph = row.Polymorph, Scripted = row.Scripted, Relationship = row.Relationship,
                    };
                    foreach (var (k, v) in row.Stats) buff.Stats[k] = v;
                    foreach (var h in row.Heals)
                        buff.Heals.Add(new HealOverTime { EffectId = h.EffectId, IntervalMs = h.IntervalMs, PerTick = h.PerTick, Remaining = h.Remaining, NextAt = h.NextAt });
                    if (row.Combat is { } sc)
                    {
                        buff.Combat = new AbnormalCombat.BuffCombat { BarrierAmount = sc.BarrierAmount, ReflectPermille = sc.ReflectPermille, Held = sc.Held, Silence = sc.Silence, Rooted = sc.Rooted };
                        foreach (var d in row.Dots ?? [])
                            buff.Combat.Dots.Add(new AbnormalCombat.DotRow { EffectId = d.EffectId, IntervalMs = d.IntervalMs, PerTick = d.PerTick, Remaining = d.Remaining, NextAt = d.NextAt });
                    }
                    else RestoreCombatFromTables(buff, now);
                    buffs.Add(buff);
                    buffUid = Math.Max(buffUid, row.Uid);
                }
            }
            if (settings.TryGetValue(ItemCooldownsSetting, out var cool) && cool.Deserialize<Dictionary<string, long>>() is { } groups)
                foreach (var (group, until) in groups)
                    if (until > now) itemCooldowns[group] = until;
        }
        catch (JsonException) { buffs.Clear(); itemCooldowns.Clear(); }
    }

    SavedBuff[] SerializeBuffs() => buffs.Select(b => new SavedBuff(b.Uid, b.AbnormalId, b.SkillId, b.ExpireAt, b.Rebirth, b.Polymorph, b.Scripted, b.Relationship,
        new Dictionary<string, int>(b.Stats), b.Heals.Select(h => new SavedHeal(h.EffectId, h.IntervalMs, h.PerTick, h.Remaining, h.NextAt)).ToList(),
        b.Combat is { } c ? new SavedCombat(c.BarrierAmount, c.ReflectPermille, c.Held, c.Silence, c.Rooted) : null,
        b.Combat?.Dots.Select(d => new SavedDot(d.EffectId, d.IntervalMs, d.PerTick, d.Remaining, d.NextAt)).ToList() ?? [])).ToArray();

    async Task FlushBuffSettingsAsync(long now, CancellationToken ct, bool force = false)
    {
        if (!buffSettingsDirty || Store is not { } store || CharacterId is not { } character || (!force && now < buffSettingsFlushAt)) return;
        buffSettingsFlushAt = now + 2000;
        buffSettingsDirty = false;
        await SaveSettingAsync(ActiveBuffsSetting, SerializeBuffs(), ct);
        await SaveSettingAsync(ItemCooldownsSetting, itemCooldowns.Where(kv => kv.Value > now).ToDictionary(kv => kv.Key, kv => kv.Value), ct);
    }

    void MarkItemCooldownDirty() => MarkBuffSettingsDirty();

    /// <summary>Oxygen Deficiency (abnormal 3000): no expiry until the gauge refills; 5% max HP per second (effect 30001).</summary>
    public const int OxygenDeficiencyAbnormal = 3000, OxygenDeficiencyEffect = 30001;

    List<byte[]> EnsureOxygenDeficiency(long now)
    {
        if (resources is null || buffs.Any(b => b.AbnormalId == OxygenDeficiencyAbnormal)) return [];
        var replies = new List<byte[]>();
        var perTick = Math.Max(1, resources.HpMax * SuffocationRatio / 10_000);
        var combat = new AbnormalCombat.BuffCombat();
        combat.Dots.Add(new AbnormalCombat.DotRow
        {
            EffectId = OxygenDeficiencyEffect, IntervalMs = 1000, PerTick = perTick, Remaining = int.MaxValue, NextAt = now + 1000,
        });
        var buff = new Buff
        {
            Uid = ++buffUid, AbnormalId = OxygenDeficiencyAbnormal, SkillId = 0, Group = AbnormalStackRules.For(OxygenDeficiencyAbnormal).Group,
            DurationMs = -1, ExpireAt = PassiveExpireTime, Combat = combat,
        };
        buffs.Add(buff);
        MarkBuffSettingsDirty();
        var add = PlayerPackets.AbnormalAdd(playerKey, [Wire(buff)]);
        worldPlayer?.Map?.Broadcast(worldPlayer, [add]);
        replies.Add(add);
        replies.Add(RidePackets.AbnormalEffect(playerKey, playerKey, buff.Uid, OxygenDeficiencyEffect, buff.Combat!.Dots[0].Remaining, perTick, 0));
        return replies;
    }

    List<byte[]> ClearOxygenDeficiencyIfPresent()
    {
        var gone = buffs.Where(b => b.AbnormalId == OxygenDeficiencyAbnormal).ToList();
        return gone.Count == 0 ? [] : RemoveBuffs(gone, ExpireEtc);
    }
}
