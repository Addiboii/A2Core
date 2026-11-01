using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Core gameplay loop E2E on <see cref="SharedWorld"/> with the native TCP packet harness (build 25767555, level cap 45):
/// login, create, enter, move, combat, loot, equip, level-up, death, potions, NPC shop, return scroll, logout, relogin.
/// </summary>
public sealed class CoreLoopE2ETests
{
    const long Now = 1_760_400_000_000;
    const int LifePotion = 530126003;
    const int ReturnScroll = 530560001;
    const int SpiritStone = 502760001;
    const int GrocerGoodsId = 103015101;
    static GameData Data => GameSessionTestHelpers.LiveGameData;

    static async Task<(GameSession Session, FakeCharacterStore Store, SharedWorld World, int WireId)> CreateAndEnterAsync(long now)
    {
        var store = new FakeCharacterStore();
        var world = new SharedWorld(Data);
        var session = new GameSession(Data,
            new GameSessionOptions { World = world, CharacterLevel = 1, WorldId = store.WorldId, TrainingDummy = true, LevelCap = Data.MaxLevel },
            new Random(45)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("CoreLoop").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), now);
        return (session, store, world, wireId);
    }

    static async Task<GameSession> ReloginAsync(FakeCharacterStore store, SharedWorld world, int wireId, long now)
    {
        var session = new GameSession(Data,
            new GameSessionOptions { World = world, WorldId = store.WorldId, TrainingDummy = true, LevelCap = Data.MaxLevel },
            new Random(46)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), now);
        return session;
    }

    [Fact]
    public async Task Cap45_shared_world_loop_persists_through_logout_and_relogin()
    {
        var t = Now;
        var (session, store, world, wireId) = await CreateAndEnterAsync(t);
        Assert.True(session.InWorld);
        Assert.Equal(1, session.Level);
        Assert.Equal(45, Data.MaxLevel);

        var questRow = new QuestRecord("HQ1101018", 1101018, 9001, QuestLog.Incomplete, 1, new int[6]);
        session.DevelopmentQuestLog().Set(questRow);
        await store.SaveQuestAsync(store.States[0].Summary.CharacterId, questRow, default);

        var fieldSpot = new Vector3d(-127874, -111187, 25393);
        session.Teleport(1010, fieldSpot, t);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(fieldSpot with { X = fieldSpot.X + 400 }), t + 500);
        Assert.Equal(fieldSpot with { X = fieldSpot.X + 400 }, session.Position);

        var spawn = Data.Spawns(1010).First(s => LootTables.Embedded.HasLoot(s.NpcId) && s.Relationship == 6);
        var target = session.Targets!.Values.First(m => m.NpcId == spawn.NpcId);
        target.Hp = 1;
        var basic = MapEventScenarioHelpers.BasicAttackSkill(session);
        var killReplies = await MapEventScenarioHelpers.CombatCastAsync(session, basic, target.Key, session.Position!.Value, t + 1000);
        Assert.True(target.Dead);
        Assert.Contains(killReplies, p => GameSessionTestHelpers.Op(p) == 0x561e);
        var dropUid = session.GroundDrops.First();
        await session.HandleAsync(new PacketWriter().U16(0x5620).Varint(1).I64(dropUid).U8(0).ToArray(), t + 1100);

        var className = Data.Profile(1, 1)!.Class;
        var learnedSkill = Data.LearnedSkills(className, session.Level).First(s => Data.Skill(s.SkillId)?.Cost.Mp > 0).SkillId;
        var freshTarget = session.Targets!.Values.First(m => !m.Dead && m.Relationship == 6);
        freshTarget.Hp = freshTarget.HpMax;
        await MapEventScenarioHelpers.CombatCastAsync(session, learnedSkill, freshTarget.Key, session.Position!.Value, t + 1200);
        await MapEventScenarioHelpers.CombatCastAsync(session, basic, freshTarget.Key, session.Position!.Value, t + 1300);

        store.Items.Add(FakeCharacterStore.BagItem(210130001, 1, 200));
        await session.InventoryChangesAsync(default);
        var torsoKey = store.Items.Single(i => i.WireId == 200).WireId;
        var equip = await session.HandleAsync(new PacketWriter().U16(0xe247).I64(torsoKey).U8(5).ToArray(), t + 1400);
        Assert.Equal(0, BitConverter.ToUInt16(equip[0], 2));
        Assert.Equal("equipment", store.Items.Single(i => i.WireId == torsoKey).Location);
        var unequip = await session.HandleAsync(new PacketWriter().U16(0xe249).I64(torsoKey).U8(5).I32(0).ToArray(), t + 1500);
        Assert.Equal(0, BitConverter.ToUInt16(unequip[0], 2));
        Assert.Equal("bag", store.Items.Single(i => i.WireId == torsoKey).Location);
        await session.HandleAsync(new PacketWriter().U16(0xe247).I64(torsoKey).U8(5).ToArray(), t + 1600);

        var levelBefore = session.Level;
        for (var i = 0; i < 40; i++)
        {
            var mob = session.Targets!.Values.FirstOrDefault(m => !m.Dead && m.Relationship == 6);
            if (mob is null) break;
            mob.Hp = 1;
            await MapEventScenarioHelpers.CombatCastAsync(session, basic, mob.Key, session.Position!.Value, t + 1700 + i * 40);
        }
        Assert.True(session.Level > levelBefore);
        var overCap = session.Level;
        session.ShareExperience(999_999_999);
        await session.TickAsync(t + 3500);
        await session.TickAsync(t + 3600);
        Assert.InRange(session.Level, overCap, Data.MaxLevel);

        store.Items.Add(FakeCharacterStore.BagItem(SpiritStone, 1, 201));
        store.Items.Add(FakeCharacterStore.BagItem(LifePotion, 5, 202));
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000, 203));
        await session.InventoryChangesAsync(default);
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(201).ToArray(), t + 1900);
        Assert.True(session.CanRebirth);
        var fell = session.Position!.Value;
        session.NpcHitsPlayer(session.Resource!.HpMax * 10, t + 2000, killerKey: target.Key);
        Assert.True(session.Dead);
        var rebirth = await session.HandleAsync(new PacketWriter().U16(RidePackets.UseRebirthRq).U8(1).ToArray(), t + 2100);
        Assert.Contains(rebirth, p => p.SequenceEqual(RidePackets.UseRebirthReply(0, true)));
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 2200);
        Assert.False(session.Dead);
        Assert.Equal(fell, session.Position);

        session.SetResource('H', session.Resource.HpMax / 3);
        await session.InventoryChangesAsync(default);
        var potionsInBag = store.Items.Where(i => i.TemplateId == LifePotion && i.Location == "bag").Sum(i => i.Quantity);
        var potionUse = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(202).ToArray(), t + 2300);
        Assert.DoesNotContain(potionUse, p => p.Length >= 3 && GameSessionTestHelpers.Op(p) == RidePackets.ItemUseRs && BitConverter.ToUInt16(p, 2) != 0);
        await session.TickAsync(t + 2400);
        await session.TickAsync(t + 11_000);
        Assert.True(store.Items.Where(i => i.TemplateId == LifePotion && i.Location == "bag").Sum(i => i.Quantity) < potionsInBag);

        var grocer = session.Targets!.Values.First(n => n.NpcId == 2930067);
        await session.HandleAsync(new PacketWriter().U16(GameSession.ShopOpenRq).Varint((uint)grocer.Key).ToArray(), t + 2500);
        var potionsBefore = store.Items.Where(i => i.TemplateId == LifePotion).Sum(i => i.Quantity);
        var kinaBefore = FakeCharacterStore.KinaIn(store);
        var buy = await session.HandleAsync(new PacketWriter().U16(GameSession.ShopBuyRq).Varint(1).Varint((uint)grocer.Key).Varint(1).I32(GrocerGoodsId).I32(1).ToArray(), t + 2600);
        Assert.Equal(0, BitConverter.ToUInt16(buy[0], 2));
        Assert.Equal(potionsBefore + 1, store.Items.Where(i => i.TemplateId == LifePotion).Sum(i => i.Quantity));
        Assert.True(FakeCharacterStore.KinaIn(store) < kinaBefore);
        var sellWire = store.Items.First(i => i.TemplateId == LifePotion && i.Location == "bag").WireId;
        await session.HandleAsync(new PacketWriter().U16(GameSession.ShopSellRq).Varint((uint)grocer.Key).Varint(1).I64(sellWire).I64(1).ToArray(), t + 2700);

        store.States[0] = store.States[0] with { Ascension = 3 };
        store.Items.Add(FakeCharacterStore.BagItem(ReturnScroll, 1, 204));
        await session.InventoryChangesAsync(default);
        var scrollUse = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(204).ToArray(), t + 2800);
        Assert.Contains(scrollUse, p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseStartedNt);
        var scrollDone = await session.TickAsync(t + 2800 + 3_000);
        var scrollLoad = scrollDone.LastOrDefault(p => GameSessionTestHelpers.Op(p) == 0x3621 && BitConverter.ToInt32(p, 6) == 1000);
        Assert.NotNull(scrollLoad);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == ReturnScroll && i.Quantity > 0);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 5801);
        Assert.Equal(1000, session.MapId);

        var savedLevel = session.Level;
        var savedPos = session.Position!.Value;
        var savedMap = session.MapId!.Value;
        var bagCount = store.Items.Count(i => i.Location == "bag" || i.Location == "equipment");
        if (session.DevelopmentQuestLog().Get("HQ1101018") is { } activeQuest)
            await store.SaveQuestAsync(store.States[0].Summary.CharacterId, activeQuest, default);
        await session.HandleAsync(PacketWriter.Opcode(0x3630).ToArray(), t + 3000);
        Assert.Equal(1, store.LogoutRecordCount);
        session.ExitSharedWorld();

        var again = await ReloginAsync(store, world, wireId, t + 4000);
        Assert.Equal(savedLevel, again.Level);
        Assert.Equal(savedMap, again.MapId);
        var storedAt = StoredCharacters.Position(store.States[0]);
        Assert.Equal(savedPos.X, storedAt.X, 3);
        Assert.Equal(savedPos.Y, storedAt.Y, 3);
        Assert.Equal("equipment", store.Items.Single(i => i.WireId == torsoKey).Location);
        Assert.True(store.Items.Count(i => i.Location is "bag" or "equipment") >= bagCount - 2);
        var reloadedQuest = again.DevelopmentQuestLog().Get("HQ1101018");
        Assert.NotNull(reloadedQuest);
        Assert.Equal(QuestLog.Incomplete, reloadedQuest!.Status);
        again.ExitSharedWorld();
    }
}
