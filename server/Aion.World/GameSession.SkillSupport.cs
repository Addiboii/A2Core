using System.Globalization;
using Aion.Protocol.Native;

namespace Aion.World;

// Skill heals, buffs and cleanses on the caster and its allies, and the defensive abnormals on players (wave 2 items 2.1, 2.4, 2.5;
// audit-2026-11-13 skills.md gaps G1, G2, G6, G7, G9). Each hit step of the client skill (SkillClient, OFFICIAL client data) goes
// to the targets its SkillEffectFilter names: Self -> the caster; a friendly filter (MyParty, friend) -> the party members / allies
// in its circle (the caster too when the filter counts it, bTargetCountIncludeCaster); an enemy filter -> the monsters (Combat, as
// before). Rows: HpHeal / HpHealRatio restore HP, Abnormal_True puts the abnormal at its level (the row's [3] at the skill level,
// SkillAbnormalEffectLv values) on that target, Dispel_True removes abnormals by category / id. Self-buffs of attack skills (a Self
// step of a Negative skill: 12090000, 12350000, 13140000, 16250000) land on the caster, no longer on the monster.
//
// Wire (OFFICIAL, recordings 2026-11-06): one 0x3804 per effect row and target, the step's index + 1 as the hit sequence
// (Recuperation 18120140 solo, rec 145442 #9935-#9939: HpHeal 1265 mask 4, two Abnormal rows and the Dispel mask 0 - hit type 2,
// 0 when the dispel removed nothing - HpHealRatio 1336; Healing Light solo seq 2 = its Self step, rec 115817 #119153), then
// 0x382a (level = the abnormal level, 14 there) and 0x3805 mask 9 with a heal over time's total (1288 = 4 ticks of 322, then
// 0x3805 mask 0x0b every 2000 ms naming the healer). Heal amounts shown are the computed amount, not the HP gained (16990004
// always 100000). Dispel removal: 0x382c mask 7 {uid, reason 11, caster, skill, effect} (rec 105448 #7705). A shield (HpBarrier):
// 0x3805 mask 9 with its pool at the add, mask 0x0a {attacker, absorbed} per hit, 0x382c reason 7 when used up (rec 105448 #36371,
// #36669, #36675).
// CANDIDATE (no official recording has a heal or buff on ANOTHER player of ours; other players' heals were only seen as a bystander):
// the choice of allies (same race, same map, party for MyParty, circle radius), the "target or self" rule of Healing Light (its
// party step reached an ally -> its Self step is skipped), HpHeal = rolled [0,1] x [2]/10000 x (1 + the caster's AmplifyHpHeal /
// HpHealRate), HpHealRatio = [0,1]/10000 of the target's max HP, Hot total = [1,2] x [3]/10000 x the same caster bonus, the shield
// pool = [3]/10000 of the holder's max HP (Nezekan's Shield "41% of Max HP" = 4100), dispel matching (category Buff / DeBuff, the
// [5] id or group list, count [3], chance [7]; the dispel level [4]/[6] ignored), the defensive rows' value meanings (from the skill
// texts: ConvertDamageToHpHeal 50% chance to absorb 25%, ConvertDamageToMP 50% to HP and 10% to MP while MP >= 50%, DamageTransfer
// 50% to the caster within 40 m, ImmuneDecreaseHP HP not below 10%, ImmuneSkillEffectGroup Negative = no damage and no debuffs,
// DamageReflect 10% back, never killing the monster - OURS, the AI loop calls this hit).
public sealed partial class GameSession
{
    /// <summary>The shields' pools left (by buff uid).</summary>
    readonly Dictionary<int, int> barrierLeft = [];
    /// <summary>A share of another player's hit is being taken (DamageTransfer): no second transfer from it.</summary>
    bool sharingHit;
    /// <summary>This session's random source (the caster's rolls when it heals another player).</summary>
    Random SessionRng => rng;

    static bool SupportEffect(string type) => type is "HpHeal" or "HpHealRatio" or "Abnormal_True" or "Abnormal" or "Dispel_True";

