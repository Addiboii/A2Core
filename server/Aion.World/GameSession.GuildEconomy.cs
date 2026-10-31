using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Authority = Aion.Application.Gameplay.GuildAuthority;

namespace Aion.World;

// Legion officers, applications, attendance, donations, wallet and warehouse (opcodes in GameSession.Economy.cs).
// Ported from A2Core PR #4 (Artezianec) and corrected: grade checks use GuildMemberGradeAuthority bits, the warehouse keeps
// the whole item instance and refuses bound items, the wallet changes in one statement (never below 0), every cost comes
// from the tables, and attendance turns over on the game day (07:00 UTC).
public sealed partial class GameSession
{
    static int StoreResult(string outcome) => outcome switch { "ok" => 0, "authority" => 5, "bound" => 6, "full" => 7, "money" => 3, "limit" => 8, "max" => 9, _ => 1 };

    async Task<List<byte[]>?> GuildOfficerAsync(ushort op, PacketReader r, long now, CancellationToken ct)
    {
        var economy = Economy;
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
        string outcome = "none";
        async Task Run(Func<Task> action) => await Stored(async () => { await action(); return []; });
        switch (op)
        {
            case GuildSetNoticeRq or GuildSetIntroRq:
            {
                var text = r.String();
                var notice = op == GuildSetNoticeRq;
                if (economy is not null)
                    await Run(async () => outcome = await economy.SetGuildTextAsync(Me, notice ? "notice" : "intro", text, notice ? Authority.ChangeNotice : Authority.ChangeIntroduce, ct));
                var rs = PacketWriter.Opcode(notice ? GuildSetNoticeRs : GuildSetIntroRs).U16(StoreResult(outcome)).String(text).ToArray();
                if (outcome != "ok") return [rs];
                var nt = PacketWriter.Opcode(notice ? GuildSetNoticeNt : GuildSetIntroNt).String(text).ToArray();
                await ReloadGuildAsync(ct);
                TellGuild(guild, nt);
                return [rs, nt];
            }
            case GuildSetJoinRuleRq:
            {
                var (rule, password) = (r.U8(), r.String());
                if (economy is not null) await Run(async () => outcome = await economy.SetGuildJoinRuleAsync(Me, rule, password, Authority.ChangeJoinRule, ct));
                var rs = PacketWriter.Opcode(GuildSetJoinRuleRs).U16(StoreResult(outcome)).U8(rule).ToArray();
                if (outcome != "ok") return [rs];
                var nt = PacketWriter.Opcode(GuildSetJoinRuleNt).U8(rule).String(rule == 3 ? password : "").ToArray();
                await ReloadGuildAsync(ct);
                TellGuild(guild, nt);
                return [rs, nt];
            }
            case GuildCommentRq:
            {
                var comment = r.String();
                var ok = false;
                if (economy is not null) await Run(async () => ok = await economy.SetGuildCommentAsync(Me, comment, ct));
                if (ok) await ReloadGuildAsync(ct);
                return [PacketWriter.Opcode(GuildCommentRs).U16(ok ? 0 : 1).String(comment).ToArray()];
            }
            case GuildSetGradeRq:
            {
                var (name, grade) = (r.String(), r.U8());
                var officers = Guilds.Level(guild?.Level ?? 1).Officers;
                if (economy is not null) await Run(async () => outcome = await economy.SetGuildGradeAsync(Me, name, grade, officers, ct));
                var rs = PacketWriter.Opcode(GuildSetGradeRs).U16(StoreResult(outcome)).ToArray();
                if (outcome != "ok") return [rs];
                var nt = PacketWriter.Opcode(GuildSetGradeNt).String(name).U8(grade).ToArray();
                await ReloadGuildAsync(ct);
                TellGuild(guild, nt);
                return [rs, nt];
            }
            case GuildDelegateRq:
            {
                var name = r.String();
                if (economy is not null) await Run(async () => outcome = await economy.DelegateGuildMasterAsync(Me, name, ct));
                byte[] Packet(ushort code, int? result) => (result is { } res ? PacketWriter.Opcode(code).U16(res) : PacketWriter.Opcode(code))
                    .String(StoredName).U8(outcome == "ok" ? 1 : 0).String(name).U8(outcome == "ok" ? 0 : 2).ToArray();
                if (outcome != "ok") return [Packet(GuildDelegateRs, StoreResult(outcome))];
                await ReloadGuildAsync(ct);
                var nt = Packet(GuildDelegateNt, null);
                TellGuild(guild, nt);
                return [Packet(GuildDelegateRs, 0), nt, GuildDescUpdate()];
            }
            case GuildRecvJoinRq:
            {
                IReadOnlyList<GuildJoinRequest> list = [];
                if (economy is not null) await Run(async () => list = await economy.GuildJoinRequestsAsync(Me, Authority.JoinRequestApproval, ct));
                var w = PacketWriter.Opcode(GuildRecvJoinRs).U16(0).Varint(list.Count);
                foreach (var a in list) WriteJoinRequest(w, a);
                return [w.ToArray()];
            }
            case GuildSendJoinRq:
            {
                IReadOnlyList<GuildView> list = [];
                if (economy is not null) await Run(async () => list = await economy.SentGuildJoinRequestsAsync(Me, ct));
                var w = PacketWriter.Opcode(GuildSendJoinRs).U16(0).Varint(list.Count);
                foreach (var g in list)
                    w.I64(g.Id).String(g.Name).String(g.Members.FirstOrDefault(m => m.Grade == 0)?.Name ?? "").U8(g.Level).I64(g.Exp)
                        .U8(g.Members.Count).U8(Guilds.Level(g.Level).Members).I32(g.Emblem).String(g.Intro).U8(g.JoinRule).Bool(true).I64(0).U8(profileRace);
                // The world-entry query (0x8a1c) also carries the chat unlock.
                return [w.ToArray(), .. UnlockUpdateOnce()];
            }
            case GuildJoinCancelRq:
            {
                var id = r.I64();
                var ok = false;
                if (economy is not null) await Run(async () => ok = await economy.CancelGuildJoinAsync(Me, id, ct));
                return [PacketWriter.Opcode(GuildJoinCancelRs).U16(ok ? 0 : 1).I64(id).U8(0).ToArray()];
            }
            case GuildJoinConfirmRq:
            {
                var (name, accept) = (r.String(), r.Bool());
                Guid? applicant = null;
                var members = Guilds.Level(guild?.Level ?? 1).Members;
                if (economy is not null) await Run(async () => (outcome, applicant) = await economy.AnswerGuildJoinAsync(Me, name, accept, members, Authority.JoinRequestApproval, ct));
                var rs = PacketWriter.Opcode(GuildJoinConfirmRs).U16(StoreResult(outcome)).String(name).Bool(accept).ToArray();
                if (outcome != "ok") return [rs];
                var g = await ReloadGuildAsync(ct);
                if (applicant is { } who && options.World?.FindOnline(who) is { } online)
                    online.Send([PacketWriter.Opcode(GuildJoinConfirmNt).I64(g?.Id ?? 0).Bool(accept).ToArray()]);
                if (accept) TellGuild(g, PacketWriter.Opcode(GuildMemberConnectedNt).String(name).ToArray());
                return [rs];
            }
            case GuildDonationInfoRq:
            {
                var left = 0;
                if (economy is not null) await Run(async () => left = await economy.GuildDonationsLeftAsync(Me, GameDay(at), DonationsPerDay, ct));
                return [PacketWriter.Opcode(GuildDonationInfoRs).U16(economy is null ? 1 : 0).U8(left).ToArray()];
            }
            case GuildDonationRq:
            {
                var level = r.U8();
                byte[] Reply(int result, int left) => PacketWriter.Opcode(GuildDonationRs).U16(result).U8(level).U8(left).ToArray();
                if (economy is null || level < 1 || level > DonationTiers.Length) return [Reply(1, 0)];
                var tier = DonationTiers[level - 1];
                if (CostStacks(KinaItemId, tier.Kina) is not { } costs) return [Reply(3, 0)];
                var left = 0;
                await Run(async () => (outcome, left) = await economy.GuildDonateAsync(Me, GameDay(at), DonationsPerDay, costs, tier.Essence, tier.Exp, ct));
                if (outcome != "ok") return [Reply(StoreResult(outcome), left)];
                var replies = new List<byte[]> { Reply(0, left) };
                replies.AddRange(await InventoryChangesAsync(ct, 0, 0));
                long? wallet = null;
                await Run(async () => wallet = await economy.GuildWalletAsync(Me, ct));
                if (wallet is { } amount) replies.Add(GuildWallet(amount, 1));
                return replies;
            }
            case GuildAttendanceInfoRq or GuildAttendanceRq or GuildAttendanceRewardRq:
            {
                var attend = op == GuildAttendanceRq;
                (string Result, int Today, int Yesterday) state = ("none", 0, 0);
                if (economy is not null) await Run(async () => state = await economy.GuildAttendanceAsync(Me, GameDay(at), attend, Guilds.AttendanceExp, ct));
                return op switch
                {
                    GuildAttendanceInfoRq => [PacketWriter.Opcode(GuildAttendanceInfoRs).U16(state.Result == "none" ? 1 : 0).U8(state.Today).U8(state.Yesterday)
                        .Bool(state.Result == "ok").Bool(false).ToArray()],
                    GuildAttendanceRq => [PacketWriter.Opcode(GuildAttendanceRs).U16(state.Result == "ok" ? 0 : 1).ToArray()],
                    // OURS: the attendance reward (no table found) is not given; the reply only reports yesterday's count.
                    _ => [PacketWriter.Opcode(GuildAttendanceRewardRs).U16(1).U8(state.Yesterday).ToArray()],
                };
            }
        }
        return null;
    }

