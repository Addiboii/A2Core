using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Death, CharRespawn_RQ, CharDie layout, and auto-rez timing (build pin 25767555).</summary>
public class DeathResurrectTests
{
    const long Now = 1_700_000_000_000;

    static async Task<GameSession> EnterAscendedAsync(FakeCharacterStore store, long now, GameSessionOptions? options = null)
    {
        var shared = new SharedWorld(GameData.Embedded);
        var session = new GameSession(GameData.Embedded,
            options ?? new GameSessionOptions { World = shared, CharacterLevel = 10, WorldId = store.WorldId, TrainingDummy = true },
            new Random(31))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        session.Teleport(1010, GameSessionTestHelpers.MapStartOrDefault(1010), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 1);
        return session;
    }

    [Fact]
    public void CharDie_player_PvE_matches_usmap_body_length()
    {
        var ours = GamePackets.Kill(1, 11010000, 2001, 0, "", dieType: 2, npcCasterDataId: 55_001, npcDieActId: 0, reason: 1);
        Assert.Equal(0x8d04, ours[0] | (ours[1] << 8));
        Assert.Equal(2, ours[^1]);
        Assert.Equal(0u, BitConverter.ToUInt32(ours, ours.Length - 9));
        Assert.Equal(1u, BitConverter.ToUInt32(ours, ours.Length - 5));
    }

    [Fact]
    public async Task CharDie_on_player_death_carries_resurrect_dat_timers()
    {
        const long t = Now + 50_000;
        var session = await EnterAscendedAsync(new FakeCharacterStore(), t);
        var death = session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001, killerSkill: 11010000);
        var kill = death.First(p => GameSessionTestHelpers.Op(p) == 0x8d04);
        Assert.Equal(0u, BitConverter.ToUInt32(kill, kill.Length - 9));
        Assert.Equal(1u, BitConverter.ToUInt32(kill, kill.Length - 5));
    }

    [Fact]
    public async Task Short_CharRespawn_RQ_revives_at_respawn_point()
    {
        const long t = Now + 150_000;
        var session = await EnterAscendedAsync(new FakeCharacterStore(), t);
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
        Assert.True(session.Dead);
        var rez = await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).U8(0).ToArray(), t + 2000);
        Assert.Contains(rez, p => p.SequenceEqual(RidePackets.RespawnReply(0)));
        Assert.False(session.Dead);
        Assert.Contains(rez, p => GameSessionTestHelpers.Op(p) == 0x3647);
        Assert.Contains(rez, p => GameSessionTestHelpers.Op(p) == 0x3621);
    }

    [Fact]
    public void Auto_resurrect_delay_follows_dat_and_development_override()
    {
        var data = GameData.Embedded;
        Assert.Equal(60_000, data.AutoResurrectMs(1));
        Assert.Equal(0, data.DefaultResurrectWaitMs(1));
        var dev = new GameSession(data, new GameSessionOptions { AutoResurrectMsOverride = 60_000 }, new Random(1));
        Assert.Equal(60_000, dev.ResurrectAutoDelayMs);
    }

    [Fact]
    public async Task Manual_default_respawn_replies_and_clears_dead()
    {
        const long t = Now + 100_000;
        var session = await EnterAscendedAsync(new FakeCharacterStore(), t);
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
        Assert.True(session.Dead);
        var rez = await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).I64(0).ToArray(), t + 2000);
        Assert.Contains(rez, p => p.SequenceEqual(RidePackets.RespawnReply(0)));
        Assert.False(session.Dead);
        Assert.Contains(rez, p => GameSessionTestHelpers.Op(p) == 0x3647);
        Assert.Contains(rez, p => GameSessionTestHelpers.Op(p) == 0x3621);
    }

    [Fact]
    public async Task Auto_resurrect_includes_respawn_reply()
    {
        const long t = Now + 200_000;
        var store = new FakeCharacterStore();
        var session = await EnterAscendedAsync(store, t,
            new GameSessionOptions
            {
                AutoResurrectMsOverride = 5_000,
                World = new SharedWorld(GameData.Embedded),
                CharacterLevel = 10,
                WorldId = store.WorldId,
                TrainingDummy = true,
            });
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
        var tick = await session.TickAsync(t + 1000 + 5_000);
        Assert.Contains(tick, p => p.SequenceEqual(RidePackets.RespawnReply(0)));
        Assert.False(session.Dead);
    }

    [Fact]
    public async Task Load_complete_after_revive_restores_hp()
    {
        const long t = Now + 300_000;
        var session = await EnterAscendedAsync(new FakeCharacterStore(), t);
        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 1000, killerKey: 2001);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).I64(0).ToArray(), t + 2000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 2100);
        Assert.True(session.Resource.Hp > 0);
        Assert.Equal(session.Resource.HpMax, session.Resource.Hp);
    }
}
