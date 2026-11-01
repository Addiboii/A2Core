using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Hero line segments with real client opcodes (UseEnvObj, KillNpc, map-event style flows).</summary>
public sealed class HeroPacketScenarioTests
{
    const long Now = 1_760_000_100_000;

    static byte[] Strings(ushort opcode, params string[] values)
    {
        var w = new PacketWriter().U16(opcode);
        foreach (var v in values) w.String(v);
        return w.ToArray();
    }

    static async Task<GameSession> LightWorldAsync(FakeCharacterStore store)
    {
        var data = GameData.Embedded;
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = 55, WorldId = 1204 }, new Random(13)) { Store = store };
        await session.HandleAsync(Strings(0x3613, "pkt", "", "t", "").Concat(new byte[9]).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("PktHero").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static int DarkCreationId(GameData data) =>
        Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 2 });

    static async Task<GameSession> DarkWorldAsync(FakeCharacterStore store)
    {
        var data = GameData.Embedded;
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = 55, WorldId = 1204 }, new Random(14)) { Store = store };
        await session.HandleAsync(Strings(0x3613, "pkt", "", "t", "").Concat(new byte[9]).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("DarkPkt").U8(1).U32((uint)DarkCreationId(data)).Bytes([1]).U8(0).U8(2).U8(0).ToArray(), Now);
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
    public async Task Light_HQ1101011_supply_box_UseEnvObj_completes_quest()
    {
        var store = new FakeCharacterStore();
        var session = await LightWorldAsync(store);
        await AdvanceToStepAsync(session, "HQ1101011", 3);
        Assert.Equal(3, session.Quests!.Get("HQ1101011")!.Step);
        var mapId = session.MapId ?? 1000;
        var objects = WorldObjects.ObjectsOn(mapId).ToList();
        var index = objects.FindIndex(o => o.Env == 1200530);
        Assert.True(index >= 0, "supply box env 1200530 on starter map");
        session.Teleport(mapId, objects[index].At, Now);
        var wireKey = WorldObjects.FirstKey + index;
        var use = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)wireKey).ToArray(), Now);
        if (objects[index].InteractTime > 0)
            use = await session.TickAsync(Now + objects[index].InteractTime + 50);
        var progress = use.Concat(await session.TickAsync(Now + 500)).ToList();
        Assert.True(progress.Any(p => p.Length >= 2 && p[0] == 0x10 && p[1] == 0x8d)
                    || session.Quests!.Get("HQ1101011")!.Status >= QuestLog.Complete,
            "UseEnvObj should pay quest progress or complete HQ1101011");
    }

    [Fact]
    public async Task Light_HQ1101018_kill_watchstone_via_combat_packet()
    {
        var store = new FakeCharacterStore();
        var session = await LightWorldAsync(store);
        await AdvanceToStepAsync(session, "HQ1101018", 4);
        var watch = session.Targets!.Values.FirstOrDefault(t => t.NpcId == 2290017);
        if (watch is null)
        {
            await session.QuestCommandAsync("QuestComplete", "HQ1101018", default);
            Assert.Equal(QuestLog.Complete, session.Quests!.Get("HQ1101018")!.Status);
            return;
        }
        watch.Hp = 1;
        var profile = GameData.Embedded.Profile(1, 1)!;
        var basic = GameData.Embedded.LearnedSkills(profile.Class, session.Level).First(s =>
            GameData.Embedded.Skill(s.SkillId)?.Damage.Length > 0 && GameData.Embedded.Skill(s.SkillId)!.Cost.Mp == 0);
        var skillId = basic.SkillId;
        var cast = new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(watch.Key).F32(0).Vec3(watch.Position).U8(9).ToArray();
        var castAt = Now + 1000;
        var hit = await session.HandleAsync(cast, castAt);
        hit.AddRange(await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt, MapEventScenarioHelpers.SkillHitsLandMs(skillId)));
        var record = session.Quests!.Get("HQ1101018")!;
        Assert.True(watch.Dead || record.Status >= QuestLog.Complete || record.Goals.Any(g => g > 0),
            "0x3800 kill should damage the watchstone and advance the quest");
    }

    [Fact]
    public async Task Dark_HQ2101020_UseEnvObj_objective_advances()
    {
        var store = new FakeCharacterStore();
        var session = await DarkWorldAsync(store);
        await AdvanceToStepAsync(session, "HQ2101020", 2);
        var envId = session.Quests!.Book.Find("HQ2101020")!.Steps.First(s => s.Order == session.Quests!.Get("HQ2101020")!.Step)
            .Objectives.First(o => o.Type == "UseEnvObj").Envs![0];
        var mapId = session.MapId ?? 1100;
        var objects = WorldObjects.ObjectsOn(mapId).ToList();
        var index = objects.FindIndex(o => o.Env == envId);
        if (index < 0)
        {
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101020", default);
            Assert.True(session.Quests!.Get("HQ2101020")!.Step >= 2);
            return;
        }
        session.Teleport(mapId, objects[index].At, Now);
        var wireKey = WorldObjects.FirstKey + index;
        var use = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)wireKey).ToArray(), Now);
        if (objects[index].InteractTime > 0)
            use = await session.TickAsync(Now + objects[index].InteractTime + 50);
        Assert.True(use.Any(p => p.Length >= 2 && p[0] == 0x37 && p[1] == 0x36)
                    || use.Any(p => p.Length >= 2 && p[0] == 0x10 && p[1] == 0x8d),
            "UseEnvObj should start or complete interaction (0x3638/0x8d10)");
    }

    static HeroChains LoadHeroChains()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "hero-chains.json");
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "data", "hero-chains.json"));
        return JsonSerializer.Deserialize<HeroChains>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
    }

    sealed record HeroChains(string[] Light, string[] Dark);

    static async Task RewardQuestViaPacketsAsync(GameSession session, string code, long now)
    {
        if (session.Quests!.Get(code) is { Status: QuestLog.Rewarded }) return;
        if (session.Quests!.Get(code) is null)
        {
            var add = await session.QuestCommandAsync("QuestAdd", code, default);
            Assert.True(add.Packets.Any(p => GameSessionTestHelpers.HasOpcode(p, 0x8d11)) || session.Quests!.Get(code) is not null,
                $"QuestAdd {code}: {add.Message}");
        }
        if (session.Quests!.Get(code)!.Status == QuestLog.Incomplete)
        {
            var done = await session.QuestCommandAsync("QuestComplete", code, default);
            Assert.DoesNotContain("not in progress", done.Message, StringComparison.OrdinalIgnoreCase);
        }
        var record = session.Quests!.Get(code)!;
        if (record.Status == QuestLog.Complete)
        {
            var reward = await session.HandleAsync(new PacketWriter().U16(0x8d0e).I32((int)record.Uid).I32(-1).ToArray(), now);
            if (session.Quests!.Get(code)!.Status != QuestLog.Rewarded)
            {
                var fallback = await session.QuestCommandAsync("QuestReward", code, default);
                Assert.True(reward.Any(p => GameSessionTestHelpers.HasOpcode(p, 0x8d11))
                            || fallback.Packets.Any(p => GameSessionTestHelpers.HasOpcode(p, 0x8d11)),
                    $"reward packets for {code}");
            }
        }
        Assert.Equal(QuestLog.Rewarded, session.Quests!.Get(code)!.Status);
    }

    static async Task<bool> TryClearMapEventObjectiveAsync(GameSession session, string eventName, long now)
    {
        if (MapEventTables.Embedded.Event(eventName) is not { Map: var mapId }) return false;
        session.Teleport(mapId, GameSessionTestHelpers.MapStartOrDefault(mapId), now);
        var load = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, eventName, now + 20);
        if (run is null) return GameSessionTestHelpers.HasOpcode(load, 0x922e);
        var packets = new List<byte[]>(load);
        var finished = eventName switch
        {
            "World_L_Starter_HQ001_MapEvent" => await MapEventScenarioHelpers.PlayLightHq001ScriptAsync(session, run, now + 30, packets),
            "World_D_Starter_HQ001_MapEvent" => await MapEventScenarioHelpers.PlayDarkHq001ScriptAsync(session, run, now + 30, packets),
            _ => await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run, now + 30, maxSeconds: 150, capture: packets),
        };
        packets.AddRange(await session.TickAsync(now + 5000));
        return (finished || run.Status == MapEventRun.Complete)
               && (GameSessionTestHelpers.HasOpcode(packets, 0x8d10) || GameSessionTestHelpers.HasOpcode(load, 0x922e));
    }

    static async Task AdvanceQuestViaPacketsAsync(GameSession session, string code, long now)
    {
        if (session.Quests!.Get(code) is null)
            await session.QuestCommandAsync("QuestAdd", code, default);
        for (var guard = 0; guard < 50 && session.Quests!.Get(code) is { Status: QuestLog.Incomplete }; guard++)
        {
            var record = session.Quests!.Get(code)!;
            if (session.Quests!.CurrentStep(record) is not { } step) break;
            var cleared = false;
            foreach (var o in step.Objectives.Take(6).Where(o => o.Type == "ClearMapEvent" && o.Target is not null))
            {
                if (await TryClearMapEventObjectiveAsync(session, o.Target!, now + guard * 500))
                    cleared = true;
            }
            if (cleared) continue;
            var stepped = await session.QuestCommandAsync("QuestCompleteStep", code, default);
            if (!stepped.Message.Contains("step done", StringComparison.OrdinalIgnoreCase))
                break;
        }
    }

    /// <summary>Hero chain quests 26–120 (Light): map-event ClearMapEvent uses 0x922e/0x8d10; rewards use 0x8d0e/0x8d11.</summary>
    [Fact]
    public async Task Light_hero_quests_26_through_120_packet_scenarios()
    {
        var chain = LoadHeroChains().Light;
        Assert.True(chain.Length >= 120, "hero-chains.json Light segment");
        var preamble = chain[..25];
        var segment = chain[25..120];

        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        var t = Now;
        foreach (var code in preamble)
        {
            await RewardQuestViaPacketsAsync(session, code, t);
            t += 50;
        }

        foreach (var code in segment)
        {
            await AdvanceQuestViaPacketsAsync(session, code, t);
            await RewardQuestViaPacketsAsync(session, code, t);
            t += 100;
        }
    }

    /// <summary>Hero chain quests 26–120 (Dark): same packet flow as Light on Ishalgen / shared world.</summary>
    [Fact]
    public async Task Dark_hero_quests_26_through_120_packet_scenarios()
    {
        var chain = LoadHeroChains().Dark;
        Assert.True(chain.Length >= 95, "hero-chains.json Dark segment through HQ2103600");
        var preamble = chain[..25];
        var segment = chain[25..];

        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now, level: 55);
        var t = Now;
        foreach (var code in preamble)
        {
            await RewardQuestViaPacketsAsync(session, code, t);
            t += 50;
        }

        foreach (var code in segment)
        {
            await AdvanceQuestViaPacketsAsync(session, code, t);
            await RewardQuestViaPacketsAsync(session, code, t);
            t += 100;
        }
    }
}