    /// <summary>GuildJoinRecv: pc id, name, comment, requested, logout, online, reputation, level, conqueror level, item level, power.</summary>
    void WriteJoinRequest(PacketWriter w, GuildJoinRequest a) =>
        w.I32(a.WireId).String(a.Name).String(a.Comment).I64(a.At.ToUnixTimeMilliseconds()).I64(0).Bool(options.World?.FindOnline(a.CharacterId) is not null)
            .I64(0).I32(a.Level).I32(0).I32(0).I64(0);

    /// <summary>UpdateGuildWallet_NT {[{type 1 EssenceGuild, amount}], reason}.</summary>
    static byte[] GuildWallet(long amount, byte reason) => PacketWriter.Opcode(GuildWalletNt).Varint(1).U8(1).I64(amount).U8(reason).ToArray();

    /// <summary>A manual-join legion (EGuildJoinRule 1) takes an application instead (officers see GuildJoinRequested_NT).</summary>
    async Task<bool> ApplyToGuildAsync(long id, string comment, CancellationToken ct)
    {
        if (Economy is not { } economy) return false;
        var outcome = "none";
        await Stored(async () => { outcome = await economy.RequestGuildJoinAsync(Me, id, comment, Guilds.JoinRequestsSent, Guilds.JoinRequestsReceived, ct); return []; });
        return outcome == "ok";
    }

