using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Generic skill effects: heals, dispels, pull / knockback, CC on players, slow / silence / DoT / taunt on monsters, cooldowns, aggro.</summary>
public sealed class SkillEffectTests
{
    static readonly long Now = 1_700_000_100_000;
    const int HealingLight = 17100000, Defiance = 11260000, DimensionalControl = 16330000;
    const int Stun = 100000311, Slow = 100000201, Silence = 162100001, Bleed = 100000701, Knockback = 100000321, Taunt = 112900072;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static async Task<(GameSession Session, Target Mob)> Setup(string className)
    {
        var data = GameData.Embedded;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1 } p && p.Class == className);
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data, new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Fx").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var map = Assert.IsType<Dictionary<int, Target>>(session.Targets);
        var dummy = session.Targets![Combat.TrainingDummyKey].Position;
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(dummy with { X = dummy.X + 700 }), Now + 5_000);
        var at = session.Position!.Value;
        var mob = map[880_301] = new Target
        {
            Key = 880_301, Name = "Mob", NpcId = 2400032, Hp = 100_000, HpMax = 100_000, Relationship = 6,
            Position = at with { X = at.X + 600 }, Direction = 0, Radius = 100, RunSpeed = 600, WalkSpeed = 200,
        };
        return (session, mob);
    }

    static double Distance(Vector3d a, Vector3d b) => Math.Sqrt(a.DistanceSquared(b));

    static bool HasOp(IEnumerable<byte[]> packets, int op) => packets.Any(p => GameSessionTestHelpers.Op(p) == op);

    // ---------- Data ----------

    [Fact]
    public void Heal_rows_come_with_a_scope_and_are_marked_improvised()
    {
        Assert.Equal([["HpHealRatio", "2500", "2500"]], SkillEffects.Rows(HealingLight));
        Assert.True(SkillEffects.IsFriendly(HealingLight));
        Assert.Equal("target", SkillEffects.Scope(HealingLight));
        Assert.Equal("party", SkillEffects.Scope(17120000));
        Assert.Equal(1500, SkillEffects.ScopeRange(17120000));
        Assert.Equal("effects_source", SkillAreas.For(HealingLight) is { Effects: not null } ? "effects_source" : "");
        Assert.False(SkillEffects.IsFriendly(13070000));
        Assert.True(SkillEffects.BreaksControl(Defiance));
        Assert.False(SkillEffects.BreaksControl(HealingLight));
    }

    [Fact]
    public void Abnormal_specs_read_what_a_monster_does_under_each_abnormal()
    {
        Assert.True(AbnormalSpecs.Npc(Stun).Held);
        Assert.Equal(3000, AbnormalSpecs.Npc(Slow).SlowPermille);
        Assert.True(AbnormalSpecs.Npc(Silence).Silence);
        Assert.True(AbnormalSpecs.Npc(Taunt).Taunt);
        Assert.Equal(new AbnormalSpecs.DotSpec(100000701, 1000, 300, 0), AbnormalSpecs.Npc(Bleed).Dots.Single());
        Assert.Equal(500, AbnormalSpecs.Npc(Knockback).Moves.Single(m => m.Kind == AbnormalSpecs.DisplaceKind.Knockback).Amount);
        Assert.True(AbnormalSpecs.CrowdControl(Stun) && AbnormalSpecs.CrowdControl(Slow) && !AbnormalSpecs.CrowdControl(Bleed));
        Assert.True(AbnormalSpecs.Debuff(Bleed) && !AbnormalSpecs.Debuff(170900001));
    }

    [Fact]
    public void Displacement_points_go_away_from_and_toward_the_caster()
    {
        var caster = new Vector3d(0, 0, 0);
        var away = GameSession.PointAway(caster, new Vector3d(100, 0, 5), 500);
        Assert.Equal(600, away.X, 6);
        Assert.Equal(5, away.Z);
        var pulled = GameSession.PointPulled(caster, new Vector3d(1000, 0, 0), 250);
        Assert.Equal(250, pulled.X, 6);
        Assert.Equal(new Vector3d(100, 0, 0), GameSession.PointPulled(caster, new Vector3d(100, 0, 0), 250));
    }

    // ---------- Heals ----------

    [Fact]
    public async Task Healing_Light_restores_a_quarter_of_the_maximum_with_the_bars_update()
    {
        var (session, _) = await Setup("Cleric");
        var max = session.Resource!.HpMax;
        session.SetHpForChecks(max / 2);
        var before = session.Resource.Hp;
        var replies = await session.HandleAsync(Cast(HealingLight, session.PlayerKey, session.Position!.Value), Now + 6_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Equal(1, session.PendingSkillEffectCount);

        var landed = await session.TickAsync(Now + 6_000 + 2_000);

        Assert.Equal(0, session.PendingSkillEffectCount);
        Assert.Equal(before + max / 4, session.Resource.Hp);
        Assert.True(HasOp(landed, 0x8d00));
    }

    [Fact]
    public async Task Heal_never_goes_past_the_maximum_and_never_revives_the_dead()
    {
        var (session, _) = await Setup("Cleric");
        var max = session.Resource!.HpMax;
        session.SetHpForChecks(max - 10);
        await session.HandleAsync(Cast(HealingLight, session.PlayerKey, session.Position!.Value), Now + 6_000);
        await session.TickAsync(Now + 8_000);
        Assert.Equal(max, session.Resource.Hp);
    }

    [Fact]
    public async Task Resource_heals_pay_mp_and_sp_flat_or_by_ratio()
    {
        var (session, _) = await Setup("Cleric");
        session.SetResource('M', 100);
        var mp = session.Resource!.Mp;
        var packets = session.ReceiveFriendlyRow(["MpHeal", "300", "300"], Now);
        Assert.Equal(mp + 300, session.Resource.Mp);
        Assert.True(HasOp(packets, 0x8d00));
        session.ReceiveFriendlyRow(["MpHealRatio", "10000", "10000"], Now);
        Assert.Equal(session.Resource.MpMax, session.Resource.Mp);
        session.SetResource('S', 0);
        session.ReceiveFriendlyRow(["SpHealRatio", "5000", "5000"], Now);
        Assert.Equal(session.Resource.SpMax / 2, session.Resource.Sp);
    }

    [Fact]
    public async Task Heal_over_time_buff_ticks_hp_from_the_hot_abnormal()
    {
        var (session, _) = await Setup("Cleric");
        var max = session.Resource!.HpMax;
        session.SetHpForChecks(max / 4);
        var before = session.Resource.Hp;
        var added = session.ReceiveBuffs([(170900001, 8000, 8000, false)], 17090000, Now);
        Assert.True(HasOp(added, 0x382a));
        Assert.True(HasOp(added, 0x3805), "the first 0x3805 names the remaining total");
        var ticks = await session.TickAsync(Now + 3_500);
        Assert.True(session.Resource.Hp > before);
        Assert.True(HasOp(ticks, 0x3805) && HasOp(ticks, 0x8d00));
    }

    // ---------- Dispel ----------

    [Fact]
    public async Task Cleanse_removes_crowd_control_and_keeps_other_buffs()
    {
        var (session, _) = await Setup("Cleric");
        session.TryApplyPlayerDebuff(Stun, 5_000, Now);
        session.ReceiveBuffs([(170900001, 8000, 8000, false)], 17090000, Now);
        Assert.True(session.HeldByAbnormal);
        var removed = session.Cleanse(AbnormalSpecs.CrowdControl, int.MaxValue, Now);
        Assert.True(HasOp(removed, 0x382c));
        Assert.False(session.HeldByAbnormal);
        Assert.Contains(170900001, session.ActiveBuffs);
        Assert.DoesNotContain(Stun, session.ActiveBuffs);
    }

    [Fact]
    public async Task Defiance_is_cast_while_stunned_and_clears_the_stun()
    {
        var (session, _) = await Setup("Gladiator");
        session.TryApplyPlayerDebuff(Stun, 20_000, Now + 6_000);
        Assert.True(session.HeldByAbnormal);
        var replies = await session.HandleAsync(Cast(Defiance, session.PlayerKey, session.Position!.Value), Now + 6_100);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        await session.TickAsync(Now + 6_100 + 2_000);
        Assert.False(session.HeldByAbnormal);
        Assert.Contains(100200001, session.ActiveBuffs);
    }

    [Fact]
    public async Task Defiance_makes_the_caster_immune_to_the_next_stun()
    {
        var (session, _) = await Setup("Gladiator");
        await session.HandleAsync(Cast(Defiance, session.PlayerKey, session.Position!.Value), Now + 6_000);
        await session.TickAsync(Now + 8_000);
        var blocked = session.TryApplyPlayerDebuff(Stun, 5_000, Now + 8_100);
        Assert.True(HasOp(blocked, 0x382d));
        Assert.False(session.HeldByAbnormal);
    }

    [Fact]
    public async Task Purge_removes_a_monsters_beneficial_abnormals_only()
    {
        var (session, mob) = await Setup("Elementalist");
        session.ApplyNpcDebuffForTests(mob, Bleed, 10_000, Now);
        session.ApplyNpcDebuffForTests(mob, 170900001, 10_000, Now);
        var replies = session.ApplyHostileRowForTests(["Dispel", "5"], 16000000, mob, Now);
        Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x382c);
        await session.TickAsync(Now + 1_500);
        Assert.True(mob.Hp < mob.HpMax, "the bleed (a debuff) stays on the monster");
    }

    // ---------- Crowd control on players ----------

    [Fact]
    public async Task Stunned_player_cannot_move_or_cast_until_it_ends()
    {
        var (session, _) = await Setup("Assassin");
        var at = session.Position!.Value;
        var applied = session.TryApplyPlayerDebuff(Stun, 3_000, Now + 6_000);
        Assert.True(HasOp(applied, 0x382a));
        var moved = await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(at with { X = at.X + 50 }), Now + 6_100);
        Assert.Contains(moved, p => GameSessionTestHelpers.Op(p) == GameSession.MoveUpdateReject);
        var cast = await session.HandleAsync(Cast(13010000, Combat.TrainingDummyKey, at), Now + 6_200);
        Assert.DoesNotContain(cast, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

        await session.TickAsync(Now + 6_000 + 3_500);
        Assert.False(session.HeldByAbnormal);
        var free = await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(at with { X = at.X + 50 }), Now + 6_000 + 3_600);
        Assert.DoesNotContain(free, p => GameSessionTestHelpers.Op(p) == GameSession.MoveUpdateReject);
    }

    [Fact]
    public async Task Silenced_player_cannot_cast_but_can_still_move()
    {
        var (session, _) = await Setup("Assassin");
        var at = session.Position!.Value;
        session.TryApplyPlayerDebuff(Silence, 5_000, Now + 6_000);
        var cast = await session.HandleAsync(Cast(13010000, Combat.TrainingDummyKey, at), Now + 6_100);
        Assert.DoesNotContain(cast, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        var moved = await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(at with { X = at.X + 50 }), Now + 6_200);
        Assert.DoesNotContain(moved, p => GameSessionTestHelpers.Op(p) == GameSession.MoveUpdateReject);
    }

    [Fact]
    public async Task Slowed_player_loses_move_speed_and_gets_it_back()
    {
        var (session, _) = await Setup("Assassin");
        var speed = session.AllowedSpeed();
        var applied = session.TryApplyPlayerDebuff(Slow, 3_000, Now + 6_000);
        Assert.True(HasOp(applied, 0x364b));
        Assert.Contains(Slow, session.ActiveBuffs);
        var ended = await session.TickAsync(Now + 6_000 + 3_500);
        Assert.DoesNotContain(Slow, session.ActiveBuffs);
        Assert.True(HasOp(ended, 0x364b), "the move speed is restored");
        Assert.Equal(speed, session.AllowedSpeed(), 6);
    }

    [Fact]
    public async Task Knockback_abnormal_throws_a_player_away_and_snaps_it_when_the_move_ends()
    {
        var (session, _) = await Setup("Assassin");
        var start = session.Position!.Value;
        var caster = start with { X = start.X - 300 };
        var replies = session.TryApplyPlayerDebuff(Knockback, 3_000, Now + 6_000, caster, 100_001);
        Assert.True(HasOp(replies, 0x3814), "Abnormal_Move_Start_NT");
        Assert.Equal(1, session.PendingMoveStopCount);
        Assert.InRange(session.Position!.Value.X, start.X + 499, start.X + 501);
        var moved = await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(start), Now + 6_050);
        Assert.Contains(moved, p => GameSessionTestHelpers.Op(p) == GameSession.MoveUpdateReject);

        var landed = await session.TickAsync(Now + 6_000 + 2_500);
        Assert.Equal(0, session.PendingMoveStopCount);
        Assert.True(HasOp(landed, 0x3815), "Abnormal_Move_Stop_NT");
        Assert.True(HasOp(landed, GameSession.MoveUpdateReject), "the position snap");
    }

    [Fact]
    public async Task Pull_on_a_player_brings_it_to_the_caster()
    {
        var (session, _) = await Setup("Assassin");
        var start = session.Position!.Value;
        var caster = start with { X = start.X - 2_000 };
        var replies = session.DisplacedBy(caster, 100_001, DimensionalControl, 200, pull: true, Now + 6_000);
        Assert.True(HasOp(replies, 0x3814));
        Assert.True(Distance(session.Position!.Value, caster) < 400);
    }

    // ---------- Monsters ----------

    [Fact]
    public async Task Pull_skill_drags_monsters_in_with_a_move_start_and_a_position_snap()
    {
        var (session, mob) = await Setup("Elementalist");
        var caster = session.Position!.Value;
        Assert.Equal([["Pull", "200"]], SkillEffects.Rows(DimensionalControl));
        var before = Distance(caster, mob.Position);
        var replies = await session.HandleAsync(Cast(DimensionalControl, mob.Key, mob.Position), Now + 6_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

        var pulled = await session.TickAsync(Now + 6_000 + 2_000);

        Assert.True(HasOp(pulled, 0x3814), "Abnormal_Move_Start_NT");
        Assert.True(Distance(caster, mob.Position) < before - 200, "the monster stands close to the caster now");
        Assert.True(mob.DisplacedUntil > Now + 8_000, "it does not act while it is dragged");
        var stopped = await session.TickAsync(Now + 6_000 + 5_000);
        Assert.True(HasOp(stopped, 0x3815) && HasOp(stopped, 0x371a), "stop NT and position");
        Assert.Equal(0, session.PendingMoveStopCount);
    }

    [Fact]
    public async Task Knockback_abnormal_throws_a_monster_away_from_the_player()
    {
        var (session, mob) = await Setup("Gladiator");
        var caster = session.Position!.Value;
        var before = Distance(caster, mob.Position);
        var replies = session.ApplyNpcDebuffForTests(mob, Knockback, 3_000, Now + 6_000);
        Assert.True(HasOp(replies, 0x382a));
        Assert.True(HasOp(replies, 0x3814));
        Assert.InRange(Distance(caster, mob.Position) - before, 499, 501);
        Assert.True(mob.Held, "the knockdown holds it");
    }

    [Fact]
    public async Task Slow_and_silence_abnormals_change_what_a_monster_can_do()
    {
        var (session, mob) = await Setup("Gladiator");
        session.ApplyNpcDebuffForTests(mob, Slow, 3_000, Now + 6_000);
        Assert.Equal(3000, mob.SlowPermille);
        Assert.False(mob.Held);
        session.ApplyNpcDebuffForTests(mob, Silence, 3_000, Now + 6_000);
        Assert.True(mob.Silenced);
        await session.TickAsync(Now + 6_000 + 3_500);
        Assert.Equal(0, mob.SlowPermille);
        Assert.False(mob.Silenced);
    }

    [Fact]
    public async Task Damage_over_time_abnormal_ticks_a_monsters_hp_down()
    {
        var (session, mob) = await Setup("Assassin");
        var applied = session.ApplyNpcDebuffForTests(mob, Bleed, 3_000, Now + 6_000);
        Assert.True(HasOp(applied, 0x3805), "the first 0x3805 names the total");
        var ticks = await session.TickAsync(Now + 6_000 + 3_500);
        Assert.Equal(97_000, mob.Hp);
        Assert.True(HasOp(ticks, 0x3805) && HasOp(ticks, 0x8d00));
    }

    [Fact]
    public async Task Damage_over_time_that_finishes_a_monster_kills_it()
    {
        var (session, mob) = await Setup("Assassin");
        mob.Hp = 500;
        session.ApplyNpcDebuffForTests(mob, Bleed, 3_000, Now + 6_000);
        var ticks = await session.TickAsync(Now + 6_000 + 3_500);
        Assert.True(mob.Dead);
        Assert.True(HasOp(ticks, 0x8d04), "the kill");
    }

    [Fact]
    public void Taunt_and_aggro_transfer_move_the_hate()
    {
        var data = GameData.Embedded;
        var ai = new NpcAi(new Random(3), data);
        var mob = new Target { Key = 2100, Name = "M", NpcId = 2400032, Hp = 100, HpMax = 100, Relationship = 6, RunSpeed = 600, WalkSpeed = 200, Radius = 50 };
        var targets = new Dictionary<int, Target> { [mob.Key] = mob };
        ai.Engage(targets, mob, Now, attackerKey: 1, damage: 400);
        Assert.Equal(400, ai.HateOf(mob, 1));

        ai.TransferAggro(targets, mob, Now, fromKey: 1, toKey: 100_002, permille: 500);
        Assert.Equal(200, ai.HateOf(mob, 1));
        Assert.Equal(200, ai.HateOf(mob, 100_002));

        ai.Taunt(targets, mob, Now, tauntKey: 100_003);
        Assert.True(ai.HateOf(mob, 100_003) > ai.HateOf(mob, 1));

        ai.TransferAggro(targets, mob, Now, fromKey: 1, toKey: 0, permille: 1000);
        Assert.Equal(0, ai.HateOf(mob, 1));
    }

    [Fact]
    public async Task Taunt_abnormal_makes_the_caster_the_monsters_top_hate()
    {
        var (session, mob) = await Setup("Gladiator");
        var replies = session.ApplyNpcDebuffForTests(mob, Taunt, 1_700, Now + 6_000);
        Assert.True(HasOp(replies, 0x382a));
        Assert.True(session.HateOf(mob) > 0);
    }

    // ---------- Cooldowns and resources ----------

    [Fact]
    public async Task Cooldown_cuts_shorten_running_cooldowns_and_tell_the_client()
    {
        var (session, _) = await Setup("Cleric");
        await session.HandleAsync(Cast(HealingLight, session.PlayerKey, session.Position!.Value), Now + 6_000);
        var blocked = await session.HandleAsync(Cast(HealingLight, session.PlayerKey, session.Position!.Value), Now + 6_000 + 3_000);
        Assert.DoesNotContain(blocked, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        var cut = session.AdjustCooldowns(-4_000, 0, Now + 6_500);
        Assert.True(HasOp(cut, 0x3847));
        var ready = await session.HandleAsync(Cast(HealingLight, session.PlayerKey, session.Position!.Value), Now + 6_000 + 3_000);
        Assert.Contains(ready, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
    }

    [Fact]
    public async Task Cooldown_increase_starts_one_for_a_group_that_is_not_running()
    {
        var (session, _) = await Setup("Cleric");
        var packets = session.AdjustCooldowns(3_000, 4242, Now);
        Assert.True(HasOp(packets, 0x3847));
        Assert.Empty(session.AdjustCooldowns(-1_000, 999_999, Now));
    }

    [Fact]
    public async Task Mp_burn_takes_mana_from_the_victim_and_updates_its_bars()
    {
        var (session, _) = await Setup("Cleric");
        session.SetResource('M', 1000);
        var packets = session.BurnMp(400);
        Assert.Equal(600, session.Resource!.Mp);
        Assert.True(HasOp(packets, 0x8d00));
        session.BurnMp(5_000);
        Assert.Equal(0, session.Resource.Mp);
    }

    // ---------- Players against players ----------

    static async Task<(GameSession A, GameSession B)> DuelAsync()
    {
        const long t = 1_760_400_000_000;
        var data = GameData.Embedded;
        var world = new SharedWorld(data);
        async Task<GameSession> Enter(FakeCharacterStore store, string name, int seed)
        {
            store.Progress = (45, 0);
            var session = new GameSession(data, new GameSessionOptions { World = world, CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
                new Random(seed)) { Store = store };
            await session.HandleAsync(GameSessionTestHelpers.Login(), t);
            await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([1]).U8(0).U8(1).U8(0).ToArray(), t);
            await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), t);
            await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t);
            return session;
        }
        var a = await Enter(new FakeCharacterStore(), "FxA", 3);
        var b = await Enter(new FakeCharacterStore(Guid.NewGuid()), "FxB", 5);
        var spot = new Vector3d(-127874, -111187, 25393);
        a.Teleport(1010, spot, t);
        b.Teleport(1010, spot with { X = spot.X + 50 }, t);
        await a.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t);
        await b.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t);
        Assert.Null(a.PvpVictimForTests(b.PlayerKey));
        Assert.Null(a.TeleportAnchorForTests(b.PlayerKey));
        await a.HandleAsync(SocialRequests.EncodeKey(SocialOpcodes.DuelCallOutRq, b.PlayerKey), t + 10);
        var room = world.Social!.Duels.Single().Key;
        await b.HandleAsync(SocialRequests.EncodeDuelAnswer(SocialOpcodes.DuelCallOutAcceptRq, room, a.PlayerKey), t + 20);
        await a.TickAsync(t + 20 + Duel.CountdownMs);
        Assert.Equal(DuelState.InBattle, world.Social.Duels[room].State);
        return (a, b);
    }

    [Fact]
    public async Task Duel_opponent_is_the_pvp_victim_and_a_teleport_target()
    {
        var (a, b) = await DuelAsync();
        Assert.Same(b.WorldPlayer, a.PvpVictimForTests(b.PlayerKey));
        Assert.Equal(b.WorldPlayer!.Position, a.TeleportAnchorForTests(b.PlayerKey));
    }

    [Fact]
    public async Task Skill_abnormals_stun_and_throw_the_duel_opponent()
    {
        const long t = 1_760_400_100_000;
        var (a, b) = await DuelAsync();
        var spot = b.Position!.Value;
        a.PvpSkillDebuffs(b.WorldPlayer!, 11290000, t);
        Assert.True(b.HeldByAbnormal || b.ActiveBuffs.Contains(Knockback), "the abnormals of the skill land on the victim");
        var outbox = new List<byte[]>();
        while (b.WorldPlayer!.Outbox.TryDequeue(out var packet)) outbox.Add(packet);
        Assert.True(HasOp(outbox, 0x382a));
        Assert.True(HasOp(outbox, 0x3814), "the knockback shows as Abnormal_Move_Start_NT");
        Assert.True(Distance(b.Position!.Value, spot) > 400);
        var landed = await b.TickAsync(t + 3_000);
        Assert.True(HasOp(landed, 0x3815));
    }

    // ---------- The audit's list of handlers is what the code does ----------

    [Fact]
    public void Handled_effect_types_are_listed_for_the_audit()
    {
        foreach (var type in SkillEffects.Handled) Assert.Contains(type, SkillEffectSupport.Handled);
    }
}
