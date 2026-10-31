using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Combat and field-mob playability (levels 1–45, build pin 25767555): data curve, exp, packets.</summary>
public sealed class CombatParityTests
{
    static readonly int[] FieldMaps = [1010, 1011, 1100, 1110, 1111, 20, 22, 23];
    const long Now = 1_760_200_000_000;
    static GameData Data => GameSessionTestHelpers.LiveGameData;

    public static IEnumerable<object[]> Levels1To45() =>
        Enumerable.Range(1, 45).Select(l => new object[] { l });

    [Theory]
    [MemberData(nameof(Levels1To45))]
    public void Hp_for_level_is_positive_and_monotonic(int level)
    {
        var hp = Combat.HpForLevel(level);
        Assert.InRange(hp, 1, 200_000);
        if (level > 1)
            Assert.True(hp >= Combat.HpForLevel(level - 1), $"level {level} HP should not drop");
    }

    [Theory]
    [MemberData(nameof(Levels1To45))]
    public void Kill_experience_is_disabled(int level)
    {
        var session = new GameSession(Data, new GameSessionOptions { CharacterLevel = level, LevelCap = Data.MaxLevel }, new Random(level));
        Assert.Equal(0, session.KillExperience(level));
        Assert.Equal(0, session.KillExperience(Math.Max(1, level - 11)));
    }

    [Theory]
    [MemberData(nameof(Levels1To45))]
    public void Representative_field_spawn_has_valid_combat_data(int level)
    {
        var spawn = RepresentativeFieldSpawn(level);
        if (spawn is null)
        {
            // Sparse placement at this level on field maps (documented in audit_combat.py caveats).
            return;
        }
        var hp = Combat.HpFor(spawn);
        Assert.InRange(hp, 1, 500_000);
        if (spawn.Aggressive)
        {
            Assert.NotNull(spawn.Aggro);
            Assert.NotEmpty(spawn.Skills ?? []);
        }
    }

    [Theory]
    [MemberData(nameof(Levels1To45))]
    public void Loot_tables_roll_without_error(int level)
    {
        var spawn = RepresentativeFieldSpawn(level);
        if (spawn is null || !LootTables.Embedded.HasLoot(spawn.NpcId)) return;
        var rolls = LootTables.Embedded.Roll(spawn.NpcId, 1, new Random(level + 99));
        Assert.NotNull(rolls);
    }

    static NpcSpawn? RepresentativeFieldSpawn(int level)
    {
        foreach (var mapId in FieldMaps)
        {
            var hit = Data.Spawns(mapId).FirstOrDefault(s =>
                s.Level == level && s.Relationship == 6 && s.Function == "None");
            if (hit is not null) return hit;
        }
        return null;
    }

