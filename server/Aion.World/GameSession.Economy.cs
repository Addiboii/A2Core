using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Authority = Aion.Application.Gameplay.GuildAuthority;

namespace Aion.World;

// Items, economy and legion management, ported from A2Core PR #4 (Artezianec) with the PR's bare developer opcodes
// remapped to live ones: live = developer opcode + the run's offset, each run anchored on an OBSERVED opcode:
//   0x56 run (+0x1ebe; anchor SoulBindStart 0x562b, ItemSort 0x563e, GodstoneList_NT 0x5633 in the entry burst):
//     ItemExceedEnchant 0x5624/0x5625, GodstoneList_NT 0x5633, GodstoneUpdate_NT 0x5634, ItemEnchantGodstone 0x5635/0x5636,
//     BroadcastItemEnchantGodstone_NT 0x5637, CombineGodstone 0x5638/0x5639.
//   0x8a run (+0x518b; anchor GuildMemberConnected_NT 0x8a39, SendJoinRequests_RS 0x8a1d at world entry): join requests
//     0x8a12-0x8a1d, join rule 0x8a1e-0x8a20, intro 0x8a24-26, notice 0x8a27-29, grade 0x8a2a-2c, comment 0x8a30/31,
//     delegate 0x8a36-38, donation 0x8a3a-3d, attendance 0x8a3e-43; wallet NT 0x8a9f and warehouse 0x8aa0-0x8aae
//     (CANDIDATE: beyond the anchor; 0x8aaf AgitDecoList_NT fits the official entry).
//   0xe2 run (+0xa6fb; anchor TradeServerInfo_NT 0xe263, 0xe274): broker 0xe264-0xe272 (game protocol only has selling,
//     buying by sale id, deliveries and the summary; browsing is not in it).
//   0xe3 run (+0xa762; anchor RetrievePushRewardInfo 0xe348/0xe349 OBSERVED): TakePushReward 0xe346/47, PushRewardChanged_NT 0xe34a.
// Results are u16 like every observed RS (usmap says i32). Costs are computed here from the tables, never from the request.
public sealed partial class GameSession
{
    public const ushort ExceedEnchantRq = 0x5624, ExceedEnchantRs = 0x5625,
        GodstoneListNt = 0x5633, GodstoneUpdateNt = 0x5634, GodstoneSocketRq = 0x5635, GodstoneSocketRs = 0x5636, GodstoneSocketNt = 0x5637,
        GodstoneCombineRq = 0x5638, GodstoneCombineRs = 0x5639,
        GuildJoinRequestedNt = 0x8a12, GuildJoinConfirmRq = 0x8a13, GuildJoinConfirmRs = 0x8a14, GuildJoinConfirmNt = 0x8a15,
        GuildJoinCancelRq = 0x8a16, GuildJoinCancelRs = 0x8a17, GuildRecvJoinRq = 0x8a19, GuildRecvJoinRs = 0x8a1a,
        GuildSendJoinRq = 0x8a1c, GuildSendJoinRs = 0x8a1d, GuildSetJoinRuleRq = 0x8a1e, GuildSetJoinRuleRs = 0x8a1f, GuildSetJoinRuleNt = 0x8a20,
        GuildSetIntroRq = 0x8a24, GuildSetIntroRs = 0x8a25, GuildSetIntroNt = 0x8a26, GuildSetNoticeRq = 0x8a27, GuildSetNoticeRs = 0x8a28, GuildSetNoticeNt = 0x8a29,
        GuildSetGradeRq = 0x8a2a, GuildSetGradeRs = 0x8a2b, GuildSetGradeNt = 0x8a2c, GuildCommentRq = 0x8a30, GuildCommentRs = 0x8a31,
        GuildDelegateRq = 0x8a36, GuildDelegateRs = 0x8a37, GuildDelegateNt = 0x8a38,
        GuildDonationInfoRq = 0x8a3a, GuildDonationInfoRs = 0x8a3b, GuildDonationRq = 0x8a3c, GuildDonationRs = 0x8a3d,
        GuildAttendanceInfoRq = 0x8a3e, GuildAttendanceInfoRs = 0x8a3f, GuildAttendanceRq = 0x8a40, GuildAttendanceRs = 0x8a41,
        GuildAttendanceRewardRq = 0x8a42, GuildAttendanceRewardRs = 0x8a43,
        GuildWalletNt = 0x8a9f, GuildStorageInfoRq = 0x8aa0, GuildStorageInfoRs = 0x8aa1, GuildStorageModifiedNt = 0x8aa2, GuildStorageExtendedNt = 0x8aa3,
        GuildStorageHistoryRq = 0x8aa5, GuildStorageHistoryRs = 0x8aa6, GuildStorageStoreRq = 0x8aa7, GuildStorageStoreRs = 0x8aa8,
        GuildStorageTakeRq = 0x8aa9, GuildStorageTakeRs = 0x8aaa, GuildStorageSortRq = 0x8aab, GuildStorageSortRs = 0x8aac,
        GuildStorageExtendRq = 0x8aad, GuildStorageExtendRs = 0x8aae,
        TradeSellRq = 0xe264, TradeSellRs = 0xe265, TradeCancelRq = 0xe266, TradeCancelRs = 0xe267, TradeRenewRq = 0xe268, TradeRenewRs = 0xe269,
        TradeExpireRq = 0xe26a, TradeExpireRs = 0xe26b, TradePurchaseRq = 0xe26c, TradePurchaseRs = 0xe26d, TradeSoldNt = 0xe26e,
        TradeDeliveryRq = 0xe26f, TradeDeliveryRs = 0xe270, TradeSummaryRq = 0xe271, TradeSummaryRs = 0xe272,
        PushRewardTakeRq = 0xe346, PushRewardTakeRs = 0xe347, PushRewardChangedNt = 0xe34a;

