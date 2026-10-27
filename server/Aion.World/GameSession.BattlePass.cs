using Aion.Protocol.Native;

namespace Aion.World;

// Battle pass (BattlePass.dat / BattlePassGroup.dat, achievements.json `battlepass`): a group is active in its activation
// window for its race (the two Light global passes 10044/10045 run 2026-09-30 → 2026-12-16, OBSERVED listed as the 21
// BattlePass-category achievements at world entry). A group's missions are the achievements of its mission group; a
// completed mission step pays its AchievementReward.RewardPoint as pass exp (OURS: at completion, not on a claim) to
// the exp achievement (type BattlePassExp, 101 steps = levels) of every pass of the group; level rewards come through
// TakeAchievementReward on that achievement, the paid pass's only after a purchase (none is sold here).
// BattlePassInfoList_NT 0xe2bd at entry, TakeBattlePassReward 0xe2bb/0xe2bc (acknowledged), BattlePassPurchase
// 0xe2c6/0xe2c7 (refused): the 0xe2 run, bodies from the usmap (CANDIDATE). The weekly exp limit is not applied.
public sealed partial class GameSession
{
    public const ushort BattlePassInfoListNt = 0xe2bd, TakeBattlePassRewardRq = 0xe2bb, TakeBattlePassRewardRs = 0xe2bc, BattlePassPurchaseRq = 0xe2c6, BattlePassPurchaseRs = 0xe2c7;

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

    /// <summary>0xe2bb TakeBattlePassReward_RQ {group, mission, step, map}: the mission's exp was paid at completion; the step is marked rewarded.</summary>
    List<byte[]> TakeBattlePassReward(byte[] plain, long now)
    {
        if (plain.Length < 11) return [];
        var groupId = BitConverter.ToInt32(plain, 2);
        var achievementId = BitConverter.ToInt32(plain, 6);
        var step = plain[10];
        var state = achievements.GetValueOrDefault(achievementId);
        var ok = Achievements.Of(achievementId) is { } entry && state is not null && state.Count >= entry.StepCount(step) && state.RewardedStep < step;
        if (ok) { state!.RewardedStep = step; achievementDirty.Add(achievementId); }
        var w = PacketWriter.Opcode(TakeBattlePassRewardRs).U16(ok ? 0 : 1).I32(groupId).I32(achievementId).U8(step).Varint(ok ? 1u : 0u);
        if (ok) w.I32(achievementId).U8(step);
        return ok ? [w.ToArray(), AchievementUpdate([achievementId]), BattlePassInfoList(now)] : [w.ToArray()];
    }
}