    // ---------- Warehouse ----------

    static PacketWriter WriteStorageItem(PacketWriter w, GuildStorageEntry e)
    {
        var stones = GodstonesOf(e.Item);
        w.I64(WireKey(e.Item)).I32(e.Item.TemplateId).I64(e.Item.Quantity).I32(e.Slot).U8(0)
            .U8(EnchantOf(e.Item)).U8(EnchantOf(e.Item, "exceed")).U8(0).Varint(0).Varint(3);
        foreach (var s in stones) w.I32(s);
        return w;
    }

    async Task<List<byte[]>?> GuildStorageAsync(ushort op, PacketReader r, long now, CancellationToken ct)
    {
        var economy = Economy;
        async Task<GuildStorageView?> View()
        {
            GuildStorageView? view = null;
            if (economy is not null) await Stored(async () => { view = await economy.GuildStorageAsync(Me, ct); return []; });
            return view;
        }
        byte[] Modified(GuildStorageView view, byte reason, IEnumerable<(byte Type, GuildStorageEntry Entry)> changes)
        {
            var list = changes.ToList();
            var w = PacketWriter.Opcode(GuildStorageModifiedNt).I64(view.Revision).U8(reason).Varint(list.Count);
            foreach (var (type, entry) in list) WriteStorageItem(w.U8(type), entry);
            return w.ToArray();
        }
        switch (op)
        {
            case GuildStorageInfoRq:
            {
                if (await View() is not { } view) return [PacketWriter.Opcode(GuildStorageInfoRs).U16(1).Varint(0).I64(0).U8(0).I32(0).I64(0).ToArray()];
                var w = PacketWriter.Opcode(GuildStorageInfoRs).U16(0).Varint(view.Items.Count);
                foreach (var e in view.Items) WriteStorageItem(w, e);
                return [w.I64(view.Revision).U8(view.Extensions).I32(0).I64(view.LastSortMs).ToArray()];
            }
            case GuildStorageStoreRq or GuildStorageTakeRq:
            {
                var store = op == GuildStorageStoreRq;
                r.I32();   // npc key
                var count = r.Varint();
                if (count > 64) return [];
                var items = new List<(long Key, long Count)>();
                for (var i = 0; i < count; i++) items.Add((r.I64(), r.I64()));
                byte[] Reply(int result) => PacketWriter.Opcode(store ? GuildStorageStoreRs : GuildStorageTakeRs).U16(result).ToArray();
                if (economy is null || await View() is not { } before) return [Reply(1)];
                var outcome = "none";
                var slots = Guilds.Slots(before.Extensions);
                foreach (var (key, n) in items)
                {
                    if (store)
                    {
                        if (!storedItems.TryGetValue(key, out var row) || row.Location != "bag") { outcome = "none"; break; }
                        if (!ItemRules.Embedded.GuildStorable(row.TemplateId)) { outcome = "bound"; break; }
                        await Stored(async () => { outcome = await economy.StoreGuildItemAsync(Me, row.InstanceId, row.Revision, n, slots, Authority.StorageStore, ct); return []; });
                    }
                    else
                    {
                        if (before.Items.FirstOrDefault(e => WireKey(e.Item) == key) is not { } entry) { outcome = "none"; break; }
                        await Stored(async () => { outcome = await economy.TakeGuildItemAsync(Me, entry.Item.InstanceId, n, Authority.StorageTakeOut, ct); return []; });
                    }
                    if (outcome != "ok") break;
                }
                var replies = new List<byte[]> { Reply(StoreResult(outcome)) };
                if (await View() is { } after && after.Revision != before.Revision)
                {
                    replies.AddRange(await InventoryChangesAsync(ct, 0, 0));
                    var old = before.Items.ToDictionary(e => e.Item.InstanceId);
                    var changes = after.Items.Where(e => !old.TryGetValue(e.Item.InstanceId, out var o) || o.Item.Quantity != e.Item.Quantity)
                        .Select(e => (old.ContainsKey(e.Item.InstanceId) ? (byte)1 : (byte)0, e))
                        .Concat(before.Items.Where(e => after.Items.All(a => a.Item.InstanceId != e.Item.InstanceId)).Select(e => ((byte)2, e)));
                    var nt = Modified(after, store ? (byte)1 : (byte)2, changes);
                    TellGuild(guild, nt);
                    replies.Add(nt);
                }
                return replies;
            }
            case GuildStorageSortRq:
            {
                var ok = false;
                if (economy is not null) await Stored(async () => { ok = await economy.SortGuildStorageAsync(Me, now, Guilds.StorageSortCooldownMs, ct); return []; });
                var view = await View();
                var replies = new List<byte[]> { PacketWriter.Opcode(GuildStorageSortRs).U16(ok ? 0 : 1).I64(view?.LastSortMs ?? 0).ToArray() };
                if (ok && view is not null)
                {
                    var nt = Modified(view, 3, view.Items.Select(e => ((byte)1, e)));
                    TellGuild(guild, nt);
                    replies.Add(nt);
                }
                return replies;
            }
            case GuildStorageExtendRq:
            {
                r.I32();
                var expected = r.U8();
                var view = await View();
                byte[] Reply(int result) => PacketWriter.Opcode(GuildStorageExtendRs).U16(result).U8(view?.Extensions ?? 0).ToArray();
                if (economy is null || view is null || expected != view.Extensions + 1 || Guilds.Extension(expected) is not { } step) return [Reply(1)];
                if ((guild?.Level ?? 1) < step.Level) return [Reply(8)];
                var costs = step.Cost > 0 ? CostStacks(KinaItemId, step.Cost) : [];
                if (costs is null) return [Reply(3)];
                var outcome = "none";
                await Stored(async () => { outcome = await economy.ExtendGuildStorageAsync(Me, expected, Guilds.MaxExtensions, costs, Authority.StorageExtend, ct); return []; });
                if (outcome != "ok") return [Reply(StoreResult(outcome))];
                var nt = PacketWriter.Opcode(GuildStorageExtendedNt).U8(expected).ToArray();
                TellGuild(guild, nt);
                return [PacketWriter.Opcode(GuildStorageExtendRs).U16(0).U8(expected).ToArray(), .. await InventoryChangesAsync(ct, 0, 0), nt];
            }
            case GuildStorageHistoryRq:
            {
                var since = r.I64();
                var floor = now - Guilds.HistoryDays * 86_400_000L;
                IReadOnlyList<GuildStorageRecord> list = [];
                if (economy is not null)
                    await Stored(async () => { list = await economy.GuildStorageHistoryAsync(Me, DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(since, floor)), ct); return []; });
                var w = PacketWriter.Opcode(GuildStorageHistoryRs).U16(economy is null ? 1 : 0).I64(list.Count > 0 ? list[0].At.ToUnixTimeMilliseconds() : since).Varint(list.Count);
                foreach (var h in list) w.String(h.Name).U8(h.Type).I32(h.TemplateId).I64(h.Count).I64(h.At.ToUnixTimeMilliseconds());
                return [w.ToArray()];
            }
        }
        return null;
    }
}
