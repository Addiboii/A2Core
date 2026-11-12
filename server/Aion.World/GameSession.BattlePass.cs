using Aion.Protocol.Native;

namespace Aion.World;

// Battle pass (BattlePass.dat / BattlePassGroup.dat, achievements.json `battlepass`): a group is active in its activation
// window for its race (the two Light global passes 10044/10045 run 2026-09-30 → 2026-12-16, OBSERVED listed as the 21
// BattlePass-category achievements at world entry). A group's missions are the achievements of its mission group; a
// completed mission step pays its AchievementReward.RewardPoint as pass exp (OURS: at completion, not on a claim) to
// the exp achievement (type BattlePassExp, 101 steps = levels) of every pass of the group; level rewards come through
// TakeBattlePassReward (or TakeAchievementReward) on that achievement, the paid pass's only after a purchase (none is sold here).
// BattlePassInfoList_NT 0xe324 at entry, TakeBattlePassReward 0xe322/0xe323 (pays the level), BattlePassPurchase
// 0xe32d/0xe32e (refused). OBSERVED 0xe324 and 0xe323 in the community recordings 2026-11-04 to 06 (161 lists; claims
// 163412 #893-894, 162114, 234912; reports/progression.md 4): the catalogue's 0xe3 run (base 0xdd6e), not 0xdd07; the
// others by position (CANDIDATE). Bodies from the usmap. The weekly exp limit is not applied.
public sealed partial class GameSession
{
    public const ushort BattlePassInfoListNt = 0xe324, TakeBattlePassRewardRq = 0xe322, TakeBattlePassRewardRs = 0xe323, BattlePassPurchaseRq = 0xe32d, BattlePassPurchaseRs = 0xe32e;

    List<AchievementTables.PassGroup> ActivePasses(long now) =>
        Achievements.PassGroups.Where(g => (g.Race == 0 || g.Race == profileRace) && g.FromMs <= now && now <= g.ToMs).ToList();

    /// <summary>The active group whose mission group holds the achievement, if any.</summary>
    AchievementTables.PassGroup? PassOfMission(AchievementTables.Entry entry, long now) =>
        ActivePasses(now).FirstOrDefault(g => g.Missions is { } missions && string.Equals(missions, entry.Group, StringComparison.OrdinalIgnoreCase));

    bool PaidPassLocked(int achievementId) =>
        Achievements.PassGroups.Any(g => g.Passes.Any(p => p.Length >= 3 && p[1] == achievementId && p[2] != 0));

    /// <summary>A mission step completed: its exp to the group's passes.</summary>
    List<byte[]> BattlePassExp(AchievementTables.Entry mission, int step, long now)
    {
        if (PassOfMission(mission, now) is not { } group) return [];
        var exp = mission.Points(step);
        if (exp <= 0) return [];
        var ids = group.Passes.Where(p => p.Length >= 2).Select(p => (int)p[1]).ToHashSet();
        return Progress("BattlePassExp", e => ids.Contains(e.Id), s => s.Count + exp, now, 1);
    }

    /// <summary>
    /// BattlePassInfoList_NT 0xe324: per active group id, total exp, period exp, exp reset time, repeat reward time, the passes
    /// held, the rewarded level by held pass, a per-pass map, two packed bools and an i32. OBSERVED (the community recordings
    /// 2026-11-04 to 06; e.g. `...01 d60d0300 01 d60d0300 65 02 ... 02 01000000` for group 20044): only the passes the character
    /// holds are listed (the free one; a paid one once bought, none are sold here), a rewarded level only when above 0, and the
    /// tail is bools false/true (byte 02) then i32 1 in every recorded list. CANDIDATE: the per-pass map (recorded with a
    /// value per pass, e.g. 40000; not understood) is sent empty. The exp values are our own pass exp (OURS).
    /// The community fork found the same tail and lists.
    /// </summary>
    byte[] BattlePassInfoList(long now)
    {
        var groups = ActivePasses(now);
        var w = PacketWriter.Opcode(BattlePassInfoListNt).Varint((uint)groups.Count);
        foreach (var group in groups)
        {
            var free = group.Passes.FirstOrDefault(p => p.Length >= 3 && p[2] == 0) ?? group.Passes.FirstOrDefault();
            var total = free is null ? 0 : AchievementCount((int)free[1]);
            var held = free is null ? [] : new List<long[]> { free };
            w.I32(group.Id).I32(total).I32(total).I64(CycleStart(2, now) + 7 * 86_400_000L).I64(0).Varint((uint)held.Count);
            foreach (var pass in held) w.I32((int)pass[0]);
            var rewarded = held.Select(p => (Pass: (int)p[0], Level: achievements.GetValueOrDefault((int)p[1])?.RewardedStep ?? 0)).Where(r => r.Level > 0).ToList();
            w.Varint((uint)rewarded.Count);
            foreach (var (pass, level) in rewarded) w.I32(pass).U8((byte)level);
            w.Varint(0).Bool(false).Bool(true).I32(1);
        }
        return w.ToArray();
    }

    /// <summary>EIncreaseReason kBattlePassReward (OBSERVED: 72 in every single-item 0x561b before a recorded 0xe323, 2026-11-04 to 06).</summary>
    const byte IncreaseBattlePassReward = 72;

    /// <summary>
    /// 0xe322 TakeBattlePassReward_RQ {group, pass exp achievement, level, map}: the level's items (the achievement's step
    /// reward), then the reply with an empty map. OBSERVED (community recordings 2026-11-04 to 06, 26262663): 11 of 13
    /// replies follow a 0x561b with the level's item, increase reason 72, e.g. 163412 #893-894 (503700032 for level 31 of
    /// 820439000, then `0000 4f4e0000 d8e7e630 1f 00`); no 0x5684 or 0xe324 between or after. Until now the level was
    /// marked rewarded without its items (the community fork found it). OURS: every unclaimed level up to the one
    /// asked is paid, in one 0x561b, as TakeAchievementReward does; a paid pass's levels stay locked (none is sold).
    /// </summary>
    async Task<List<byte[]>> TakeBattlePassRewardAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 11) return [];
        var groupId = BitConverter.ToInt32(plain, 2);
        var achievementId = BitConverter.ToInt32(plain, 6);
        var step = plain[10];
        byte[] Reply(int result) => PacketWriter.Opcode(TakeBattlePassRewardRs).U16(result).I32(groupId).I32(achievementId).U8(step).Varint(0).ToArray();
        if (Achievements.Of(achievementId) is not { Type: "BattlePassExp" } entry || achievements.GetValueOrDefault(achievementId) is not { } state)
            return [Reply(1)];
        var paid = false;
        for (var level = state.RewardedStep + 1; level <= Math.Min((int)step, entry.Steps.Length); level++)
        {
            if (await TakeStepAsync(achievementId, level, ct, send: false) is null) break;
            paid = true;
        }
        if (!paid) return [Reply(1)];
        return [.. await InventoryChangesAsync(ct, IncreaseBattlePassReward, 0), Reply(state.RewardedStep >= step ? 0 : 1)];
    }
}
