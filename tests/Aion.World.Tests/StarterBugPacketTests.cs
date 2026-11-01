using System.Reflection;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Starter zones (Poeta / Ishalgen, hero ≤15): party kill credit, accept/give-up gating, env flags.</summary>
public sealed class StarterBugPacketTests
{
    const long Now = 1_760_000_300_000;

    static async Task<(GameSession A, GameSession B)> PartyOnMap1000Async()
    {
        var world = new SharedWorld(GameData.Embedded);
        var storeA = new FakeCharacterStore();
        var storeB = new FakeCharacterStore();
        var a = await EnterShared(world, storeA, "PoetaA", 1);
        var b = await EnterShared(world, storeB, "PoetaB", 2);
        a.Teleport(1000, GamePackets.SpawnPosition, Now);
        b.Teleport(1000, GamePackets.SpawnPosition, Now);
        await a.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await b.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await a.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionManual, PartyOptions.JoinFree, PartyOptions.SpecialNormal, 2)), Now + 1);
        await a.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, b.CharacterName!), Now + 2);
        await b.HandleAsync(SocialRequests.EncodePartyInviteResponse([
            new PartyInviteReply(world.Social.Parties.Values.Single().Key, PartyInviteAnswer.Accept)]), Now + 3);
        return (a, b);
    }

    static async Task<GameSession> EnterShared(SharedWorld world, FakeCharacterStore store, string name, int seed)
    {
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = world, CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(seed)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([1]).U8(0).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static async Task AdvanceToStepAsync(GameSession session, string code, int step)
    {
        if (session.Quests!.Get(code) is null)
            await session.QuestCommandAsync("QuestAdd", code, default);
        for (var guard = 0; guard < 12 && session.Quests!.Get(code) is { Status: QuestLog.Incomplete, Step: var s } && s < step; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", code, default);
    }

    [Fact]
    public async Task Party_kill_credit_reaches_nearby_member_HQ1101018_step_2()
    {
        var (leader, member) = await PartyOnMap1000Async();
        await AdvanceToStepAsync(leader, "HQ1101018", 2);
        await AdvanceToStepAsync(member, "HQ1101018", 2);
        var spot = GamePackets.SpawnPosition;
        leader.Teleport(1000, spot, Now + 10);
        member.Teleport(1000, spot, Now + 10);
        await leader.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 10);
        await member.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 10);
        var kills = (List<int>)typeof(GameSession).GetField("killedNpcs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(member)!;
        kills.Add(2101164);
        var questKills = typeof(GameSession).GetMethod("QuestKillsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var replies = (List<byte[]>)await (Task<List<byte[]>>)questKills.Invoke(member, [default])!;
        Assert.NotEmpty(replies);
        Assert.True(leader.Quests!.Get("HQ1101018")!.Goals[0] >= 1,
            "QuestKillsAsync should fan out kill credit to nearby party members");
    }

    [Fact]
    public async Task HQ1101011_chest_sends_canInteract_when_step_3_opens()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 55);
        await AdvanceToStepAsync(session, "HQ1101011", 2);
        var index = WorldObjects.ObjectsOn(1000).ToList().FindIndex(o => o.Env == 1200530);
        Assert.True(index >= 0);
        session.Teleport(1000, WorldObjects.ObjectsOn(1000)[index].At, Now + 5);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5);
        var (_, replies) = await session.QuestCommandAsync("QuestCompleteStep", "HQ1101011", default);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x363c && p[^1] == 1);
    }

    [Fact]
    public async Task Quest_give_up_restores_watchstone_gating()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 55);
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        await AdvanceToStepAsync(session, "HQ1101018", 4);
        var watch = session.Targets!.Values.First(t => t.NpcId == 2290017);
        watch.Hp = 0;
        watch.CorpseRemoved = true;
        var uid = session.Quests!.Get("HQ1101018")!.Uid;
        await session.HandleAsync(new PacketWriter().U16(0x8d0c).I32((int)uid).ToArray(), Now + 50);
        Assert.Null(session.Quests!.Get("HQ1101018"));
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        await AdvanceToStepAsync(session, "HQ1101018", 4);
        watch = session.Targets!.Values.First(t => t.NpcId == 2290017);
        Assert.False(watch.Dead);
        Assert.True(watch.Hp > 0);
    }

    [Fact]
    public async Task AskNpc_wrong_step_does_not_advance_HQ1101018()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 55);
        await AdvanceToStepAsync(session, "HQ1101018", 2);
        var arno = session.Targets!.Values.FirstOrDefault(t => t.NpcId == 2931095);
        if (arno is null) return;
        var before = session.Quests!.Get("HQ1101018")!.Goals.ToArray();
        await session.HandleAsync(new PacketWriter().U16(0xe240).Varint(arno.Key).ToArray(), Now + 1);
        var after = session.Quests!.Get("HQ1101018")!.Goals.ToArray();
        Assert.Equal(before, after);
    }
}
