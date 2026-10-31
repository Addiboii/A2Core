using Aion.Protocol.Native;

namespace Aion.World;

// Battle pass (BattlePass.dat / BattlePassGroup.dat, achievements.json `battlepass`), the Daeva Pass tab of the Subscribe window:
// a group is active in its activation window for its race (the two Light global passes 10044/10045 run 2026-09-30 → 2026-12-16,
// OBSERVED listed as the 21 BattlePass-category achievements at world entry). A group's missions are the achievements of its
// mission group; a completed mission step pays its AchievementReward.RewardPoint as pass exp (OURS: at completion, not on a
// claim) to the exp achievement (type BattlePassExp, 101 steps = levels) of every pass of the group. A level's reward is
// the exp achievement's step reward, claimed through TakeBattlePassReward or TakeAchievementReward; the paid pass's only
// once unlocked.
// Opcodes: the E3 run (usmap position + E3Base 0xdd6e), anchored by BattlePassInfoList_NT 0xe324 (OBSERVED at world entry,
// 4 samples) and its OBSERVED neighbours 0xe32a ArenaRanking_NT, 0xe334 CutsceneComplete_RQ, 0xe33b SubscribeList_NT:
// TakeBattlePassReward 0xe322/0xe323, BattlePassInfoList_NT 0xe324, BattlePassPurchase 0xe32d/0xe32e. The earlier 0xe2 run
// numbers (0xe2bb, 0xe2c6) are still accepted. Bodies from the usmap; result u16 as every observed RS (CANDIDATE).
// INFERRED (OURS): _battle_pass_list lists the passes the character holds (the free ones and the unlocked paid ones);
// _period_exp is 0 (the weekly exp limit is not applied); BattlePassPurchase_RQ._item_id is the BattlePass.dat id of a
// paid pass (or its group id: every paid pass of the group). BattlePass.dat UnlockCostType/UnlockCostCount are not
// extracted, so the unlock is free unless Aion:Shop:PremiumMode is Disabled. An unlock is stored as an achievement
// record keyed by the pass id (count 1), which no achievement id collides with.
public sealed partial class GameSession
{
    /// <summary>Daeva Pass paid track unlock (OBSERVED: Premium Subscription button, client BattlePass.dat UnlockCostCount).</summary>
    public const long PaidPassUnlockQunaCost = 1500;
    public const ushort TakeBattlePassRewardRq = SocialOpcodes.E3Base + 1460, TakeBattlePassRewardRs = SocialOpcodes.E3Base + 1461,
        BattlePassInfoListNt = SocialOpcodes.E3Base + 1462, BattlePassPurchaseRq = SocialOpcodes.E3Base + 1471, BattlePassPurchaseRs = SocialOpcodes.E3Base + 1472;
    const ushort LegacyTakeBattlePassRewardRq = 0xe2bb, LegacyBattlePassPurchaseRq = 0xe2c6;

    List<AchievementTables.PassGroup> ActivePasses(long now) =>
        Achievements.PassGroups.Where(g => (g.Race == 0 || g.Race == profileRace) && g.FromMs <= now && now <= g.ToMs).ToList();

    /// <summary>The active group whose mission group holds the achievement, if any.</summary>
    AchievementTables.PassGroup? PassOfMission(AchievementTables.Entry entry, long now) =>
        ActivePasses(now).FirstOrDefault(g => g.Missions is { } missions && string.Equals(missions, entry.Group, StringComparison.OrdinalIgnoreCase));

    static bool IsPaid(long[] pass) => pass.Length >= 3 && pass[2] != 0;

    bool PassUnlocked(long[] pass) => !IsPaid(pass) || (achievements.GetValueOrDefault((int)pass[0])?.Count ?? 0) > 0;

    bool PaidPassLocked(int achievementId) =>
        Achievements.PassGroups.Any(g => g.Passes.Any(p => p.Length >= 2 && p[1] == achievementId && !PassUnlocked(p)));

