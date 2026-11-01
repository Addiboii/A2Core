using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    /// <summary>Legion wallet: kina stack deposits use storage store/take with template 930100001 (no separate RQ in Oct 7 usmap).</summary>
    const long WalletTakeItemKey = -1;

    async Task<List<byte[]>?> TryGuildStorageWalletRequestAsync(ushort op, byte[] plain, PacketReader r, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return null;
        switch (op)
        {
            case GuildOpcodes.StorageHistoryRq:
            {
                var after = r.I64();
                var info = ("none", 0L, (IReadOnlyList<GuildStorageHistoryRow>)[]);
                await Stored(async () => { info = await store.GuildStorageHistoryAsync(character, after, ct); return []; });
                if (info.Item1 != "ok")
                    return [PacketWriter.Opcode(GuildOpcodes.StorageHistoryRs).U16(1).I64(0).Varint(0).ToArray()];
                var w = PacketWriter.Opcode(GuildOpcodes.StorageHistoryRs).U16(0).I64(info.Item2).Varint(info.Item3.Count);
                foreach (var row in info.Item3)
                    w.String(row.CharName).I32(row.RecordType).I32(row.TemplateId).I64(row.Quantity).I64(row.RecordedAtMs);
                return [w.ToArray()];
            }
            case GuildOpcodes.StorageItemSortRq:
            {
                _ = r.I64();
                var outcome = ("none", 0L);
                await Stored(async () => { outcome = await store.SortGuildStorageAsync(character, ct); return []; });
                if (outcome.Item1 != "ok") return [PacketWriter.Opcode(GuildOpcodes.StorageItemSortRs).U16(1).I64(0).ToArray()];
                await ReloadGuildAsync(ct);
                return [PacketWriter.Opcode(GuildOpcodes.StorageItemSortRs).U16(0).I64(outcome.Item2).ToArray()];
            }
            case GuildOpcodes.StorageSlotExtendRq:
            {
                _ = r.I64(); var expected = r.I32();
                var outcome = ("none", 0);
                await Stored(async () => { outcome = await store.ExtendGuildStorageAsync(character, expected, ct); return []; });
                if (outcome.Item1 != "ok")
                    return [PacketWriter.Opcode(GuildOpcodes.StorageSlotExtendRs).U16(outcome.Item1 switch { "authority" => 2, "kina" => 3, "count" => 4, _ => 1 }).I32(expected).ToArray(),
                        .. await InventoryChangesAsync(ct, 0, 0)];
                await ReloadGuildAsync(ct);
                var extNt = PacketWriter.Opcode(GuildOpcodes.StorageSlotExtendedNt).I32(outcome.Item2).ToArray();
                TellGuild(guild, extNt);
                return [PacketWriter.Opcode(GuildOpcodes.StorageSlotExtendRs).U16(0).I32(outcome.Item2).ToArray(), extNt, GuildInfo(0),
                    .. await InventoryChangesAsync(ct, 0, 0)];
            }
        }
        return null;
    }

    async Task<List<byte[]>?> TryGuildWalletFromStorageAsync(ushort op, PacketReader r, List<(Guid Item, long Revision, int Count)> bagCosts, List<(long Key, int Count)> take, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return null;
        if (op == GuildOpcodes.StorageItemStoreRq && bagCosts is { Count: 1 }
            && storedItems.Values.FirstOrDefault(i => i.InstanceId == bagCosts[0].Item) is { } row && row.TemplateId == KinaItemId)
        {
            var outcome = ("none", (IReadOnlyList<(int, long)>)[]);
            await Stored(async () => { outcome = await store.DonateGuildWalletAsync(character, bagCosts[0].Count, bagCosts, ct); return []; });
            if (outcome.Item1 != "ok") return [PacketWriter.Opcode(GuildOpcodes.StorageItemStoreRs).U16(1).ToArray(), .. await InventoryChangesAsync(ct, 0, 0)];
            await ReloadGuildAsync(ct);
            var walletNt = UpdateGuildWalletNt(guild, 1);
            TellGuild(guild, walletNt);
            return [PacketWriter.Opcode(GuildOpcodes.StorageItemStoreRs).U16(0).ToArray(), walletNt, .. await InventoryChangesAsync(ct, 0, 0)];
        }
        if (op == GuildOpcodes.StorageItemTakeOutRq && take is { Count: 1 } && take[0].Key == WalletTakeItemKey)
        {
            var outcome = ("none", (IReadOnlyList<(int, long)>)[]);
            await Stored(async () => { outcome = await store.WithdrawGuildWalletAsync(character, take[0].Count, ct); return []; });
            if (outcome.Item1 != "ok") return [PacketWriter.Opcode(GuildOpcodes.StorageItemTakeOutRs).U16(1).ToArray()];
            await ReloadGuildAsync(ct);
            var walletNt = UpdateGuildWalletNt(guild, 2);
            TellGuild(guild, walletNt);
            return [PacketWriter.Opcode(GuildOpcodes.StorageItemTakeOutRs).U16(0).ToArray(), walletNt, .. await InventoryChangesAsync(ct, 0, 0)];
        }
        return null;
    }
}