    static GodstoneTables Godstones => GodstoneTables.Embedded;
    static ExceedEnchantTables Exceeds => ExceedEnchantTables.Embedded;
    static GuildTables Guilds => GuildTables.Embedded;

    /// <summary>OURS: broker listings last 7 days and cost a 1% fee (min 1 Kina) when listed; GlobalSetting has no fee or duration.</summary>
    public const int TradeListingDays = 7, TradeFeePermille = 10;
    /// <summary>GlobalSetting trade_max_price.</summary>
    public const long TradeMaxPrice = 99_999_999_999;
    /// <summary>OURS: donation tiers (Kina, legion Essence, legion exp) per _donation_level 1-3, three a day; no table in the client data.</summary>
    static readonly (long Kina, long Essence, long Exp)[] DonationTiers = [(10_000, 10, 10), (100_000, 120, 100), (1_000_000, 1_500, 1_000)];
    public const int DonationsPerDay = 3;

    INativeEconomyStore? Economy => CharacterId is null ? null : Store as INativeEconomyStore;
    Guid Me => CharacterId ?? Guid.Empty;
    readonly Dictionary<int, int> godstones = [];

    /// <summary>Godstone roll 0..99999999 (replaceable in checks).</summary>
    public Func<int> GodstoneRoll { get; set; } = () => Random.Shared.Next(100_000_000);

    static int[] GodstonesOf(ItemState item)
    {
        var slots = new int[3];
        if (item.Upgrades.ValueKind == JsonValueKind.Object && item.Upgrades.TryGetProperty("godstones", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var (stone, i) in list.EnumerateArray().Select((s, i) => (s, i)).Where(x => x.i < 3))
                slots[i] = stone.TryGetInt32(out var n) ? n : 0;
        return slots;
    }

    /// <summary>The game day (07:00 UTC boundary, as shop limits).</summary>
    static DateOnly GameDay(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime.AddHours(-7));

    /// <summary>Reloads the godstone collection from the store (checks and staff tools).</summary>
    public Task ReloadEconomyForChecksAsync() => LoadEconomyAsync(default);

    async Task LoadEconomyAsync(CancellationToken ct)
    {
        godstones.Clear();
        if (Economy is not { } economy) return;
        await Stored(async () =>
        {
            foreach (var (id, count) in await economy.GodstonesAsync(Me, ct)) godstones[id] = count;
            return [];
        });
    }

    /// <summary>World entry: the godstone collection when it has stones (the observed entry sends an empty list).</summary>
    IEnumerable<byte[]> EconomyAtEntry()
    {
        if (godstones.Count == 0) yield break;
        var w = PacketWriter.Opcode(GodstoneListNt).Varint(godstones.Count);
        foreach (var (id, count) in godstones.OrderBy(g => g.Key)) w.I32(id).I32(count);
        yield return w.ToArray();
    }

    /// <summary>GodstoneUpdate_NT {reason EGodstoneModifyReason (1 UseItem, 2 Enchant), updates [{id, count}]}: the new totals (CANDIDATE).</summary>
    byte[] GodstoneUpdate(byte reason, IEnumerable<int> ids)
    {
        var list = ids.Distinct().ToList();
        var w = PacketWriter.Opcode(GodstoneUpdateNt).U8(reason).Varint(list.Count);
        foreach (var id in list) w.I32(id).I32(godstones.GetValueOrDefault(id));
        return w.ToArray();
    }

    async Task<List<byte[]>?> EconomyRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        var op = Opcode(plain);
        var handled = op is >= ExceedEnchantRq and <= GodstoneCombineRs or >= GuildJoinConfirmRq and <= GuildAttendanceRewardRs or >= GuildStorageInfoRq and <= GuildStorageExtendRs
            or >= TradeSellRq and <= TradeSummaryRs or PushRewardTakeRq or 0xe348;
        if (!handled || op is GuildDescUpdateNt or GuildDisbandRq or GuildDisbandRs or GuildMemberConnectedNt) return null;
        var r = new PacketReader(plain) { Offset = 2 };
        try
        {
            return op switch
            {
                ExceedEnchantRq => await ExceedEnchantAsync(r.I64(), ct),
                GodstoneSocketRq => await SocketGodstoneAsync(r.I64(), r.U8(), r.I32(), ct),
                GodstoneCombineRq => await CombineGodstonesAsync(r, ct),
                >= GuildJoinConfirmRq and <= GuildAttendanceRewardRs => await GuildOfficerAsync(op, r, now, ct),
                >= GuildStorageInfoRq and <= GuildStorageExtendRs => await GuildStorageAsync(op, r, now, ct),
                >= TradeSellRq and <= TradeSummaryRs => await TradeAsync(op, r, now, ct),
                PushRewardTakeRq => await TakePushRewardAsync(r.U8(), r.I32(), r.U8(), now, ct),
                0xe348 when plain.Length == 2 => await PushRewardInfoAsync(now, ct),
                _ => null,
            };
        }
        catch (ProtocolException) { return []; }
    }

