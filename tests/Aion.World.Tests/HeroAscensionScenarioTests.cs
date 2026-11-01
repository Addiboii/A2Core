using System.Text.Json;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// GameSession flows for the starter hero line through ascension quests (real opcodes where covered by Native.Checks).
/// </summary>
public sealed class HeroAscensionScenarioTests
{
    const long Now = 1_760_000_000_123;

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static string[] LightChainThrough(string lastCode)
    {
        var chains = LoadChains();
        var end = Array.IndexOf(chains.Light, lastCode);
        Assert.True(end >= 0, $"{lastCode} not in Light chain");
        return chains.Light[..(end + 1)];
    }

    static string[] DarkChainThrough(string lastCode)
    {
        var chains = LoadChains();
        var end = Array.IndexOf(chains.Dark, lastCode);
        Assert.True(end >= 0, $"{lastCode} not in Dark chain");
        return chains.Dark[..(end + 1)];
    }

    static HeroChains LoadChains()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "hero-chains.json");
        return JsonSerializer.Deserialize<HeroChains>(File.ReadAllText(path), JsonOptions)!;
    }

    sealed record HeroChains(string[] Light, string[] Dark);

    static int DarkCreationId(GameData data) =>
        Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 2 });

    static byte[] LoginRequest() =>
        Strings(0x3613, "test-session", "", "t", "").Concat(new byte[9]).ToArray();

    static byte[] Strings(ushort opcode, params string[] values)
    {
        var w = new PacketWriter().U16(opcode);
        foreach (var v in values) w.String(v);
        return w.ToArray();
    }

    static byte[] CreatePacket(GameData data, int race, string name)
    {
        if (race == 2)
            return new PacketWriter().U16(0x361b).String(name).U8(1).U32((uint)DarkCreationId(data)).Bytes([1]).U8(0).U8(2).U8(0).ToArray();
        return new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray();
    }

    static async Task<GameSession> EnterWorldAsync(FakeCharacterStore store, int race)
    {
        var data = GameData.Embedded;
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = 55, WorldId = 1204 }, new Random(11))
        { Store = store };
        if (store.States.Count == 0)
        {
            await session.HandleAsync(LoginRequest(), Now);
            await session.HandleAsync(CreatePacket(data, race, race == 2 ? "DarkHero" : "LightHero"), Now);
        }
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static async Task RewardQuestAsync(GameSession session, string code)
    {
        var record = session.Quests!.Get(code)!;
        if (record.Status == QuestLog.Incomplete)
        {
            var done = await session.QuestCommandAsync("QuestComplete", code, default);
            Assert.DoesNotContain("not in progress", done.Message, StringComparison.OrdinalIgnoreCase);
        }
        record = session.Quests!.Get(code)!;
        Assert.Equal(QuestLog.Complete, record.Status);
        var reward = await session.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)record.Uid).I32(0).ToArray(), Now);
        Assert.Contains(reward, p => p.Length >= 2 && p[0] == 0x0f && p[1] == 0x8d);
    }

    static async Task WalkChainAsync(GameSession session, FakeCharacterStore store, int race, string[] codes, string terminalCode)
    {
        var opening = race == 2 ? DevelopmentDefaults.OpeningQuestDark : DevelopmentDefaults.OpeningQuestLight;
        Assert.Contains(store.Quests, q => q.Code == opening && q.Status == QuestLog.Rewarded);

        foreach (var code in codes.Skip(1))
        {
            if (session.Quests!.Get(code) is null)
            {
                var add = await session.QuestCommandAsync("QuestAdd", code, default);
                if (add.Message.Contains("not", StringComparison.OrdinalIgnoreCase))
                    Assert.True(session.Quests!.CanAccept(code, session.Level), $"Cannot accept {code}: {add.Message}");
            }
            await RewardQuestAsync(session, code);
            Assert.Equal(QuestLog.Rewarded, session.Quests!.Get(code)!.Status);
        }

        Assert.Equal(QuestLog.Rewarded, session.Quests!.Get(terminalCode)!.Status);
    }

    [Fact]
    public async Task Light_HQ1101010_talk_and_reward_advances_chain()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, race: 1);
        Assert.Equal("HQ1100010", store.Quests.First(q => q.Status == QuestLog.Rewarded).Code);
        Assert.Equal("HQ1101010", session.Quests!.Get("HQ1101010")!.Code);
        var nania = session.Targets!.Values.First(n => n.NpcId == 2930635);
        var dialog = await session.HandleAsync(new PacketWriter().U16(0xe240).I32(nania.Key).ToArray(), Now);
        Assert.Contains(dialog, b => b[0] == 0x41 && b[1] == 0xe2);
        Assert.Equal(1, session.Quests!.Get("HQ1101010")!.Step);
        await session.HandleAsync(new PacketWriter().U16(0xe242).I32(nania.Key).ToArray(), Now);
        Assert.Equal(2, session.Quests!.Get("HQ1101010")!.Step);
        await RewardQuestAsync(session, "HQ1101010");
        Assert.Equal(QuestLog.Rewarded, session.Quests!.Get("HQ1101010")!.Status);
        Assert.Null(session.Quests!.Get("HQ1101011"));
        Assert.True(session.Quests!.CanAccept("HQ1101011", session.Level));
    }

    [Theory]
    [InlineData(1, "HQ1101020")]
    [InlineData(2, "HQ2101050")]
    public async Task Hero_line_through_ascension_with_reward_packets(int race, string ascensionCode)
    {
        var codes = race == 1 ? LightChainThrough(ascensionCode) : DarkChainThrough(ascensionCode);
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, race);
        await WalkChainAsync(session, store, race, codes, ascensionCode);
        var glide = await session.HandleAsync(new PacketWriter().U16(0x3706).ToArray(), Now);
        Assert.Contains(glide, p => p.Length >= 2 && p[0] == 0x43 && p[1] == 0x37);
    }

    [Theory]
    [InlineData(1, "HQ1101021")]
    [InlineData(2, "HQ2101060")]
    public async Task Hero_line_first_subspace_quest_after_ascension(int race, string subspaceCode)
    {
        var ascension = race == 1 ? "HQ1101020" : "HQ2101050";
        var codes = race == 1 ? LightChainThrough(subspaceCode) : DarkChainThrough(subspaceCode);
        var store = new FakeCharacterStore();
        var session = await EnterWorldAsync(store, race);
        var throughAscension = codes[..(Array.IndexOf(codes, ascension) + 1)];
        await WalkChainAsync(session, store, race, throughAscension, ascension);
        Assert.Equal(QuestLog.Rewarded, session.Quests!.Get(ascension)!.Status);
        if (session.Quests!.Get(subspaceCode) is null)
            Assert.True(session.Quests!.CanAccept(subspaceCode, session.Level), $"{subspaceCode} should unlock after ascension");
        await RewardQuestAsync(session, subspaceCode);
        Assert.Equal(QuestLog.Rewarded, session.Quests!.Get(subspaceCode)!.Status);
    }
}
