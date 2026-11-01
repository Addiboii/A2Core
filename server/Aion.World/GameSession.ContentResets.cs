using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    public const ushort RewardLimitResetNt = 0x3C05, RewardLimitUpdateNt = 0x3C04, DungeonCoolTimesNt = 0x3A60;
    long characterDailyResetMs;
    long characterWeeklyResetMs;
    readonly Dictionary<int, (string Period, int Count)> rewardLimits = [];
    readonly Dictionary<int, long> dungeonCoolUntilMs = [];
    bool rewardLimitsLoaded;

    void LoadRewardLimits(IReadOnlyList<RewardLimitRecord> rows)
    {
        rewardLimits.Clear();
        foreach (var row in rows) rewardLimits[row.LimitType] = (row.Period, row.Count);
        rewardLimitsLoaded = true;
    }

    async Task FlushRewardLimitsAsync(CancellationToken ct)
    {
        if (!rewardLimitsLoaded || Store is not { } store || CharacterId is not { } character) return;
        var rows = rewardLimits.Select(kv => new RewardLimitRecord(kv.Key, kv.Value.Period, kv.Value.Count)).ToList();
        await Stored(async () => await store.SaveRewardLimitsAsync(character, rows, ct) ? [[]] : []);
    }

    void LoadDungeonCooltimes(IReadOnlyList<DungeonCooltimeRecord> rows)
    {
        dungeonCoolUntilMs.Clear();
        foreach (var row in rows) dungeonCoolUntilMs[row.DungeonId] = row.UntilMs;
    }

    async Task FlushDungeonCooltimesAsync(CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return;
        var rows = dungeonCoolUntilMs.Select(kv => new DungeonCooltimeRecord(kv.Key, kv.Value)).ToList();
        await Stored(async () => await store.SaveDungeonCooltimesAsync(character, rows, ct) ? [[]] : []);
    }

    /// <summary>Lazy resets on login / after a missed world tick while offline.</summary>
    List<byte[]> ApplyLazyPeriodicResets(long now, bool notifyClient)
    {
        var replies = new List<byte[]>();
        var daily = ContentResetSchedule.PeriodStartMs(1, now);
        var weekly = ContentResetSchedule.PeriodStartMs(2, now);
        if (daily > characterDailyResetMs)
        {
            characterDailyResetMs = daily;
            replies.AddRange(ResetDailyAchievementStates(now));
            if (notifyClient) replies.Add(RewardLimitResetPacket([1]));
        }
        if (weekly > characterWeeklyResetMs)
        {
            characterWeeklyResetMs = weekly;
            replies.AddRange(ResetWeeklyAchievementStates(now));
            if (notifyClient) replies.Add(RewardLimitResetPacket([2]));
        }
        PruneExpiredDungeonCooltimes(now);
        return replies;
    }

    List<byte[]> ResetDailyAchievementStates(long now)
    {
        var touched = new List<int>();
        foreach (var (id, state) in achievements.ToList())
        {
            if (Achievements.Of(id) is not { Reset: 1 }) continue;
            if (ContentResetSchedule.AchievementCycleStartMs(1, now) <= state.UpdatedMs) continue;
            achievements[id] = new AchievementState { UpdatedMs = now };
            achievementDirty.Add(id);
            touched.Add(id);
        }
        return touched.Count == 0 ? [] : [AchievementUpdate(touched)];
    }

    List<byte[]> ResetWeeklyAchievementStates(long now)
    {
        var touched = new List<int>();
        foreach (var (id, state) in achievements.ToList())
        {
            if (Achievements.Of(id) is not { Reset: 2 }) continue;
            if (ContentResetSchedule.AchievementCycleStartMs(2, now) <= state.UpdatedMs) continue;
            achievements[id] = new AchievementState { UpdatedMs = now };
            achievementDirty.Add(id);
            touched.Add(id);
        }
        return touched.Count == 0 ? [] : [AchievementUpdate(touched)];
    }

    void PruneExpiredDungeonCooltimes(long now)
    {
        foreach (var id in dungeonCoolUntilMs.Keys.Where(k => dungeonCoolUntilMs[k] <= now).ToList())
            dungeonCoolUntilMs.Remove(id);
    }

    static byte[] RewardLimitResetPacket(IReadOnlyList<int> types)
    {
        var w = PacketWriter.Opcode(RewardLimitResetNt).Varint((uint)types.Count);
        foreach (var t in types) w.I32(t);
        return w.ToArray();
    }

    byte[] DungeonCoolTimesList(long now)
    {
        PruneExpiredDungeonCooltimes(now);
        var w = PacketWriter.Opcode(DungeonCoolTimesNt).Varint((uint)dungeonCoolUntilMs.Count);
        foreach (var (dungeonId, until) in dungeonCoolUntilMs) w.I32(dungeonId).I64(until);
        return w.ToArray();
    }

    List<byte[]> ContentResetWorldTick(long now)
    {
        if (options.World is not { } world) return [];
        if (!world.Resets.NeedsTick(now)) return [];
        var plan = world.Resets.Plan(now);
        world.Resets.ScheduleNext(now);
        if (!plan.DailyCrossed && !plan.WeeklyCrossed) return [];
        world.Resets.MarkApplied(plan);
        foreach (var player in world.Online.Values)
            player.Send(player.Session.ApplyLazyPeriodicResets(now, notifyClient: true));
        return [];
    }

    async Task EnsureContentResetsLoadedAsync(CancellationToken ct)
    {
        if (options.World is not { } world || Store is not { } store) return;
        if (!world.ResetsLoaded)
        {
            var cursor = await store.ResetCursorsAsync(ct);
            world.Resets.Load(cursor);
            world.ResetsLoaded = true;
        }
    }

    async Task FlushContentResetWorkAsync(CancellationToken ct)
    {
        if (options.World is not { } world || Store is not { } store) return;
        if (world.Resets.PendingDailyWorldWork)
        {
            var day = ContentResetSchedule.GameDayUtc(DateTimeOffset.FromUnixTimeMilliseconds(world.Resets.Snapshot().DailyBoundaryMs));
            await store.ApplyDailyLegionResetsAsync(day, ct);
            world.Resets.ClearPendingWorldWork(daily: true, weekly: false);
        }
        if (world.Resets.PendingWeeklyWorldWork)
        {
            await store.ApplyWeeklyLegionResetsAsync(world.Resets.Snapshot().WeeklyBoundaryMs, ct);
            world.Resets.ClearPendingWorldWork(daily: false, weekly: true);
        }
        if (world.Resets.Dirty)
        {
            var snap = world.Resets.Snapshot();
            world.Resets.ClearDirty();
            await store.SaveResetCursorsAsync(snap, ct);
        }
    }
}
