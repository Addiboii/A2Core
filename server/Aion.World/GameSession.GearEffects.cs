using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Gear effects (client tables: tools/data/gear-effects.json, GearEffects). What worn gear does beyond its own stats:
//
// 1. Godstone passives. A socketed godstone's GodstoneData OptAbnormalId is an ItemPassive abnormal the wearer carries while the
//    item is worn. OBSERVED: the own MyCharAppear 0x3633 lists them (rec 115817 #174008: 30000171 and 30000231, the character's
//    Fire and Atk_Earth godstones), other players' 0x3645 lists them (745 samples), and a player who equips/sockets gets
//    0x382a {mask 0x11 (caster, range base), uid, abnormal, duration -1, expire 0x3bb2ad57580, caster self, level 1, not ready,
//    position} (rec 115817 #55934/#66049/#85531, other players' keys, each right before their equip 0x561c). OURS: one 0x382a per
//    new passive on equip / socket, 0x382c reason 7 when it goes (CANDIDATE: no removal recorded).
// 2. Godstone procs. The passive's SkillAbnormalEffect TriggerSkillByAttackAll row: values[0] skill, [1] chance /10000, [2]
//    cooldown ms, [6] the hit types that trigger it ("Normal, Critical"). The skill is a system skill whose SkillEffect is a flat
//    Damage (Atk_* / element godstones: [0..1] flat) or an Abnormal_Cc ([0..1] duration ms, [2] abnormal, Probability condition
//    3000 or 2000 for the hard CC). OBSERVED (70 own procs: rec 115817, 210132, 211036, 211600; 154 of other players): the proc is
//    cast with the caster's next action uid: 0x3802 {caster, mask 0, skill, uid, target mask 0 (no location), target, yaw, attack
//    speed, 01, cooldown 0} and at once 0x3806 {caster, skill, uid, 00} (own procs: 70 of 70 ends at +0 ms; others' procs carry
//    no end), then about 48 ms later the hit: damage procs 0x3804 switch 06 (plotter flags 0, EAttackAngle) with the damage
//    (crits seen: hit type 3); CC procs 0x3804 switch 00 (effect, seq 1, attack speed) and, when the condition passes, 0x382a on
//    the target {mask 0x13 caster+skill+range, uid, abnormal, duration, expire, caster, level 1, skill, ...} (bleed 3001016
//    #36374), the DoT ticks 0x3805 {mask 0a, caster, the abnormal's uid, the Dot_Dmg effect id, damage, skill}.
//    OURS / CANDIDATE: one roll per landed cast per target (not per hit row), the cooldown from the proc, our packets in the same
//    batch as the skill's hits (no 48 ms delay), damage = the flat value reduced by the target's defense like a skill's flat part,
//    crit by the weapon's critical (x1.5) - the official damage was higher than the table flat (3000019, flat 1000: 1564-3460).
//    Dot_Dmg [0] interval ms, [1..2] per tick, reduced by defense (official 648 per tick from a 2000-4000 bleed). Slow/snare lower
//    the monster's MoveSpeed (StatChange); blind, silence and slow's CombatSpeed show only. PvP procs not built.
// 3. Equipment sets (ItemSet / ItemSetEffect): pieces worn of a set reach its steps; a step's OptStats and the StatChange rows of
//    its OptAbnormalId join the stat sheet, its abnormal is announced as a gear passive (CANDIDATE: no set was ever recorded;
//    today only the two Arcana sets exist: PvEAddDamage 60/150, PvEDamageDefense 1000; Arcana_A_set_002's 2-piece CastSkill
//    (skill 4000301 every 30 s) is not cast).
// 4. Sub-stat skills (ItemEquipSubstatSkill). OBSERVED: the official item record carries _sub_skills [{skill i32, level u8}] and
//    _add_sub_skills [{skill, 0}] (rec 210132 #1605: 14710000, 14720000, 14780000, 14790000 at level 1, one per worn item), and
//    the skills' SkillAdditionalLevel _equip byte is the sum of the worn items' levels (#1549-1557 0x3813 reason 10
//    SkillAdditionalLevelModify: 14720000 shown 12 = point 9 + Daevanion 2 + equip 1; the 0x5100 entries the same). OURS /
//    CANDIDATE: the roll at soul bind (Item.dat bUseSubStatSkill: up to SoulbindRandomSkillCountMax picks, each with
//    SubStatSkillSelectProb /10000, weighted by SkillRandomProb among the character's class rows, level SkillLevelBaseValue),
//    stored as upgrades.sub_skills; 0x3813 reason 10 for each skill whose equip levels change on an equip.
// 5. Wings: CollectionStats takes Wing.dat / WingEnchantEffect from the same file (see there). No Wing.dat row has an EnchantGroup,
//    so WingEnchant_RQ has nothing to enchant (not handled; every wing stays at enchant level 0).
public sealed partial class GameSession
{
    static GearEffects Gear => GearEffects.Embedded;
    const byte SkillAdditionalLevelModify = 10;   // ESkillAcquireReason