    /// <summary>A hit step for the caster's own side: Self, a friendly-only filter, or no filter on a Positive skill.</summary>
    static bool SupportStep(SkillClient.Skill s, SkillClient.Step st) => st.Filter switch
    {
        null => s.Disposition == "Positive",
        { Shape: "Self" } => true,
        { Friend: true, Enemy: false } => true,
        _ => false,
    };

    /// <summary>The abnormals a skill's client rows give only to its own side (Self / friendly steps), never to the monsters it hits.</summary>
    internal static HashSet<int> SupportOnlyAbnormals(int skill)
    {
        if (ClientSkills.Of(skill) is not { } s) return [];
        static IEnumerable<int> Ids(SkillClient.Step st) => st.Effects.Where(e => e.Type is "Abnormal_True" or "Abnormal").Select(e => SkillClient.Int(e.Values, 2)).Where(a => a > 0);
        var own = s.Steps.Where(st => SupportStep(s, st)).SelectMany(Ids).ToHashSet();
        own.ExceptWith(s.Steps.Where(st => !SupportStep(s, st)).SelectMany(Ids));
        return own;
    }

    /// <summary>The caster's heal power: AmplifyHpHeal / HpHealRate (0.01% units, CANDIDATE: the official heals ran ~1.3x the table).</summary>
    double HealPower => 1 + Math.Max(0, Bonus("AmplifyHpHeal") + Bonus("HpHealRate") + Bonus("HPHealRate")) / 10_000.0;

    static int Roll(Random rng, int a, int b) => a == b ? a : rng.Next(Math.Min(a, b), Math.Max(a, b) + 1);

    /// <summary>
    /// A cast's rows for the caster's own side: per support step its targets (SupportRecipients), each target's rows; the hits go to
    /// the caster and those who see it, each target's own HP / buffs to that target. Skills the client tables do not describe keep
    /// the old rule (SelfBuffs: a Positive skill's abnormals on the caster).
    /// </summary>
    List<byte[]> SupportCast(int skill, CastRequest request, long now)
    {
        if (ClientSkills.Of(skill) is not { } s || !s.Steps.Any(st => SupportStep(s, st) && st.Effects.Any(e => SupportEffect(e.Type))))
            return SelfBuffs(skill, now);
        var level = CombatSkillLevel(skill);
        var power = HealPower;
        var plan = s.Steps.Where(st => SupportStep(s, st) && st.Effects.Any(e => SupportEffect(e.Type)))
            .Select(st => (Step: st, To: SupportRecipients(st.Filter, request))).ToList();
        // "Heal the aimed party member, else yourself" (Healing Light 17100000: a MyParty step for 1 target not counting the caster and
        // a Self step with the same rows; solo only the Self step went off, rec 115817 #119153): an ally reached -> no Self step.
        var reachedAlly = plan.Where(p => p.Step.Filter is { Shape: not "Self", Max: 1, CountCaster: false } && p.To.Any(t => t.Who != this))
            .Select(p => p.Step.Effects.Select(e => e.Id).ToHashSet()).ToList();
        var shown = new List<byte[]>();
        var own = new List<byte[]>();
        foreach (var (step, to) in plan)
        {
            if (step.Filter is null or { Shape: "Self" } && reachedAlly.Any(ids => ids.SetEquals(step.Effects.Select(e => e.Id)))) continue;
            foreach (var (who, aimed) in to)
            {
                var (hits, theirs) = who.TakeSupport(this, skill, step, level, power, aimed, to.Count, now);
                shown.AddRange(hits);
                if (who == this) own.AddRange(theirs);
                else if (who.worldPlayer is { } ally) ally.Send(ally.Seeing.Contains(playerKey) ? theirs : [.. hits, .. theirs]);
            }
        }
        if (worldPlayer?.Map is { } map) map.Broadcast(worldPlayer, shown);
        return [.. shown, .. own];
    }