    // ---------- Godstones ----------

    /// <summary>A GetGodstone item used: one stone from its gacha group joins the collection (GodstoneUpdate reason UseItem).</summary>
    async Task<List<byte[]>?> UseGodstoneItemAsync(ItemState row, CancellationToken ct)
    {
        if (!Godstones.IsGodstoneItem(row.TemplateId)) return null;
        var stone = Godstones.FromItem(row.TemplateId, GodstoneRoll());
        if (stone == 0 || Economy is not { } economy) return [];
        var done = await Stored(async () => await economy.UseGodstoneItemAsync(Me, row.InstanceId, row.Revision, stone, ct) ? [[]] : []);
        if (done.Count == 0) return [];
        godstones[stone] = godstones.GetValueOrDefault(stone) + 1;
        return [.. await InventoryChangesAsync(ct, 0, 0), GodstoneUpdate(1, [stone])];
    }

    /// <summary>
    /// ItemEnchantGodstone_RQ {item_uid i64, slot u8, godstone_id i32}: a stone from the collection goes into an empty slot
    /// of a weapon with godstone slots (Item.dat GodStoneSlotCount) for GlobalSetting godstone_cost_money_count Kina (CANDIDATE).
    /// </summary>
    async Task<List<byte[]>> SocketGodstoneAsync(long key, byte slot, int stone, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(GodstoneSocketRs).U16(result).I64(key).U8(slot).I32(stone).ToArray();
        if (Economy is not { } economy || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "equipment")) return [Reply(1)];
        var slots = Godstones.Slots(row.TemplateId);
        if (slot >= slots || Godstones.Of(stone) is null || godstones.GetValueOrDefault(stone) < 1 || GodstonesOf(row)[slot] != 0) return [Reply(2)];
        if (CostStacks(KinaItemId, Godstones.SocketKina) is not { } costs) return [Reply(3)];
        var done = await Stored(async () => await economy.SocketGodstoneAsync(Me, row.InstanceId, row.Revision, slot, slots, stone, costs, ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(1)];
        if ((godstones[stone] -= 1) <= 0) godstones.Remove(stone);
        var replies = new List<byte[]> { Reply(0) };
        replies.AddRange(await InventoryChangesAsync(ct, 0, 0));
        replies.Add(GodstoneUpdate(2, [stone]));
        if (row.Location == "equipment" && inventory.TryGetValue(key, out var worn) && VisualSlot.TryGetValue(worn.Slot, out var visual))
        {
            var notice = PacketWriter.Opcode(GodstoneSocketNt).Varint(playerKey).I32(row.TemplateId).U8((byte)visual).U8(slot).I32(stone).ToArray();
            worldPlayer?.Map?.Broadcast(worldPlayer, [notice]);
            replies.Add(notice);
        }
        return replies;
    }

