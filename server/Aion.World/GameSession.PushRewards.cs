using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Push rewards (gift box). 0xe3 run (catalogue + 0xdd6e): RetrievePushRewardInfo 0xe348/0xe349,
// TakePushReward 0xe346/0xe347, PushRewardChanged_NT 0xe34a. Common_PushRewardInfo (usmap):
// EPushRewardType u8, id i32, target seq u8, sender, title, body, sent i64, expire i64, item i32, count i64.
public sealed partial class GameSession
{
    public const ushort TakePushRewardRq = 0xe346, TakePushRewardRs = 0xe347, PushRewardChangedNt = 0xe34a;
    const byte PushRewardTargetSequence = 1;
    const byte IncreasePushRewardTake = 22;   // EIncreaseReason (OBSERVED on official take)

    static void WritePushRewardInfo(PacketWriter w, PushRewardRecord reward)
    {
        w.U8(reward.Type).I32(reward.Id).U8(PushRewardTargetSequence).String(reward.Sender).String(reward.Title).String(reward.Body)
            .I64(reward.SentMs).I64(reward.ExpireMs).I32(reward.ItemTemplateId).I64(reward.ItemCount);
    }

    public static byte[] PushRewardChangedPacket() => PacketWriter.Opcode(PushRewardChangedNt).ToArray();

    public void NotifyPushRewardChanged() => options.World?.BroadcastPushRewardChanged();

    public async Task<PushRewardRecord?> AddPushRewardAsync(byte type, string sender, string title, string body, int itemTemplateId, long itemCount, long expireMs,
        CancellationToken ct) =>
        Store is { } store ? await StoredValue(() => store.AddPushRewardAsync(type, sender, title, body, itemTemplateId, itemCount, expireMs, ct), null) : null;

    /// <summary>0xe348 RetrievePushRewardInfo_RQ (no body): active gifts this character has not taken.</summary>
    async Task<List<byte[]>> RetrievePushRewardsAsync(long now, CancellationToken ct)
    {
        var rewards = await AvailablePushRewardsAsync(now, ct);
        var next = rewards.Count > 0 ? rewards.Min(r => r.ExpireMs) : now + 3_600_000;
        var w = PacketWriter.Opcode(PushRewardInfoRs).U16(0).I64(next).Varint((uint)rewards.Count);
        foreach (var reward in rewards) WritePushRewardInfo(w, reward);
        return [w.ToArray()];
    }

    async Task<IReadOnlyList<PushRewardRecord>> AvailablePushRewardsAsync(long now, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return [];
        return await StoredValue(() => store.PushRewardsAsync(character, now, ct), []);
    }

    /// <summary>0xe346 TakePushReward_RQ {type u8, id i32, target seq u8}: grant once, 0x561b then 0xe347.</summary>
    async Task<List<byte[]>> TakePushRewardAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 8 || Store is not { } store || CharacterId is not { } character) return [];
        var type = plain[2];
        var id = BitConverter.ToInt32(plain, 3);
        var seq = plain[7];
        byte[] Reply(int result, PushRewardRecord? reward = null) => PacketWriter.Opcode(TakePushRewardRs).U16(result).U8(type).I32(id).U8(seq)
            .I32(reward?.ItemTemplateId ?? 0).I64(reward?.ItemCount ?? 0).ToArray();
        if (seq != PushRewardTargetSequence) return [Reply(1)];
        var taken = await StoredValue(() => store.TakePushRewardAsync(character, type, id, seq, ct), null);
        if (taken is not { } reward) return [Reply(1)];
        var itemReplies = await InventoryChangesAsync(ct, IncreasePushRewardTake, 0);
        return [.. itemReplies, Reply(0, reward)];
    }
}