    /// <summary>
    /// The players a support step reaches: the caster for Self (or no filter); for a friendly filter the same-race players on the map
    /// (party members only for MyParty) inside its circle around the caster (or the aimed player), the caster only when the filter
    /// counts it, the aimed one first, then the nearest, at most TargetCountMax. A one-target filter not counting the caster takes the
    /// aimed player only. Summon filters (MyChild, MyParent...) reach no player.
    /// </summary>
    List<(GameSession Who, bool Aimed)> SupportRecipients(SkillClient.Filter? f, CastRequest request)
    {
        if (resources is null || dead) return [];
        if (f is null or { Shape: "Self" }) return [(this, true)];
        if (!f.Friend || f.Only is "MyChild" or "MyParent" or "MyParentParty") return [];
        if (worldPlayer?.Map is not { } map)
            return f.CountCaster || f.Shape == "Single" && request.TargetKey == playerKey ? [(this, true)] : [];
        var me = position ?? worldPlayer.Position;
        IEnumerable<WorldPlayer> pool = f.Only == "MyParty" ? (IEnumerable<WorldPlayer>?)Social?.PartyOf(worldPlayer)?.Members ?? [worldPlayer] : map.Players.Values;
        var live = pool.Where(p => p.Map == map && !p.Hidden && p.Session.resources is not null && !p.Session.dead && p.Session.profileRace == profileRace
            && (f.CountCaster || p != worldPlayer)).Distinct().ToList();
        var aimedPlayer = live.FirstOrDefault(p => p.Key == request.TargetKey);
        if (f.Shape == "Single" || f.Max <= 1 && !f.CountCaster)
            return aimedPlayer is not null && InFriendArea(f, me, aimedPlayer.Position) ? [(aimedPlayer.Session, true)] : [];
        var centre = f.NeedTarget && aimedPlayer is not null ? aimedPlayer.Position : me;
        return live.Where(p => p == worldPlayer || InFriendArea(f, centre, p.Position))
            .OrderBy(p => p == worldPlayer ? 0 : p == aimedPlayer ? 1 : 2).ThenBy(p => p.Position.DistanceSquared(centre))
            .Take(Math.Max(1, f.Max)).Select(p => (p.Session, p.Key == request.TargetKey || p == worldPlayer && request.TargetKey is 1 or 0)).ToList();
    }

    /// <summary>A player's body (50 cm) inside a friendly area: the circle's radius (Arc / Rectangle: their reach, CANDIDATE) and height.</summary>
    static bool InFriendArea(SkillClient.Filter f, Vector3d centre, Vector3d at)
    {
        var reach = Math.Max(f.Radius, f.Length) + 50;
        var height = f.Height > 0 ? f.Height : DefaultAreaHeight;
        double dx = at.X - centre.X, dy = at.Y - centre.Y;
        return Math.Abs(at.Z - centre.Z) <= height + 50 && dx * dx + dy * dy <= reach * reach;
    }

    /// <summary>The row conditions for a player target of a support step (its buffs, HP, the caster's buffs; ObjectType PC).</summary>
    SkillClient.RowContext SupportContext(GameSession caster, bool aimed, int gathered)
    {
        int GroupOf(int a) => ClientSkills.AbnormalOf(a)?.Group ?? AbnormalTable.Embedded.Of(a)?.Group ?? a;
        var mine = buffs.Select(b => b.AbnormalId).ToHashSet();
        var theirs = caster.buffs.Select(b => b.AbnormalId).ToHashSet();
        bool HasType(string type) => mine.Any(a => ClientSkills.AbnormalOf(a)?.Effects.Any(e => e.Type == type) == true || AbnormalTable.Embedded.Of(a)?.Effects.Any(e => e.Type == type) == true);
        var r = resources!;
        return new SkillClient.RowContext(aimed, Math.Max(1, gathered), false, mine.Contains, g => buffs.Any(b => b.Group == g || GroupOf(b.AbnormalId) == g), HasType,
            theirs.Contains, g => caster.buffs.Any(b => b.Group == g || GroupOf(b.AbnormalId) == g), r.HpMax > 0 ? (int)(r.Hp * 10_000L / r.HpMax) : 10_000, caster.SessionRng, TargetPc: true);
    }