    [Fact]
    public async Task Packet_kill_drop_pickup_and_mob_respawn_on_field_map()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 10);
        var spawn = Data.Spawns(1010).First(s => LootTables.Embedded.HasLoot(s.NpcId) && s.Relationship == 6);
        var at = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
        session.Teleport(1010, at, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var target = session.Targets!.Values.First(t => t.NpcId == spawn.NpcId);
        target.Hp = 1;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var castAt = Now + 1000;
        var kill = await MapEventScenarioHelpers.CombatCastAsync(session, skill, target.Key, at, castAt);
        var landAt = castAt + MapEventScenarioHelpers.SkillHitsLandMs(skill);
        Assert.True(target.Dead);
        Assert.Contains(kill, p => p.Length >= 2 && GameSessionTestHelpers.Op(p) == 0x561e);
        var uid = session.GroundDrops.First();
        var loot = await session.HandleAsync(new PacketWriter().U16(0x5620).Varint(1).I64(uid).U8(0).ToArray(), landAt + 100);
        Assert.Contains(loot, p => p.SequenceEqual(LootPackets.LootReply(0, uid, false)));
        var respawn = await session.TickAsync(landAt + GameSession.RespawnMs + 50);
        Assert.Contains(respawn, p => p.SequenceEqual(GamePackets.Death(target.Key)));
        Assert.Equal(target.HpMax, target.Hp);
    }

    [Fact]
    public async Task Rest_triples_hp_regen_out_of_combat()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        await session.HandleAsync(new PacketWriter().U16(0x3747).Varint(session.PlayerKey).I32(0).ToArray(), Now);
        Assert.True(session.Resting);
        session.SetResource('H', session.Resource!.HpMax / 2);
        var before = session.Resource.Hp;
        await session.TickAsync(Now + 1000);
        var sitting = session.Resource.Hp - before;
        await session.HandleAsync(new PacketWriter().U16(0x374a).Varint(session.PlayerKey).I32(0).ToArray(), Now + 1000);
        session.SetResource('H', session.Resource.HpMax / 2);
        before = session.Resource.Hp;
        await session.TickAsync(Now + 2000);
        var standing = session.Resource.Hp - before;
        Assert.True(sitting > 0 && standing > 0);
        Assert.Equal(standing * GameSession.RestRegenFactor, sitting);
    }

    [Fact]
    public async Task Party_member_on_same_map_gets_exp_share_packet()
    {
        var world = new SharedWorld(Data);
        var storeA = new FakeCharacterStore();
        var storeB = new FakeCharacterStore();
        var alice = await EnterPartyWorld(world, storeA, "Alice", 12);
        var bob = await EnterPartyWorld(world, storeB, "Bob", 13);
        await alice.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionManual, PartyOptions.JoinFree, PartyOptions.SpecialNormal, 2)), Now);
        var partyKey = world.Social.Parties.Values.Single().Key;
        await alice.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, "Bob"), Now + 1);
        await bob.HandleAsync(SocialRequests.EncodePartyInviteResponse([new PartyInviteReply(partyKey, PartyInviteAnswer.Accept)]), Now + 2);
        var spot = new Vector3d(-127874, -111187, 25393);
        alice.Teleport(1010, spot, Now + 3);
        bob.Teleport(1010, spot, Now + 3);
        await alice.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 3);
        await bob.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 3);
        var before = bob.Experience;
        bob.ShareExperience(40);
        var replies = await bob.TickAsync(Now + 4);
        Assert.Equal(before + 40, bob.Experience);
        Assert.Contains(replies, p => p.SequenceEqual(GamePackets.ExpUpdate(before + 40, 40, 1)));
    }

    [Fact]
    public async Task Party_ground_drop_not_registered_on_other_map()
    {
        var world = new SharedWorld(Data);
        var storeA = new FakeCharacterStore();
        var storeB = new FakeCharacterStore();
        var alice = await EnterPartyWorld(world, storeA, "Leader", 20);
        var bob = await EnterPartyWorld(world, storeB, "Far", 21);
        await alice.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionManual, PartyOptions.JoinFree, PartyOptions.SpecialNormal, 2)), Now);
        var partyKey = world.Social.Parties.Values.Single().Key;
        world.Social.Parties.Values.Single().Members.Add(bob.WorldPlayer!);
        alice.Teleport(1010, new Vector3d(-127874, -111187, 25393), Now);
        bob.Teleport(1011, new Vector3d(-127874, -111187, 25393), Now);
        await alice.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await bob.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var spawn = Data.Spawns(1010).First(s => LootTables.Embedded.HasLoot(s.NpcId) && s.Relationship == 6);
        var target = alice.Targets!.Values.First(t => t.NpcId == spawn.NpcId);
        target.Hp = 1;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(alice);
        await MapEventScenarioHelpers.CombatCastAsync(session: alice, skill, target.Key, alice.Position!.Value, Now + 100);
        Assert.NotEmpty(alice.GroundDrops);
        Assert.Empty(bob.GroundDrops);
    }

    static async Task<GameSession> EnterPartyWorld(SharedWorld world, FakeCharacterStore store, string name, int wireSuffix)
    {
        var session = new GameSession(Data,
            new GameSessionOptions { World = world, CharacterLevel = 15, WorldId = store.WorldId, TrainingDummy = true },
            new Random(wireSuffix))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }
}