    /// <summary>
    /// CombineGodstone_RQ {_materials map id -> count}: GlobalSetting godstone_combine_material_count_max stones of one grade
    /// (all combinable, owned) become one stone rolled from that grade's GodstoneCombine pool.
    /// RS {result, material_grade, reward_ids, updates}.
    /// </summary>
    async Task<List<byte[]>> CombineGodstonesAsync(PacketReader r, CancellationToken ct)
    {
        var count = r.Varint();
        if (count > 16) return [];
        var materials = new Dictionary<int, int>();
        for (var i = 0; i < count; i++)
        {
            var (id, n) = (r.I32(), r.I32());
            materials[id] = materials.GetValueOrDefault(id) + n;
        }
        byte[] Reply(int result, int grade, int reward, IEnumerable<int> changed)
        {
            var w = PacketWriter.Opcode(GodstoneCombineRs).U16(result).U8(grade).Varint(reward == 0 ? 0 : 1);
            if (reward != 0) w.I32(reward);
            var ids = changed.Distinct().ToList();
            w.Varint(ids.Count);
            foreach (var id in ids) w.I32(id).I32(godstones.GetValueOrDefault(id));
            return w.ToArray();
        }
        var stones = materials.Keys.Select(Godstones.Of).ToList();
        if (Economy is not { } economy) return [Reply(1, 0, 0, [])];
        if (materials.Count == 0 || stones.Any(s => s is null || !s.Combinable) || stones.Select(s => s!.Grade).Distinct().Count() != 1
            || materials.Values.Any(n => n < 1) || materials.Values.Sum() != Godstones.CombineCount) return [Reply(2, 0, 0, [])];
        var grade = stones[0]!.Grade;
        if (!Godstones.CanCombine(grade)) return [Reply(2, grade, 0, [])];
        if (materials.Any(m => godstones.GetValueOrDefault(m.Key) < m.Value)) return [Reply(3, grade, 0, [])];
        var reward = Godstones.Combine(grade, GodstoneRoll());
        if (reward == 0) return [Reply(2, grade, 0, [])];
        var done = await Stored(async () => await economy.CombineGodstonesAsync(Me, materials, reward, ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(1, grade, 0, [])];
        foreach (var (id, n) in materials)
            if ((godstones[id] -= n) <= 0) godstones.Remove(id);
        godstones[reward] = godstones.GetValueOrDefault(reward) + 1;
        return [Reply(0, grade, reward, [.. materials.Keys, reward])];
    }

    // ---------- Exceed enchanting ----------

    /// <summary>
    /// ItemExceedEnchant_RQ {equip_uid i64, cost_items (ignored)}: an item at its Enchant.dat maximum goes up one exceed
    /// level (ExceedEnchant.dat). RS {result, equip_uid, current level u8, item id}. Failure keeps materials spent, adds the
    /// failure correction (upgrades.exceed_bonus) and drops FailPenalty levels.
    /// </summary>
    async Task<List<byte[]>> ExceedEnchantAsync(long key, CancellationToken ct)
    {
        byte[] Reply(int result, int level, int itemId) => PacketWriter.Opcode(ExceedEnchantRs).U16(result).I64(key).U8(level).I32(itemId).ToArray();
        if (Economy is not { } economy || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "equipment")) return [Reply(1, 0, 0)];
        var level = EnchantOf(row, "exceed");
        if (EnchantOf(row) == 0 || Enchants.Next(row.TemplateId, EnchantOf(row)) is not null || Exceeds.Next(row.TemplateId, level) is not { } step)
            return [Reply(2, level, row.TemplateId)];
        var costs = CostStacks(KinaItemId, step.Kina, row.InstanceId);
        foreach (var (itemId, count) in step.Costs)
            if (costs is not null && CostStacks(itemId, count, row.InstanceId) is { } more) costs.AddRange(more); else costs = null;
        if (costs is null) return [Reply(3, level, row.TemplateId)];
        var bonus = EnchantOf(row, "exceed_bonus");
        var success = EnchantRoll() < step.Success + bonus;
        var after = success ? level + 1 : Math.Max(0, level - step.FailPenalty);
        var upgrades = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["exceed"] = after, ["exceed_bonus"] = success ? 0 : bonus + step.FailCorrection });
        var done = await Stored(async () => await economy.UpgradeItemAsync(Me, row.InstanceId, row.Revision, costs, upgrades, ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(1, level, row.TemplateId)];
        var replies = new List<byte[]> { Reply(success ? 0 : 4, after, row.TemplateId) };
        replies.AddRange(await InventoryChangesAsync(ct, 0, success ? DecreaseItemEnchant : DecreaseItemEnchantFail));
        if (row.Location == "equipment" && after != level) replies.AddRange(RefreshStats());
        return replies;
    }

    /// <summary>Stats the equipped items' exceed levels add (AdditionalStat totals).</summary>
    int ExceedStat(string stat) => inventory.Values.Where(i => i.Container == InventoryItem.Equipped && i.Detail is { Exceed: > 0 })
        .Sum(i => Exceeds.Effect(i.ItemId, i.Detail!.Exceed).GetValueOrDefault(stat));
}