    /// <summary>
    /// This player takes one support step of <paramref name="caster"/>'s cast (the caster itself or an ally): heals, abnormals at their
    /// level, dispels. Returns the 0x3804 rows everyone near sees and this player's own HP / buff / stat updates.
    /// </summary>
    (List<byte[]> Hits, List<byte[]> Own) TakeSupport(GameSession caster, int skill, SkillClient.Step step, int skillLevel, double power, bool aimed, int gathered, long now)
    {
        var hits = new List<byte[]>();
        var own = new List<byte[]>();
        if (resources is not { } r || dead) return (hits, own);
        var context = SupportContext(caster, aimed, gathered);
        var rows = step.Effects.Where(e => SupportEffect(e.Type) && SkillClient.RowApplies(step, e, context)).ToList();   // weighed before any applies
        var seq = step.Index + 1;
        var casterKey = caster.playerKey;
        var speedBefore = MoveSpeedBonus();
        var healed = false;
        var statsChanged = false;
        var buffPackets = new List<byte[]>();
        foreach (var e in rows)
        {
            var v = ClientSkills.ValuesAt(e, skillLevel);
            int N(int i) => SkillClient.Int(v, i);
            switch (e.Type)
            {
                case "HpHeal" or "HpHealRatio":
                {
                    var amount = e.Type == "HpHeal"
                        ? (int)Math.Round(Roll(caster.SessionRng, N(0), N(1)) * (N(2) > 0 ? N(2) / 10_000.0 : 1) * power)
                        : (int)Math.Round((long)r.HpMax * Roll(caster.SessionRng, N(0), N(1)) / 10_000.0 * (N(3) > 0 ? N(3) / 10_000.0 : 1));
                    amount = HealReceived(Math.Max(0, amount));
                    if (amount <= 0) continue;
                    r.Hp = (int)Math.Min(r.HpMax, (long)r.Hp + amount);
                    healed = true;
                    hits.Add(SupportHit(playerKey, casterKey, skill, caster.actionUid, 2, e.Id, seq, caster.AttackSpeed, amount));
                    break;
                }
                case "Abnormal_True" or "Abnormal":
                {
                    var abnormal = N(2);
                    if (abnormal <= 0 || N(4) is > 0 and < 10_000 && caster.SessionRng.Next(10_000) >= N(4)) continue;
                    var duration = N(0) > N(1) ? N(0) : Roll(caster.SessionRng, N(0), N(1));
                    var abnormalLevel = Math.Max(1, N(3));
                    hits.Add(SupportHit(playerKey, casterKey, skill, caster.actionUid, 2, e.Id, seq, caster.AttackSpeed, 0));
                    var buff = AddBuff(abnormal, duration, skill, now, buffPackets, level: abnormalLevel, setup: b =>
                    {
                        if (caster != this) b.CasterKey = casterKey;
                        SkillHots(b, abnormal, abnormalLevel, duration, power, now);
                    });
                    if (buff is null) continue;
                    // The skill buff's stats recorded officially only where the client rows give none (they are one level's numbers).
                    if (buff.Stats.Count == 0)
                        foreach (var (stat, value) in SeenAbnormals.Stats(abnormal)) buff.Stats[stat] = buff.Stats.GetValueOrDefault(stat) + value;
                    statsChanged |= buff.Stats.Count > 0;
                    buffPackets.AddRange(ShieldUp(buff, abnormal, abnormalLevel));
                    break;
                }
                case "Dispel_True":
                {
                    var removed = Dispel(v, casterKey, skill, e.Id, buffPackets);
                    statsChanged |= removed.Any(b => b.Stats.Count > 0);
                    hits.Add(SupportHit(playerKey, casterKey, skill, caster.actionUid, removed.Count > 0 ? 2 : 0, e.Id, seq, caster.AttackSpeed, 0));
                    break;
                }
            }
        }
        own.AddRange(buffPackets);
        if (statsChanged) own.AddRange(BuffStatsChanged(speedBefore));
        if (healed)
        {
            var hp = GamePackets.NpcHp(playerKey, r.Hp);
            own.Add(hp);
            worldPlayer?.Map?.Broadcast(worldPlayer, [hp]);
        }
        return (hits, own);
    }