    /// <summary>A mission step completed: its exp to the group's passes.</summary>
    List<byte[]> BattlePassExp(AchievementTables.Entry mission, int step, long now)
    {
        if (PassOfMission(mission, now) is not { } group) return [];
        return AddPassExp(group, mission.Points(step), now);
    }

    List<byte[]> AddPassExp(AchievementTables.PassGroup group, int exp, long now)
    {
        if (exp <= 0) return [];
        var ids = group.Passes.Where(p => p.Length >= 2).Select(p => (int)p[1]).ToHashSet();
        return [.. Progress("BattlePassExp", e => ids.Contains(e.Id), s => s.Count + exp, now, 1), BattlePassInfoList(now)];
    }

    /// <summary>GM: pass exp to every active group (levels to claim without playing the missions).</summary>
    public List<byte[]> GrantBattlePassExp(int exp, long now) =>
        [.. ActivePasses(now).SelectMany(g => AddPassExp(g, exp, now).SkipLast(1)), BattlePassInfoList(now)];

    byte[] BattlePassInfoList(long now)
    {
        var groups = ActivePasses(now);
        var w = PacketWriter.Opcode(BattlePassInfoListNt).Varint((uint)groups.Count);
        foreach (var group in groups)
        {
            var free = group.Passes.FirstOrDefault(p => !IsPaid(p)) ?? group.Passes.FirstOrDefault();
            var total = free is null || free.Length < 2 ? 0 : AchievementCount((int)free[1]);
            var held = group.Passes.Where(PassUnlocked).ToList();
            w.I32(group.Id).I32(total).I32(0).I64(CycleStart(2, now) + 7 * 86_400_000L).I64(0).Varint((uint)held.Count);
            foreach (var pass in held) w.I32((int)pass[0]);
            w.Varint((uint)group.Passes.Length);
            foreach (var pass in group.Passes)
                w.I32((int)pass[0]).U8((byte)(pass.Length >= 2 ? achievements.GetValueOrDefault((int)pass[1])?.RewardedStep ?? 0 : 0));
            w.Varint(0).Bool(false).I32(0).Bool(group.Passes.Any(IsPaid) && group.Passes.Where(IsPaid).All(PassUnlocked));
        }
        return w.ToArray();
    }

