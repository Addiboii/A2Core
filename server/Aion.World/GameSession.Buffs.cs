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
        /// <summary>A ChangeRelationshipEntity effect: the relationship the player shows while it lasts (PC_FriendlyToAll: monsters ignore them).</summary>
        public int? Relationship { get; set; }
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
    const byte ExpireTimeout = 1, ExpireReplaced = 4, ExpireEtc = 7, ExpireEventFunction = 9;

    public IReadOnlyList<int> ActiveBuffs => buffs.Select(b => b.AbnormalId).ToList();

    /// <summary>A stat summed over the active buffs (StatChange / MoveSpeed rows).</summary>
    int BuffStat(string stat) => buffs.Sum(b => b.Stats.GetValueOrDefault(stat));

    static IEnumerable<int> AbnormalIds(IReadOnlyList<SkillEffectRow> effects) =>
        effects.Where(e => e.Type == "Abnormal_True" && e.Value(2) > 0).Select(e => e.Value(2));

    /// <summary>Whether an item skill's buffs are known (their effects the server does not model only show the icon).</summary>
    static bool ItemAbnormalsSupported(IReadOnlyList<SkillEffectRow> effects) =>
        AbnormalIds(effects).Any() && AbnormalIds(effects).All(id => Boxes.Abnormal(id) is not null);

    AbnormalWire Wire(Buff b) => new(b.Uid, b.AbnormalId, b.DurationMs, b.ExpireAt, CasterKey: playerKey, Level: 1, SkillId: b.SkillId == 0 ? null : b.SkillId, RangeBase: position ?? Vector3d.Zero);

    /// <summary>Adds an item skill's buffs: 0x382a, the first 0x3805 per heal row, stat and speed updates.</summary>
    List<byte[]> ApplyItemAbnormals(int skillId, IReadOnlyList<SkillEffectRow> effects, long now)
    {
        var replies = new List<byte[]>();
        var speedBefore = BuffStat("MoveSpeed");
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
    Buff? AddBuff(int abnormalId, int durationMs, int skillId, long now, List<byte[]> replies, bool scripted = false)
    {
        if (resources is null) return null;
        var info = Boxes.Abnormal(abnormalId);
        var group = info?.Group ?? abnormalId;
        replies.AddRange(RemoveBuffs(buffs.Where(b => b.Group == group).ToList(), ExpireReplaced));
        var permanent = durationMs < 0;
        var duration = permanent ? 0 : durationMs;
        var buff = new Buff
        {
            Uid = ++buffUid, AbnormalId = abnormalId, SkillId = skillId, Group = group, DurationMs = permanent ? -1 : durationMs,
            ExpireAt = permanent ? PassiveExpireTime : now + durationMs, Scripted = scripted,
        };
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
                // Hot_Item [interval, heal min, max, ...]: the heal per tick (CANDIDATE: the official totals differ by player).
                case "Hot_Item" when V(0) > 0:
                    buff.Heals.Add(Heal(id, V(0), rng.Next(Math.Min(V(1), V(2)), Math.Max(V(1), V(2)) + 1), duration, now, perTick: true));
                    break;
                // HotRatio_Item [interval, ratio min, max (1/10000 of max HP in total), cap, ...] (OBSERVED: 5% in total).
                case "HotRatio_Item" when V(0) > 0:
                    buff.Heals.Add(Heal(id, V(0), (int)Math.Min(V(3) > 0 ? V(3) : int.MaxValue, (long)resources.HpMax * V(1) / 10_000), duration, now, perTick: false));
                    break;
                // ChangeRelationshipEntity [flag, relationship, flag]: the player's relationship while the buff lasts (the transformations' PC_FriendlyToAll).
                case "ChangeRelationshipEntity" when values.Count >= 2 && MapEventTables.Relationship(values[1]) is { } relation:
                    buff.Relationship = relation;
                    break;
            }
        }
        buffs.Add(buff);
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
        if (AddBuff(abnormalId, durationMs, 0, now, replies, scripted: true) is null) return [];
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
            if (healed) replies.Add(GamePackets.NpcHp(playerKey, r.Hp));
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
}