    /// <summary>
    /// 0x3804 of a support row: target | mask (4: amount) 00 | caster | skill | the cast's action uid | hit type (2; 0 for a dispel that
    /// removed nothing) | effect | seq u32 | attack speed | [amount] | seq u8 | 00. OFFICIAL (rec 145442 #9935-#9939).
    /// </summary>
    internal static byte[] SupportHit(int targetKey, int casterKey, int skill, int actionUid, int hitType, int effectId, int seq, int attackSpeed, int amount)
    {
        var w = PacketWriter.Opcode(0x3804).Varint(targetKey).U8(amount > 0 ? 4 : 0).U8(0).Varint(casterKey).U32((uint)skill).U8((byte)actionUid)
            .Varint(hitType).U32((uint)effectId).U32((uint)seq).Varint(attackSpeed);
        if (amount > 0) w.Varint(amount);
        return w.U8((byte)seq).U8(0).ToArray();
    }

    /// <summary>A skill abnormal's heal over time (Hot [interval, flat min, max, ratio 1/10000, ...] at its level): the total over the
    /// duration, split into ticks (OFFICIAL shape: 1288 over 4 ticks of 322).</summary>
    void SkillHots(Buff b, int abnormal, int level, int durationMs, double power, long now)
    {
        if (ClientSkills.AbnormalOf(abnormal) is not { } row || durationMs <= 0) return;
        foreach (var e in row.Effects.Where(e => e.Type == "Hot"))
        {
            var v = ClientSkills.ValuesAt(e, level);
            var interval = SkillClient.Int(v, 0);
            if (interval <= 0) continue;
            var ratio = SkillClient.Int(v, 3);
            var total = (int)Math.Round(Roll(rng, SkillClient.Int(v, 1), SkillClient.Int(v, 2)) * (ratio > 0 ? ratio / 10_000.0 : 1) * power);
            if (total > 0) b.Heals.Add(Heal(e.Id, interval, total, durationMs, now, perTick: false));
        }
    }

    /// <summary>The effect rows of an active abnormal at its level (client skill rows, else the abnormal table).</summary>
    static IEnumerable<(int Id, string Type, IReadOnlyList<string> Values)> AbnormalRows(int abnormal, int level)
    {
        if (ClientSkills.AbnormalOf(abnormal) is { } row) return row.Effects.Select(e => (e.Id, e.Type, ClientSkills.ValuesAt(e, level)));
        if (AbnormalTable.Embedded.Of(abnormal) is { } table) return table.Effects.Select(e => (e.Id, e.Type, e.Values));
        return [];
    }

    static string? AbnormalKind(int abnormal) => ClientSkills.AbnormalOf(abnormal)?.Type ?? AbnormalTable.Embedded.Of(abnormal)?.Type;

    /// <summary>A shield's pool ([3]/10000 of max HP) set when it lands, announced as 0x3805 mask 9 (OFFICIAL rec 105448 #36371).</summary>
    List<byte[]> ShieldUp(Buff buff, int abnormal, int level)
    {
        if (resources is not { } r) return [];
        foreach (var (id, type, values) in AbnormalRows(abnormal, level))
        {
            var pool = type == "HpBarrier" ? ShieldPool(values, r.HpMax) : 0;
            if (pool <= 0) continue;
            barrierLeft[buff.Uid] = pool;
            return [RidePackets.AbnormalEffect(playerKey, buff.CasterKey != 0 ? buff.CasterKey : playerKey, buff.Uid, id, pool, null, buff.SkillId)];
        }
        return [];
    }

    static int ShieldPool(IReadOnlyList<string> values, int hpMax) => (int)((long)hpMax * SkillClient.Int(values, 3) / 10_000);