    /// <summary>The worn gear's effects, recomputed after a gear change (RefreshStats / socketing drops it).</summary>
    sealed record GearState(IReadOnlyList<int> Passives, IReadOnlyDictionary<string, int> Stats, IReadOnlyDictionary<int, int> SkillLevels);
    GearState? gearState;

    GearState WornGear()
    {
        if (gearState is { } cached) return cached;
        var worn = storedItems.Values.Where(i => i.Location == "equipment").ToList();
        var passives = new List<int>();
        foreach (var row in worn)
            foreach (var id in WireGodstones(row))
                if (id != 0 && Enhance.GodstoneOf(id) is { Abnormal: > 0 } stone && !passives.Contains(stone.Abnormal)) passives.Add(stone.Abnormal);
        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (set, pieces) in WornSets(worn.Select(r => r.TemplateId)))
            foreach (var (need, lines, abnormal) in set.Steps)
            {
                if (pieces < need) continue;
                foreach (var (stat, value) in lines.Concat(Gear.AbnormalOf(abnormal)?.Stats ?? [])) stats[stat] = stats.GetValueOrDefault(stat) + value;
                if (abnormal > 0 && !passives.Contains(abnormal)) passives.Add(abnormal);
            }
        var levels = new Dictionary<int, int>();
        foreach (var row in worn)
            foreach (var (skill, level) in SubSkills(row)) levels[skill] = levels.GetValueOrDefault(skill) + level;
        return gearState = new GearState(passives, stats, levels);
    }

    /// <summary>The sets the worn items belong to, with the number of different pieces worn of each.</summary>
    static IEnumerable<(GearEffects.ItemSet Set, int Pieces)> WornSets(IEnumerable<int> wornItems) =>
        wornItems.Distinct().SelectMany(item => Gear.SetsOf(item).Select(set => (set, item))).GroupBy(p => p.set.Name)
            .Select(g => (g.First().set, g.Count()));

    /// <summary>A set bonus stat of the worn gear (ItemSetEffect OptStats and the StatChange rows of its abnormal).</summary>
    int GearEffectStat(string stat) => WornGear().Stats.GetValueOrDefault(stat);
    IEnumerable<string> GearEffectStatNames() => WornGear().Stats.Keys;

    /// <summary>The worn items' sub-stat skill levels for a skill (SkillAdditionalLevel _equip).</summary>
    public int EquipSkillBonus(int skillId) => WornGear().SkillLevels.GetValueOrDefault(skillId);
    byte EquipBonusByte(int skillId) => (byte)Math.Clamp(EquipSkillBonus(skillId), 0, 255);

    /// <summary>An item's sub-stat skills (upgrades.sub_skills: [[skill, level], ...]).</summary>
    static List<(int Skill, int Level)> SubSkills(ItemState row) =>
        row.Upgrades.ValueKind == JsonValueKind.Object && row.Upgrades.TryGetProperty("sub_skills", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 2 && e[0].TryGetInt32(out _) && e[1].TryGetInt32(out _))
                .Select(e => (e[0].GetInt32(), e[1].GetInt32())).ToList()
            : [];

    static IReadOnlyList<(int Skill, byte Level)> WireSubSkills(ItemState row) =>
        SubSkills(row).Select(s => (s.Skill, (byte)Math.Clamp(s.Level, 0, 255))).ToList();

    // ---------- Passives (godstones, sets) ----------

    /// <summary>The gear passives announced to the client: abnormal id -> uid.</summary>
    readonly Dictionary<int, int> gearPassiveUids = [];

    AbnormalWire GearPassiveWire(int abnormal, int uid) =>
        new(uid, abnormal, -1, Target.PermanentExpiry, CasterKey: playerKey, Level: 1, RangeBase: position ?? Vector3d.Zero);

    /// <summary>The gear passives as other players' 0x3645 carries them (OBSERVED there).</summary>
    IEnumerable<AbnormalWire> GearPassiveWires() => gearPassiveUids.Select(p => GearPassiveWire(p.Key, p.Value));

    /// <summary>
    /// The gear passives that changed since the last announcement: 0x382a per new one, one 0x382c for those gone. With
    /// <paramref name="resend"/> (a map load) every worn passive anew.
    /// </summary>
    List<byte[]> GearPassives(bool resend = false)
    {
        if (resend) gearPassiveUids.Clear();
        var wanted = WornGear().Passives;
        var replies = new List<byte[]>();
        var gone = gearPassiveUids.Where(p => !wanted.Contains(p.Key)).ToList();
        if (gone.Count > 0)
        {
            foreach (var (id, _) in gone) gearPassiveUids.Remove(id);
            replies.Add(PlayerPackets.AbnormalRemove(playerKey, gone.Select(g => (g.Value, ExpireEtc)).ToList()));
        }
        foreach (var id in wanted.Where(id => !gearPassiveUids.ContainsKey(id)))
            replies.Add(PlayerPackets.AbnormalAdd(playerKey, [GearPassiveWire(id, gearPassiveUids[id] = ++buffUid)]));
        return replies;
    }

    /// <summary>The equip levels last told to the client (skill -> levels), for the 0x3813 on a change.</summary>
    Dictionary<int, int> sentEquipLevels = [];

    /// <summary>After a gear change (from RefreshStats): the passives (to the player and the players around) and the skills whose
    /// equip levels changed (0x3813 reason 10 SkillAdditionalLevelModify, as at the official world entry).</summary>
    List<byte[]> GearChanges()
    {
        var replies = GearPassives();
        if (replies.Count > 0) worldPlayer?.Map?.Broadcast(worldPlayer, replies.ToList());
        var levels = WornGear().SkillLevels;
        foreach (var skill in levels.Keys.Concat(sentEquipLevels.Keys).Distinct().Where(s => levels.GetValueOrDefault(s) != sentEquipLevels.GetValueOrDefault(s)).Order())
            if (learned.Contains(skill))
                replies.Add(SkillLevelUpdate(skill, SkillLevel(skill), SkillAdditionalLevelModify, DaevanionBonusByte(skill), EquipBonusByte(skill)));
        sentEquipLevels = levels.ToDictionary(p => p.Key, p => p.Value);
        return replies;
    }

    /// <summary>A map load: every worn passive again (the client draws the player anew) and the equip levels as the 0x5100 says.</summary>
    List<byte[]> GearAtLoad()
    {
        sentEquipLevels = WornGear().SkillLevels.ToDictionary(p => p.Key, p => p.Value);
        return GearPassives(resend: true);
    }

    // ---------- Procs ----------

    /// <summary>When each proc skill may go off again (Unix ms).</summary>
    readonly Dictionary<int, long> procReadyAt = [];

    /// <summary>
    /// The worn gear's on-hit skills after a cast's hits on a monster (see the header): per passive with a TriggerSkillByAttackAll
    /// row, out of cooldown, a hit of a listed type and the chance roll. Returns the packets and the damage dealt.
    /// </summary>
    (List<byte[]> Replies, int Dealt) GearProcs(Target target, IReadOnlyList<Hit> hits, double yaw, long now)
    {
        var replies = new List<byte[]>();
        var dealt = 0;
        if (target.Dead || !hits.Any(h => h.Amount > 0)) return (replies, 0);
        var types = hits.Where(h => h.Amount > 0).Select(h => h.Critical ? "Critical" : "Normal").ToHashSet();
        foreach (var abnormal in WornGear().Passives)
        {
            if (target.Dead) break;
            if (Gear.TriggerOf(abnormal) is not { } trigger || Gear.ProcSkillOf(trigger.Skill) is not { } proc) continue;
            if (!trigger.HitTypes.Any(types.Contains) || procReadyAt.GetValueOrDefault(trigger.Skill) > now) continue;
            if (rng.Next(10_000) >= trigger.Chance) continue;
            procReadyAt[trigger.Skill] = now + trigger.CooldownMs;
            var (packets, amount) = CastProc(proc, target, yaw, now);
            replies.AddRange(packets);
            dealt += amount;
        }
        return (replies, dealt);
    }

    /// <summary>One proc: start and end with the next action uid, then its effect rows on the target.</summary>
    (List<byte[]> Replies, int Dealt) CastProc(GearEffects.ProcSkill proc, Target target, double yaw, long now)
    {
        actionUid = actionUid % 255 + 1;
        var uid = (byte)actionUid;
        var replies = new List<byte[]>
        {
            PlayerPackets.SkillStart(playerKey, proc.Id, uid, target.Key, yaw, null, AttackSpeed),
            GamePackets.SkillEnd(playerKey, proc.Id, uid),
        };
        var dealt = 0;
        var defense = EffectiveDefense(target, now);
        var angle = position is { } from && FacingSide(target.Position, target.Direction, from) > 0 ? GamePackets.AngleBack : GamePackets.AngleFront;
        foreach (var effect in proc.Effects)
        {
            if (effect.Type == "Damage")
            {
                var flat = rng.Next(Math.Min(effect.Int(0), effect.Int(1)), Math.Max(effect.Int(0), effect.Int(1)) + 1);
                var critical = rng.NextDouble() < Math.Min(Combat.CriticalCap, data.WeaponStatsOf(equippedWeapon).Critical / 1000.0);
                double raw = critical ? flat * Combat.CriticalMultiplier : flat;
                var amount = Math.Max(1, (int)Math.Round(raw * Combat.DefenseScale / (Combat.DefenseScale + Math.Max(0, defense))));
                amount = PveOut(target, proc.Id, TakenAfterMods(target, amount, proc.Id, now), critical);
                if (target.Immune || target.HitGauge || target.Untouchable) amount = 0;
                target.Hp = Math.Max(HpFloor(target), target.Hp - amount);
                dealt += amount;
                replies.Add(GamePackets.SkillHit(target.Key, playerKey, proc.Id, effect.Id, amount, critical, 1, AttackSpeed, angle, actionUid: uid));
            }
            else if (effect.Type.StartsWith("Abnormal", StringComparison.Ordinal))
            {
                replies.Add(GamePackets.SkillEffect(target.Key, playerKey, proc.Id, uid, effect.Id, AttackSpeed));
                if (effect.Chance < 10_000 && rng.Next(10_000) >= effect.Chance) continue;
                replies.AddRange(ProcAbnormal(proc.Id, effect, target, defense, now));
            }
        }
        return (replies, dealt);
    }

    /// <summary>A proc's abnormal on the monster: 0x382a (caster, skill), held for stun/hold/paralysis, slowed for a snare, a DoT.</summary>
    List<byte[]> ProcAbnormal(int skill, GearEffects.ProcEffect effect, Target target, int defense, long now)
    {
        var abnormalId = effect.Int(2);
        var duration = rng.Next(Math.Min(effect.Int(0), effect.Int(1)), Math.Max(effect.Int(0), effect.Int(1)) + 1);
        if (abnormalId <= 0 || duration <= 0) return [];
        var abnormal = Gear.AbnormalOf(abnormalId);
        var hold = abnormal?.Effects.Any(e => e.Type is "Stun" or "Hold" or "Paralysis") == true;
        mapEventNow = now;
        AddNpcBuff(target, abnormalId, duration, hold: hold, casterKey: playerKey, skillId: skill);
        var replies = mapEventOut.ToList();
        mapEventOut.Clear();
        if (npcBuffs.GetValueOrDefault(target.Key)?.FirstOrDefault(b => b.AbnormalId == abnormalId) is not { } applied) return replies;
        foreach (var e in abnormal?.Effects ?? [])
        {
            if (e.Type == "StatChange" && e.Values.Count > 1 && e.Values[0] == "MoveSpeed") target.AddMod("MoveSpeed", e.Int(1) / 100.0, now + duration, skill);
            if (e.Type == "Dot_Dmg" && e.Int(0) > 0)
            {
                var tick = rng.Next(Math.Min(e.Int(1), e.Int(2)), Math.Max(e.Int(1), e.Int(2)) + 1);
                var perTick = Math.Max(1, (int)Math.Round(tick * (double)Combat.DefenseScale / (Combat.DefenseScale + Math.Max(0, defense))));
                dots.RemoveAll(d => d.TargetKey == target.Key && d.Skill == skill);
                dots.Add(new Dot(target.Key, skill, e.Id, perTick, Math.Max(250, e.Int(0)), applied.ExpireAt, applied.Uid) { NextAt = now + Math.Max(250, e.Int(0)) });
            }
        }
        return replies;
    }

    internal void SetProcReadyForChecks(int skill, long at) => procReadyAt[skill] = at;
    internal IReadOnlyDictionary<int, int> GearPassivesForChecks => gearPassiveUids;
    internal List<byte[]> GearChangesForChecks() { gearState = null; return GearChanges(); }
    internal (List<byte[]> Replies, int Dealt) GearProcsForChecks(int targetKey, IReadOnlyList<Hit> hits, long now) =>
        targets is not null && targets.TryGetValue(targetKey, out var t) ? GearProcs(t, hits, 0, now) : ([], 0);

    // ---------- Sub-stat skills ----------

    /// <summary>
    /// The sub-stat skills a freshly soul-bound item rolls (CANDIDATE, see the header), stored as upgrades.sub_skills; the skills
    /// for the 0x5630 reply (empty when the item has none or the rolls all missed).
    /// </summary>
    async Task<IReadOnlyList<(int Skill, byte Level)>> RollSubSkillsAsync(INativeCharacterStore store, Guid character, ItemState row, CancellationToken ct)
    {
        var picked = RollSubSkills(row.TemplateId, className, rng);
        if (picked.Count == 0) return [];
        var json = JsonSerializer.Serialize(picked.Select(p => new[] { p.Skill, p.Level }));
        var saved = await Stored(async () => await store.SetItemUpgradeAsync(character, row.InstanceId, "sub_skills", json, ct) ? [[]] : []);
        return saved.Count == 0 ? [] : picked.Select(p => (p.Skill, (byte)Math.Clamp(p.Level, 0, 255))).ToList();
    }

    /// <summary>The roll itself: up to the item's count of picks, each with its chance, weighted among the class's rows, no repeats.</summary>
    public static List<(int Skill, int Level)> RollSubSkills(int itemId, string? className, Random random)
    {
        var picked = new List<(int Skill, int Level)>();
        if (Gear.SubSkillsOf(itemId) is not { } roll || className is null) return picked;
        var pool = Gear.SubSkillGroup(roll.Group).Where(r => r.Class == className && r.Weight > 0).ToList();
        for (var n = 0; n < roll.CountMax && pool.Count > 0; n++)
        {
            if (random.Next(10_000) >= roll.Chance) continue;
            var pick = EnhanceTables.Pick(pool, r => r.Weight, random);
            pool.Remove(pick);
            picked.Add((pick.Skill, pick.Base));
        }
        return picked;
    }
}
