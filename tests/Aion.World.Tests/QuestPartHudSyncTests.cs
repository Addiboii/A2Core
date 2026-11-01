using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HUD episode bar (AionWidget_HUD_IndicatorQuest P_PBar_Progress): client counts EQuestStatus Rewarded (4)
/// per quest in the active Quest.dat Part from the journal (0x8d09/0x8d10/0x8d11), not Common_Quest _roll_count.
/// </summary>
public sealed class QuestPartHudSyncTests
{
    static ushort Op(byte[] p) => GameSessionTestHelpers.Op(p);

    [Fact]
    public async Task Quest_take_reward_syncs_rewarded_status_on_progress_and_add()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101010", 1101010, 50, QuestLog.Complete, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, 1, race: 1, level: 2);
        var reward = await session.HandleAsync(new PacketWriter().U16(0x8d0e).I32(50).I32(0).ToArray(), 2);
        var progress = reward.FirstOrDefault(p => Op(p) == 0x8d10);
        var add = reward.LastOrDefault(p => Op(p) == 0x8d11);
        Assert.NotNull(progress);
        Assert.NotNull(add);
        Assert.Equal(QuestLog.Rewarded, progress![6]);
        Assert.Equal(QuestLog.Rewarded, add![6]);
    }

    [Fact]
    public async Task World_entry_resends_part_cleared_quests_after_quest_list()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        store.Quests.Add(new QuestRecord("HQ1101050", 1101050, 101, QuestLog.Incomplete, 1, new int[6]));
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 8, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), 1);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), 1);
        var entry = new List<byte[]>();
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), 2));
        entry.AddRange(await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), 3));

        Assert.Contains(entry, p => Op(p) == 0x8d09);
        var bulk = Assert.Single(entry, p => Op(p) == 0x8FBC);
        static bool Carries(byte[] packet, int questId, byte status) =>
            packet.AsSpan().IndexOf([.. BitConverter.GetBytes(questId), status]) >= 0;
        Assert.True(Carries(bulk, 1101040, (byte)QuestLog.Rewarded));
        Assert.True(entry.Any(p => Op(p) == 0x8d11 && Carries(p, 1101040, (byte)QuestLog.Rewarded)),
            "rewarded part quests should be re-merged with QuestAdd after the list");
    }
}