    /// <summary>
    /// Dispel_True [?, category Buff / Debuff / DeBuff / None, property, count, compare, ids or groups, level range, chance] on this player:
    /// the matching abnormals (newest first) removed with 0x382c reason 11 naming the dispeller (OFFICIAL form, rec 105448 #7705).
    /// </summary>
    List<Buff> Dispel(IReadOnlyList<string> v, int casterKey, int skill, int effectId, List<byte[]> replies)
    {
        var category = v.Count > 1 ? v[1] : "None";
        var count = Math.Max(1, SkillClient.Int(v, 3));
        var chance = SkillClient.Int(v, 7);
        var ids = (v.Count > 5 ? v[5] : "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).ToHashSet();
        if (chance is > 0 and < 10_000 && rng.Next(10_000) >= chance) return [];
        var wantDebuff = category.Equals("Debuff", StringComparison.OrdinalIgnoreCase);
        var wantBuff = category == "Buff";
        if (!wantDebuff && !wantBuff && ids.Count == 0) return [];
        bool Matches(Buff b)
        {
            if (b.Rebirth || b.Rank || b.Scripted) return false;
            var kind = AbnormalKind(b.AbnormalId);
            if (wantDebuff && kind != "DeBuff" || wantBuff && kind != "Buff") return false;
            return ids.Count == 0 || ids.Contains(b.AbnormalId) || ids.Contains(b.Group) || ids.Contains(ClientSkills.AbnormalOf(b.AbnormalId)?.Group ?? -1);
        }
        var gone = buffs.Where(Matches).OrderByDescending(b => b.ExpireAt - b.DurationMs).Take(count).ToList();
        if (gone.Count == 0) return [];
        foreach (var b in gone) { buffs.Remove(b); barrierLeft.Remove(b.Uid); }
        var packets = new List<byte[]> { DispelRemove(playerKey, gone.Select(b => b.Uid).ToList(), casterKey, skill, effectId) };
        if (gone.Any(b => b.Polymorph) && buffs.All(b => !b.Polymorph)) packets.Add(RidePackets.PolymorphUpdate(playerKey, 0));
        if (gone.Any(b => b.Relationship is not null) && buffs.All(b => b.Relationship is null)) packets.Add(GamePackets.RelationshipUpdate(playerKey, OwnRelationship));
        worldPlayer?.Map?.Broadcast(worldPlayer, packets);
        replies.AddRange(packets);
        return gone;
    }

    /// <summary>EAbnormalExpireReason Dispel (OFFICIAL: 0x382c reason 11 after a Dispel_True hit, 79 + 48 samples).</summary>
    const byte ExpireDispel = 11;

    /// <summary>
    /// 0x382c AbnormalRemoveList_NT of a dispel: key | count | {mask 7 (dispeller, skill, effect), uid, reason 11, dispeller varint,
    /// skill i32, effect i32}. OFFICIAL (rec 105448 #7705 `2c38 d9cb01 01 07 2b 0b a162 9045ac00 6a2c4b43`).
    /// </summary>
    internal static byte[] DispelRemove(int key, IReadOnlyList<int> uids, int casterKey, int skill, int effectId)
    {
        var w = PacketWriter.Opcode(0x382c).Varint(key).Varint(uids.Count);
        foreach (var uid in uids) w.U8(7).Varint(uid).U8(ExpireDispel).Varint(casterKey).I32(skill).I32(effectId);
        return w.ToArray();
    }

    /// <summary>
    /// Whether the player's immunities stop a monster's abnormal: an ImmuneAbnormal row listing one of its effect types (unknown
    /// abnormals: crowd control), or ImmuneSkillEffectGroup Negative against a debuff. 0x3805 (mask 0) + 0x382d as the official NPC
    /// immunity (rec 105448 #651).
    /// </summary>
    bool ImmuneToAbnormal(int abnormalId, bool crowdControl, List<byte[]> replies)
    {
        if (buffs.Count == 0) return false;
        var types = (ClientSkills.AbnormalOf(abnormalId)?.Effects.Select(e => e.Type) ?? AbnormalTable.Embedded.Of(abnormalId)?.Effects.Select(e => e.Type))?.ToHashSet();
        var debuff = AbnormalKind(abnormalId) is "DeBuff" || crowdControl;
        foreach (var b in buffs)
            foreach (var (id, type, values) in AbnormalRows(b.AbnormalId, b.Level))
            {
                var blocks = type switch
                {
                    "ImmuneAbnormal" when values.Count > 5 => types is null ? crowdControl
                        : types.Overlaps(values[5].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
                    "ImmuneSkillEffectGroup" when values.Count > 5 && values[5] == "Negative" => debuff,
                    _ => false,
                };
                if (!blocks) continue;
                replies.Add(RidePackets.AbnormalTriggered(playerKey, playerKey, b.Uid, id));
                replies.Add(RidePackets.ImmuneAbnormal(playerKey, [id]));
                return true;
            }
        return false;
    }

    /// <summary>
    /// A hit on this player through its defensive abnormals, before the HP loss: no damage under a Negative immunity, a shield absorbs
    /// (0x3805 mask 0x0a with the attacker, 0x382c reason 7 when used up), a damage transfer sends a share to its caster, the damage
    /// conversions, a reflect back to the monster, and an HP floor (ImmuneDecreaseHP). Returns the damage left.
    /// </summary>
    internal int DefendHit(int damage, int attackerKey, long now, List<byte[]> replies)
    {
        if (damage <= 0 || buffs.Count == 0 || resources is not { } r) return damage;
        var original = damage;
        var active = buffs.Select(b => (Buff: b, Rows: AbnormalRows(b.AbnormalId, b.Level).ToList())).ToList();
        if (active.Any(a => a.Rows.Any(x => x.Type == "ImmuneSkillEffectGroup" && x.Values.Count > 5 && x.Values[5] == "Negative"))) return 0;
        var broken = new List<Buff>();
        foreach (var (b, rows) in active)
            foreach (var (id, type, v) in rows)
            {
                int N(int i) => SkillClient.Int(v, i);
                switch (type)
                {
                    case "HpBarrier" when damage > 0:
                    {
                        if (!barrierLeft.TryGetValue(b.Uid, out var pool)) pool = ShieldPool(v, r.HpMax);
                        var absorbed = Math.Min(pool, damage);
                        if (absorbed <= 0) break;
                        damage -= absorbed;
                        barrierLeft[b.Uid] = pool - absorbed;
                        var absorb = RidePackets.AbnormalDamage(playerKey, attackerKey != 0 ? attackerKey : playerKey, b.Uid, id, absorbed, b.SkillId);
                        worldPlayer?.Map?.Broadcast(worldPlayer, [absorb]);
                        replies.Add(absorb);
                        if (pool - absorbed <= 0) broken.Add(b);
                        break;
                    }
                    case "DamageTransfer" when damage > 0 && !sharingHit && b.CasterKey != 0 && b.CasterKey != playerKey && worldPlayer is { } held
                        && held.Map?.Players.GetValueOrDefault(b.CasterKey) is { } guardian && guardian.Session is { dead: false, resources: not null } g:
                    {
                        var range = FirstNumber(v.Count > 0 ? v[0] : "") is > 0 and var cm ? cm : 4000;
                        if (guardian.Position.DistanceSquared(held.Position) > (double)range * range) break;
                        var share = (int)((long)damage * N(1) / 10_000);
                        if (share <= 0) break;
                        damage -= share;
                        g.sharingHit = true;
                        try { guardian.Send(g.NpcHitsPlayer(share, now, attackerKey)); }
                        finally { g.sharingHit = false; }
                        break;
                    }
                    case "ConvertDamageToMP" when damage > 0 && r.MpMax > 0 && r.Mp * 2L >= r.MpMax:
                    {
                        var mpLoss = (int)((long)damage * N(1) / 10_000);
                        damage = (int)((long)damage * N(0) / 10_000);
                        if (mpLoss > 0) { r.Mp = Math.Max(0, r.Mp - mpLoss); replies.Add(GamePackets.CharPoints(playerKey, [(CharPoint.Mp, r.Mp)])); }
                        break;
                    }
                    case "ConvertDamageToHpHeal" when damage > 0 && (N(2) is <= 0 or >= 10_000 || rng.Next(10_000) < N(2)):
                        damage -= (int)((long)damage * N(0) / 10_000);
                        break;
                    case "DamageReflect" when attackerKey != 0 && (N(2) is <= 0 or >= 10_000 || rng.Next(10_000) < N(2))
                        && (worldPlayer?.Map?.Targets ?? targets)?.GetValueOrDefault(attackerKey) is { Dead: false, Attackable: true } attacker:
                    {
                        var back = (int)((long)original * Roll(rng, N(0), N(1)) / 10_000);
                        if (back <= 0) break;
                        attacker.Hp = Math.Max(1, attacker.Hp - back);   // OURS: a reflect never kills (this runs inside the monster's own AI tick)
                        var hit = GamePackets.SkillHit(attacker.Key, playerKey, b.SkillId, id, back, attackSpeed: AttackSpeed);
                        var hp = GamePackets.NpcHp(attacker.Key, attacker.Hp);
                        worldPlayer?.Map?.Broadcast(worldPlayer, [hit, hp]);
                        replies.Add(hit);
                        replies.Add(hp);
                        break;
                    }
                }
            }
        // Prevents HP from falling below [0]/10000 of max HP (Barrier Spell 18440000: 10%).
        foreach (var (_, rows) in active)
            foreach (var (_, type, v) in rows)
                if (type == "ImmuneDecreaseHP" && SkillClient.Int(v, 0) > 0)
                {
                    var floor = (int)((long)r.HpMax * SkillClient.Int(v, 0) / 10_000);
                    damage = Math.Min(damage, Math.Max(0, r.Hp - floor));
                }
        if (broken.Count > 0)
        {
            foreach (var b in broken) barrierLeft.Remove(b.Uid);
            var speedBefore = MoveSpeedBonus();
            replies.AddRange(RemoveBuffs(broken, ExpireEtc));   // a shield used up: reason 7 (OFFICIAL rec 105448 #36675)
            if (broken.Any(b => b.Stats.Count > 0)) replies.AddRange(BuffStatsChanged(speedBefore));
        }
        return Math.Max(0, damage);
    }

    static int FirstNumber(string text) =>
        int.TryParse(text.Split(',')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    // ---------- Checks ----------

    internal List<byte[]> SupportForChecks(int skill, int targetKey, long now) =>
        SupportCast(skill, new CastRequest(skill, 0, 0, targetKey, 0, position ?? Vector3d.Zero), now);
    internal void SetSkillLevelForW2Checks(int skill, int value) => skillLevels[skill] = value;
    internal (int Hp, int HpMax, int Mp, int MpMax) PointsForW2Checks => resources is { } r ? (r.Hp, r.HpMax, r.Mp, r.MpMax) : (0, 0, 0, 0);
    internal void SetPointsForW2Checks(int hp, int mp) { if (resources is { } r) { r.Hp = Math.Clamp(hp, 1, r.HpMax); r.Mp = Math.Clamp(mp, 0, r.MpMax); } }
    internal int BuffStatForW2Checks(string stat) => BuffStat(stat);
    internal (int Level, int CasterKey, int Heals, int Stats)? BuffForW2Checks(int abnormal) =>
        buffs.FirstOrDefault(b => b.AbnormalId == abnormal) is { } b ? (b.Level, b.CasterKey, b.Heals.Count, b.Stats.Count) : null;
    internal int ShieldLeftForW2Checks(int abnormal) => buffs.FirstOrDefault(b => b.AbnormalId == abnormal) is { } b ? barrierLeft.GetValueOrDefault(b.Uid, -1) : -1;
    internal List<int> NpcAbnormalsForW2Checks(int targetKey) => npcBuffs.GetValueOrDefault(targetKey)?.Select(b => b.AbnormalId).ToList() ?? [];
    internal List<byte[]> BuffTickForW2Checks(long now) => BuffTick(now);
    internal bool ImmuneForW2Checks(int abnormal) => ImmuneToAbnormal(abnormal, false, []);
}