    /// <summary>
    /// TakeBattlePassReward_RQ {group i32, achievement i32, step u8, map achievement i32 -> step u8}: every unclaimed step up
    /// to the named one of the achievement and of each map entry (a "claim all"), for a pass level (the exp achievement's
    /// step reward) or a mission. RS {result, group, achievement, step, map of the claimed achievement -> last step}.
    /// </summary>
    async Task<List<byte[]>> TakeBattlePassRewardAsync(byte[] plain, long now, CancellationToken ct)
    {
        int groupId, achievementId;
        byte step;
        var targets = new List<(int Id, int Step)>();
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            groupId = r.I32();
            achievementId = r.I32();
            step = r.U8();
            targets.Add((achievementId, step));
            if (r.Offset < plain.Length)
            {
                var n = r.Varint();
                for (var i = 0; i < n && i < 256; i++) targets.Add((r.I32(), r.U8()));
            }
        }
        catch (ProtocolException) { return []; }
        var claimed = new Dictionary<int, int>();
        var changes = new List<byte[]>();
        foreach (var (id, upTo) in targets)
        {
            if (id == 0 || upTo == 0) continue;
            for (var s = (achievements.GetValueOrDefault(id)?.RewardedStep ?? 0) + 1; s <= upTo; s++)
            {
                if (await TakeStepAsync(id, s, now, ct) is not { } taken) break;
                changes.AddRange(taken);
                claimed[id] = s;
            }
        }
        var ok = claimed.Count > 0;
        var reply = TakeBattlePassRewardOpcode(plain);
        return ok
            ? [BuildTakeBattlePassRewardReply(reply, 0, groupId, achievementId, step, claimed), .. changes, BattlePassInfoList(now)]
            : [BuildTakeBattlePassRewardReply(reply, 1, groupId, achievementId, step, claimed)];
    }

    static ushort TakeBattlePassRewardOpcode(byte[] plain) => Opcode(plain) == LegacyTakeBattlePassRewardRq ? (ushort)(LegacyTakeBattlePassRewardRq + 1) : TakeBattlePassRewardRs;

    static byte[] BuildTakeBattlePassRewardReply(ushort opcode, int result, int groupId, int achievementId, byte step, IReadOnlyDictionary<int, int> claimed)
    {
        var w = PacketWriter.Opcode(opcode).U16(result).I32(groupId).I32(achievementId).U8(step).Varint((uint)claimed.Count);
        foreach (var (id, s) in claimed) w.I32(id).U8((byte)s);
        return w.ToArray();
    }

    // OBSERVED after pass reward item grants: each 0x561b may be followed by a 14-byte 0xe323 ack (empty claimed map);
    // one 0x8c07 and two 0x561c may follow when coin-shop / equip visuals refresh — see PassGrantPackets builders.

    /// <summary>BattlePassPurchase_RQ {item_id}: unlocks a paid pass of an active group (or all of a group's paid passes).</summary>
    async Task<List<byte[]>> BattlePassPurchaseAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var itemId = BitConverter.ToInt32(plain, 2);
        var replyOpcode = Opcode(plain) == LegacyBattlePassPurchaseRq ? (ushort)(LegacyBattlePassPurchaseRq + 1) : BattlePassPurchaseRs;
        byte[] Reply(int result) => PacketWriter.Opcode(replyOpcode).U16(result).I32(itemId).ToArray();
        var passes = ActivePasses(now)
            .SelectMany(g => g.Id == itemId ? g.Passes.Where(IsPaid) : g.Passes.Where(p => p[0] == itemId && IsPaid(p)))
            .Where(p => !PassUnlocked(p)).ToList();
        if (passes.Count == 0 || shopOptions.PremiumMode == ShopPremiumMode.Disabled) return [Reply(1)];
        var price = shopOptions.PremiumMode switch
        {
            ShopPremiumMode.Disabled => -1L,
            ShopPremiumMode.Free => 0L,
            _ => PaidPassUnlockQunaCost,
        };
        if (price < 0) return [Reply(1)];
        var changes = new List<byte[]>();
        if (price > 0)
        {
            if (Store is not { } store || CharacterId is not { } character) return [Reply(1)];
            if (CostStacks(PlatformGoodsTables.Embedded.QunaItemId, price) is not { } stacks) return [Reply(2)];
            var done = await Stored(async () => await store.ShopBuyAsync(character, stacks, [], [], ct) ? [[]] : []);
            if (done.Count == 0) return [Reply(1)];
            changes.AddRange(await InventoryChangesAsync(ct, IncreaseShopBuy, DecreaseShopBuy));
        }
        foreach (var pass in passes)
        {
            achievements[(int)pass[0]] = new AchievementState { Count = 1, CompletedMs = now, UpdatedMs = now };
            achievementDirty.Add((int)pass[0]);
        }
        var expIds = passes.Where(p => p.Length >= 2).Select(p => (int)p[1]).ToHashSet();
        foreach (var expId in expIds)
        {
            if (Achievements.Of(expId) is not { } entry) continue;
            var state = achievements.GetValueOrDefault(expId) ?? new AchievementState();
            if (state.RewardedStep >= 1) continue;
            var items = entry.StepRewards(1);
            if (items.Count > 0 && Store is { } grantStore && CharacterId is { } grantCharacter)
            {
                var rewards = items.Select(i => (i.ItemId, i.Count)).ToList();
                var ok = await Stored(async () => await grantStore.ShopBuyAsync(grantCharacter, [], rewards, [], ct) ? [[]] : []);
                if (ok.Count == 0) continue;
                state.RewardedStep = 1;
                achievements[expId] = state;
                achievementDirty.Add(expId);
                changes.AddRange(await InventoryChangesAsync(ct, IncreaseAchievementReward, 0));
            }
        }
        return [Reply(0), .. changes, BattlePassInfoList(now)];
    }
}
