using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    bool legionEntryQueryDone;

    static PacketWriter WriteJoinRecvEntry(PacketWriter w, GuildJoinRequestRecv r) =>
        w.I64(CharDbId(r.CharacterId)).String(r.Name).String(r.Comment).I64(r.RequestedAt.ToUnixTimeMilliseconds()).I64(0).I32(1)
            .I64(r.Reputation).I32(r.Level).I32(0).I32(0).I32(0);

    static PacketWriter WriteJoinSentEntry(PacketWriter w, GuildJoinRequestSent r) =>
        w.I64(r.GuildId).String(r.GuildName).String(r.Comment).I64(r.RequestedAt.ToUnixTimeMilliseconds());

    static (byte Grade, int Bits)[] EffectiveAuthority(GuildView? g) =>
        g?.Authority is { Count: > 0 } a ? a.Select(x => (x.Grade, x.Bits)).ToArray() : GuildAuthority;

    PacketWriter WriteAuthorityList(PacketWriter w, GuildView? g)
    {
        var auth = EffectiveAuthority(g);
        w.Varint(auth.Length);
        foreach (var (grade, bits) in auth) w.U8(grade).I32(bits);
        return w;
    }

    PacketWriter WriteWalletList(PacketWriter w, GuildView? g)
    {
        var wallet = g?.Wallet ?? [];
        w.Varint(wallet.Count);
        foreach (var (type, amount) in wallet) w.I32(type).I64(amount);
        return w;
    }

    static List<(long Key, long Count)> ReadItemMap(PacketReader r)
    {
        var list = new List<(long, long)>();
        var count = r.Varint();
        for (var i = 0; i < count && i < 128; i++) list.Add((r.I64(), r.I64()));
        return list;
    }

    async Task<List<byte[]>?> TryGuildOfficerRequestAsync(ushort op, byte[] plain, PacketReader r, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character)
        {
            if (op is GuildOpcodes.SendJoinRequestsRq && plain.Length == 2)
            {
                var replies = new List<byte[]> { PacketWriter.Opcode(GuildOpcodes.SendJoinRequestsRs).U16(0).Varint(0).ToArray() };
                if (!legionEntryQueryDone) { legionEntryQueryDone = true; replies.AddRange(UnlockUpdateOnce()); }
                return replies;
            }
            return null;
        }
        switch (op)
        {
            case GuildOpcodes.SendJoinRequestsRq when plain.Length == 2:
            {
                IReadOnlyList<GuildJoinRequestSent> sent = [];
                await Stored(async () => { sent = await store.GuildJoinRequestsSentAsync(character, ct); return []; });
                var w = PacketWriter.Opcode(GuildOpcodes.SendJoinRequestsRs).U16(0).Varint(sent.Count);
                foreach (var row in sent) WriteJoinSentEntry(w, row);
                var replies = new List<byte[]> { w.ToArray() };
                if (!legionEntryQueryDone) { legionEntryQueryDone = true; replies.AddRange(UnlockUpdateOnce()); }
                return replies;
            }
            case GuildOpcodes.RecvJoinRequestsRq when plain.Length == 2:
            {
                IReadOnlyList<GuildJoinRequestRecv> recv = [];
                await Stored(async () => { recv = await store.GuildJoinRequestsRecvAsync(character, ct); return []; });
                if (guild is null) await ReloadGuildAsync(ct);
                if (MyGuildGrade() > 1) return [PacketWriter.Opcode(GuildOpcodes.RecvJoinRequestsRs).U16(2).Varint(0).ToArray()];
                var w = PacketWriter.Opcode(GuildOpcodes.RecvJoinRequestsRs).U16(0).Varint(recv.Count);
                foreach (var row in recv) WriteJoinRecvEntry(w, row);
                return [w.ToArray()];
            }
            case GuildOpcodes.JoinConfirmRq:
            {
                var nickname = r.String(); var accept = r.I32() != 0;
                var outcome = "none";
                await Stored(async () => { outcome = await store.ConfirmGuildJoinAsync(character, nickname, accept, Legion.MaxMembers, ct); return []; });
                if (outcome != "ok") return [PacketWriter.Opcode(GuildOpcodes.JoinConfirmRs).U16(outcome switch { "authority" => 2, "full" => 5, "missing" => 3, _ => 1 }).String(nickname).I32(accept ? 1 : 0).ToArray()];
                await ReloadGuildAsync(ct);
                var nt = PacketWriter.Opcode(GuildOpcodes.JoinConfirmNt).I64(guild?.Id ?? 0).I32(accept ? 1 : 0).ToArray();
                return [PacketWriter.Opcode(GuildOpcodes.JoinConfirmRs).U16(0).String(nickname).I32(accept ? 1 : 0).ToArray(), nt, GuildInfo(0)];
            }
            case GuildOpcodes.JoinCancelRq:
            {
                var guildId = (long)r.U64(); _ = r.I32();
                var outcome = "none";
                await Stored(async () => { outcome = await store.CancelGuildJoinRequestAsync(character, guildId, ct); return []; });
                return [PacketWriter.Opcode(GuildOpcodes.JoinCancelRs).U16(outcome == "ok" ? 0 : 1).ToArray()];
            }
            case GuildOpcodes.SetNoticeRq:
            {
                var notice = r.String();
                var outcome = "none";
                await Stored(async () => { outcome = await store.SetGuildNoticeAsync(character, notice, ct); return []; });
                if (outcome != "ok") return [PacketWriter.Opcode(GuildOpcodes.SetNoticeRs).U16(2).String(notice).ToArray()];
                await ReloadGuildAsync(ct);
                var nt = PacketWriter.Opcode(GuildOpcodes.SetNoticeNt).String(notice).ToArray();
                TellGuild(guild, nt);
                return [PacketWriter.Opcode(GuildOpcodes.SetNoticeRs).U16(0).String(notice).ToArray(), nt, GuildInfo(0)];
            }
            case GuildOpcodes.SetMemberGradeRq:
            {
                var nickname = r.String(); var grade = r.I32();
                var outcome = "none";
                await Stored(async () => { outcome = await store.SetGuildMemberGradeAsync(character, nickname, grade, ct); return []; });
                if (outcome != "ok") return [PacketWriter.Opcode(GuildOpcodes.SetMemberGradeRs).U16(1).ToArray()];
                await ReloadGuildAsync(ct);
                var nt = PacketWriter.Opcode(GuildOpcodes.SetMemberGradeNt).String(nickname).I32(grade).ToArray();
                TellGuild(guild, nt);
                return [PacketWriter.Opcode(GuildOpcodes.SetMemberGradeRs).U16(0).ToArray(), nt, GuildMembers(0)];
            }
            case GuildOpcodes.SetAuthorityRq:
            {
                _ = r.I32();
                var auth = new List<(byte, int)>();
                var n = r.Varint();
                for (var i = 0; i < n && i < 8; i++) auth.Add(((byte)r.U8(), r.I32()));
                var outcome = "none";
                await Stored(async () => { outcome = await store.SetGuildAuthorityAsync(character, auth, ct); return []; });
                if (outcome != "ok") return [PacketWriter.Opcode(GuildOpcodes.SetAuthorityRs).U16(2).ToArray()];
                await ReloadGuildAsync(ct);
                var nt = WriteAuthorityList(PacketWriter.Opcode(GuildOpcodes.SetAuthorityNt), guild).ToArray();
                TellGuild(guild, nt);
                return [PacketWriter.Opcode(GuildOpcodes.SetAuthorityRs).U16(0).ToArray(), nt];
            }
            case GuildOpcodes.DelegateMasterRq:
            {
                var nickname = r.String();
                var outcome = "none";
                await Stored(async () => { outcome = await store.DelegateGuildMasterAsync(character, nickname, ct); return []; });
                if (outcome != "ok") return [PacketWriter.Opcode(GuildOpcodes.DelegateMasterRs).U16(2).ToArray()];
                await ReloadGuildAsync(ct);
                var nt = PacketWriter.Opcode(GuildOpcodes.DelegateMasterNt).String(StoredName).I32(MyGuildGrade()).String(nickname).I32(0).ToArray();
                TellGuild(guild, nt);
                return [PacketWriter.Opcode(GuildOpcodes.DelegateMasterRs).U16(0).String(StoredName).I32(MyGuildGrade()).String(nickname).I32(0).ToArray(), nt, GuildDescUpdate(), GuildMembers(0)];
            }
            case GuildOpcodes.AttendanceInfoRq when plain.Length == 2:
            {
                var info = ("none", 0, 0, false, false);
                await Stored(async () => { info = await store.GuildAttendanceInfoAsync(character, ct); return []; });
                if (info.Item1 != "ok") return [PacketWriter.Opcode(GuildOpcodes.AttendanceInfoRs).U16(1).I32(0).I32(0).I32(0).I32(0).ToArray()];
                return [PacketWriter.Opcode(GuildOpcodes.AttendanceInfoRs).U16(0).I32(info.Item2).I32(info.Item3).I32(info.Item4 ? 1 : 0).I32(info.Item5 ? 1 : 0).ToArray()];
            }
            case GuildOpcodes.AttendanceRq when plain.Length == 2:
            {
                var outcome = "none";
                await Stored(async () => { outcome = await store.MarkGuildAttendanceAsync(character, ct); return []; });
                await ReloadGuildAsync(ct);
                return [PacketWriter.Opcode(GuildOpcodes.AttendanceRs).U16(outcome == "ok" ? 0 : 1).ToArray(), GuildInfo(0)];
            }
            case GuildOpcodes.AttendanceRewardRq when plain.Length == 2:
            {
                var outcome = ("none", 0);
                await Stored(async () => { outcome = await store.TakeGuildAttendanceRewardAsync(character, ct); return []; });
                return [PacketWriter.Opcode(GuildOpcodes.AttendanceRewardRs).U16(outcome.Item1 == "ok" ? 0 : 1).I32(outcome.Item2).ToArray()];
            }
            case GuildOpcodes.StorageInfoRq when plain.Length == 2:
            {
                var info = ("none", 0L, 0, (IReadOnlyList<GuildStorageItem>)[]);
                await Stored(async () => { info = await store.GuildStorageInfoAsync(character, ct); return []; });
                if (info.Item1 != "ok") return [PacketWriter.Opcode(GuildOpcodes.StorageInfoRs).U16(1).Varint(0).I64(0).I32(0).I32(0).I64(0).ToArray()];
                var w = PacketWriter.Opcode(GuildOpcodes.StorageInfoRs).U16(0).Varint(info.Item4.Count);
                foreach (var item in info.Item4)
                    w.I64(item.ItemKey).I32(item.TemplateId).I32((int)item.Quantity).I32(item.SlotPos).I32(0).I32(0).I32(0).Varint(0);
                var sortMs = guild?.StorageLastSortMs ?? 0;
                var reserved = guild?.StorageReservedSlots ?? 0;
                return [w.I64(info.Item2).I32(info.Item3).I32(reserved).I64(sortMs).ToArray()];
            }
            case GuildOpcodes.StorageItemStoreRq:
            {
                _ = r.I64();
                var map = ReadItemMap(r);
                var costs = new List<(Guid, long, int)>();
                foreach (var (key, count) in map)
                    if (storedItems.TryGetValue(key, out var row) && count > 0 && count <= row.Quantity)
                        costs.Add((row.InstanceId, row.Revision, (int)count));
                if (costs.Count != map.Count) return [PacketWriter.Opcode(GuildOpcodes.StorageItemStoreRs).U16(1).ToArray()];
                if (await TryGuildWalletFromStorageAsync(op, r, costs, [], ct) is { } walletReplies) return walletReplies;
                var outcome = "none";
                await Stored(async () => { outcome = await store.StoreGuildStorageAsync(character, costs, ct); return []; });
                if (outcome != "ok") return [PacketWriter.Opcode(GuildOpcodes.StorageItemStoreRs).U16(1).ToArray(), .. await InventoryChangesAsync(ct, 0, 0)];
                return [PacketWriter.Opcode(GuildOpcodes.StorageItemStoreRs).U16(0).ToArray(), .. await InventoryChangesAsync(ct, 0, 0)];
            }
            case GuildOpcodes.StorageItemTakeOutRq:
            {
                _ = r.I64();
                var map = ReadItemMap(r);
                var take = map.Select(m => (m.Key, (int)m.Count)).ToList();
                if (await TryGuildWalletFromStorageAsync(op, r, [], take, ct) is { } walletReplies) return walletReplies;
                var outcome = "none";
                await Stored(async () => { outcome = await store.TakeGuildStorageAsync(character, take, ct); return []; });
                return [PacketWriter.Opcode(GuildOpcodes.StorageItemTakeOutRs).U16(outcome == "ok" ? 0 : 1).ToArray(), .. outcome == "ok" ? await InventoryChangesAsync(ct, 0, 0) : []];
            }
        }
        return null;
    }
}
