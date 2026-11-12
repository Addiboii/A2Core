using Aion.Protocol.Native;

namespace Aion.World;

// Battle pass (BattlePass.dat / BattlePassGroup.dat, achievements.json `battlepass`): a group is active in its activation
// window for its race (the two Light global passes 10044/10045 run 2026-09-30 → 2026-12-16, OBSERVED listed as the 21
// BattlePass-category achievements at world entry). A group's missions are the achievements of its mission group; a
// completed mission step pays its AchievementReward.RewardPoint as pass exp (OURS: at completion, not on a claim) to
// the exp achievement (type BattlePassExp, 101 steps = levels) of every pass of the group; level rewards come through
// TakeAchievementReward on that achievement, the paid pass's only after a purchase (none is sold here).
// BattlePassInfoList_NT 0xe324 at entry, TakeBattlePassReward 0xe322/0xe323 (acknowledged), BattlePassPurchase
// 0xe32d/0xe32e (refused). OBSERVED 0xe324 and 0xe323 in the community recordings 2026-11-04 to 06 (161 lists; claims
// 163412 #893-894, 162114, 234912; reports/progression.md 4): the catalogue's 0xe3 run (base 0xdd6e), not 0xdd07; the
// others by position (CANDIDATE). Bodies from the usmap; the list's third map, i32 and second bool are not decoded
// (recorded: per-pass values, 1, true; we send none, 0, false). The weekly exp limit is not applied.
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

    byte[] BattlePassInfoList(long now)
    {
        var groups = ActivePasses(now);
        var w = PacketWriter.Opcode(BattlePassInfoListNt).Varint((uint)groups.Count);
        foreach (var group in groups)
        {
            var free = group.Passes.FirstOrDefault(p => p.Length >= 3 && p[2] == 0) ?? group.Passes.FirstOrDefault();
            var total = free is null ? 0 : AchievementCount((int)free[1]);
            w.I32(group.Id).I32(total).I32(total).I64(CycleStart(2, now) + 7 * 86_400_000L).I64(0).Varint((uint)group.Passes.Length);
            foreach (var pass in group.Passes) w.I32((int)pass[0]);
            w.Varint((uint)group.Passes.Length);
            foreach (var pass in group.Passes) w.I32((int)pass[0]).U8((byte)(achievements.GetValueOrDefault((int)pass[1])?.RewardedStep ?? 0));
            w.Varint(0).Bool(false).I32(0).Bool(false);
        }
        return w.ToArray();
    }

    /// <summary>
    /// 0xe322 TakeBattlePassReward_RQ {group, mission, step, map}: the mission's exp was paid at completion; the step is marked
    /// rewarded. The reply alone, with an empty map (OBSERVED 163412 #894 `0000 4f4e0000 d8e7e630 1f 00`: no 0x5684 or 0xe324 follows).
    /// </summary>
    List<byte[]> TakeBattlePassReward(byte[] plain, long now)
    {
        if (plain.Length < 11) return [];
        var groupId = BitConverter.ToInt32(plain, 2);
        var achievementId = BitConverter.ToInt32(plain, 6);
        var step = plain[10];
        var state = achievements.GetValueOrDefault(achievementId);
        var ok = Achievements.Of(achievementId) is { } entry && state is not null && state.Count >= entry.StepCount(step) && state.RewardedStep < step;
        if (ok) { state!.RewardedStep = step; achievementDirty.Add(achievementId); }
        return [PacketWriter.Opcode(TakeBattlePassRewardRs).U16(ok ? 0 : 1).I32(groupId).I32(achievementId).U8(step).Varint(0).ToArray()];
    }
}
